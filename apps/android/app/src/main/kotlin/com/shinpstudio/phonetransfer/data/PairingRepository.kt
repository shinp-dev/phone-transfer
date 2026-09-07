package com.shinpstudio.phonetransfer.data

import android.content.Context
import android.util.AtomicFile
import com.shinpstudio.phonetransfer.protocol.PairingRequest
import com.shinpstudio.phonetransfer.protocol.PairingStatus
import com.shinpstudio.phonetransfer.protocol.ServerInfo
import com.shinpstudio.phonetransfer.security.ClientIdentity
import com.shinpstudio.phonetransfer.security.PairingInvitation
import com.shinpstudio.phonetransfer.security.PairingProof
import com.shinpstudio.phonetransfer.security.PinnedTls
import java.io.File
import java.io.IOException
import java.time.Instant
import java.util.Base64
import java.util.UUID
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

@Serializable
data class SavedPc(
    val deviceId: String,
    val displayName: String,
    val endpoint: String,
    val pin: String
)

class PairingRepository(private val context: Context) {
    private val json = Json { ignoreUnknownKeys = false }
    private val file = AtomicFile(File(context.filesDir, "paired-pcs.json"))

    suspend fun saved(): List<SavedPc> = withContext(Dispatchers.IO) { readSaved() }

    @Synchronized
    private fun readSaved(): List<SavedPc> {
        if (!file.baseFile.exists()) return emptyList()
        val text = file.openRead().use { it.readBytes().toString(Charsets.UTF_8) }
        return json.decodeFromString<List<SavedPc>>(text).also { pcs ->
            check(pcs.size <= 100 && pcs.map { it.deviceId }.distinct().size == pcs.size)
            pcs.forEach {
                check(UUID.fromString(it.deviceId).toString() == it.deviceId)
                PairingInvitation.lanEndpoint(it.endpoint)
                check(it.pin.matches(Regex("[a-f0-9]{64}")))
            }
        }
    }

    @Synchronized
    private fun writeSaved(pcs: List<SavedPc>) {
        val output = file.startWrite()
        try {
            output.write(json.encodeToString(pcs).toByteArray(Charsets.UTF_8))
            file.finishWrite(output)
        } catch (error: Exception) {
            file.failWrite(output)
            throw error
        }
    }

    suspend fun forget(id: String) = withContext(Dispatchers.IO) {
        writeSaved(readSaved().filterNot { it.deviceId == id })
    }

    suspend fun connect(pc: SavedPc): ServerInfo = withContext(Dispatchers.IO) {
        val identity = ClientIdentity.load(context)
        val client = PinnedTls.client(pc.endpoint, pc.pin, identity)
        try {
            val request = Request.Builder().url("${pc.endpoint}/api/v1/info").build()
            val info = json.decodeFromString<ServerInfo>(execute(client, request, 200))
            check(info.deviceId == pc.deviceId && info.protocolVersion == 1L) {
                "SERVER_IDENTITY_MISMATCH"
            }
            info
        } finally { close(client) }
    }

    suspend fun pair(
        payload: String,
        onCode: suspend (String) -> Unit
    ): SavedPc = withContext(Dispatchers.IO) {
        val invitation = PairingInvitation.parse(payload, Instant.now())
        val pcs = readSaved()
        check(pcs.none { it.deviceId == invitation.deviceId }) { "PC_ALREADY_SAVED" }
        check(pcs.size < 100) { "PC_LIMIT" }
        val identity = ClientIdentity.load(context)
        val transcript = PairingProof.transcript(
            identity.deviceId, "Android", invitation.token, identity.certificate.encoded
        )
        val request = PairingRequest(identity.deviceId, "Android", invitation.token,
            Base64.getEncoder().encodeToString(identity.certificate.encoded),
            PairingProof.sign(identity.key, transcript))
        val bootstrap = invitation.endpoint.toString()
        val client = PinnedTls.client(bootstrap, invitation.serverSpkiSha256)
        try {
            withTimeout(120_000) {
                val route = "${invitation.endpoint}/pairing/v1/requests"
                val encoded = json.encodeToString(request)
                val body = encoded.toRequestBody("application/json".toMediaType())
                val submission = Request.Builder().url(route).post(body).build()
                val submitted = execute(client, submission, 202)
                var status = json.decodeFromString<PairingStatus>(submitted)
                val requestId = status.requestId
                check(UUID.fromString(requestId).toString() == requestId)
                onCode(PairingProof.comparisonCode(transcript))
                val proof = PairingProof.sign(
                    identity.key, "phone-transfer/pairing-status/v1\n$requestId"
                )
                while (status.status == "pending") {
                    delay(1500)
                    val poll = Request.Builder().url("$route/$requestId")
                        .header("X-Pairing-Proof", proof).build()
                    val result = execute(client, poll, 200)
                    status = json.decodeFromString<PairingStatus>(result)
                    check(status.requestId == requestId)
                }
                check(status.status == "approved") { "PAIRING_DENIED_OR_EXPIRED" }
                val pc = SavedPc(
                    invitation.deviceId, invitation.displayName,
                    invitation.apiEndpoint.toString(), invitation.serverSpkiSha256
                )
                connect(pc)
                ensureActive()
                writeSaved(pcs + pc)
                pc
            }
        } finally { close(client) }
    }

    private fun close(client: OkHttpClient) {
        client.dispatcher.cancelAll()
        client.connectionPool.evictAll()
        client.dispatcher.executorService.shutdown()
    }

    private suspend fun execute(
        client: OkHttpClient,
        request: Request,
        expected: Int
    ): String = suspendCancellableCoroutine { continuation ->
        val call = client.newCall(request)
        continuation.invokeOnCancellation { call.cancel() }
        call.enqueue(object : Callback {
            override fun onFailure(call: Call, error: IOException) {
                if (continuation.isActive) {
                    continuation.resumeWithException(IOException("CONNECTION_FAILED"))
                }
            }
            override fun onResponse(call: Call, response: Response) {
                try {
                    val result = response.use {
                        check(it.code == expected) { "HTTP_${it.code}" }
                        val source = it.body?.source() ?: error("EMPTY_RESPONSE")
                        check(!source.request(131_073)) { "RESPONSE_TOO_LARGE" }
                        source.readUtf8()
                    }
                    if (continuation.isActive) continuation.resume(result)
                } catch (error: Exception) {
                    if (continuation.isActive) continuation.resumeWithException(error)
                }
            }
        })
    }
}
