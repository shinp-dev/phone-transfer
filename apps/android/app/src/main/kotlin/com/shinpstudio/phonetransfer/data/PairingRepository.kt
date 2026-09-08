package com.shinpstudio.phonetransfer.data

import android.content.Context
import com.shinpstudio.phonetransfer.protocol.PairingRequest
import com.shinpstudio.phonetransfer.protocol.PairingStatus
import com.shinpstudio.phonetransfer.protocol.ServerInfo
import com.shinpstudio.phonetransfer.security.ClientIdentity
import com.shinpstudio.phonetransfer.security.PairingInvitation
import com.shinpstudio.phonetransfer.security.PairingProof
import com.shinpstudio.phonetransfer.security.PinnedTls
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
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

class PairingRepository(context: Context) {
    private val appContext = context.applicationContext
    private val json = Json { ignoreUnknownKeys = false }
    private val savedPcs = SavedPcStore.get(appContext)

    suspend fun saved(): List<SavedPc> = withContext(Dispatchers.IO) { savedPcs.read() }

    suspend fun forget(id: String) = withContext(Dispatchers.IO) {
        savedPcs.remove(id)
    }

    suspend fun connect(pc: SavedPc): ServerInfo = withContext(Dispatchers.IO) {
        val identity = ClientIdentity.load(appContext)
        val client = PinnedTls.client(pc.lastKnownEndpoint, pc.pin, identity)
        try {
            val request = Request.Builder().url("${pc.lastKnownEndpoint}/api/v1/info").build()
            val info = json.decodeFromString<ServerInfo>(execute(client, request, 200))
            check(info.deviceId == pc.deviceId && info.protocolVersion == 1L) {
                "SERVER_IDENTITY_MISMATCH"
            }
            info
        } finally {
            close(client)
        }
    }

    suspend fun acceptDiscovery(discovered: DiscoveredPc): SavedPc? = withContext(Dispatchers.IO) {
        val current =
            savedPcs.read().firstOrNull { it.deviceId == discovered.deviceId }
                ?: return@withContext null
        if (current.lastKnownEndpoint == discovered.endpoint) return@withContext current
        val candidate = current.copy(lastKnownEndpoint = discovered.endpoint)
        connect(candidate)
        ensureActive()
        savedPcs.updateEndpoint(current.deviceId, discovered.endpoint)
            .firstOrNull { it.deviceId == current.deviceId }
    }

    suspend fun pair(payload: String, onCode: suspend (String) -> Unit): SavedPc =
        withContext(Dispatchers.IO) {
            val invitation = PairingInvitation.parse(payload, Instant.now())
            val beforePairing = savedPcs.read()
            check(beforePairing.none { it.deviceId == invitation.deviceId }) { "PC_ALREADY_SAVED" }
            check(beforePairing.size < SavedPcRules.LIMIT) { "PC_LIMIT" }
            val identity = ClientIdentity.load(appContext)
            val transcript = PairingProof.transcript(
                identity.deviceId,
                "Android",
                invitation.token,
                identity.certificate.encoded
            )
            val request = PairingRequest(
                identity.deviceId,
                "Android",
                invitation.token,
                Base64.getEncoder().encodeToString(identity.certificate.encoded),
                PairingProof.sign(identity.key, transcript)
            )
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
                        identity.key,
                        "phone-transfer/pairing-status/v1\n$requestId"
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
                        invitation.deviceId,
                        invitation.displayName,
                        invitation.apiEndpoint.toString(),
                        invitation.serverSpkiSha256
                    )
                    connect(pc)
                    ensureActive()
                    savedPcs.add(pc)
                    pc
                }
            } finally {
                close(client)
            }
        }

    private fun close(client: OkHttpClient) {
        client.dispatcher.cancelAll()
        client.connectionPool.evictAll()
        client.dispatcher.executorService.shutdown()
    }

    private suspend fun execute(client: OkHttpClient, request: Request, expected: Int): String =
        suspendCancellableCoroutine { continuation ->
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
