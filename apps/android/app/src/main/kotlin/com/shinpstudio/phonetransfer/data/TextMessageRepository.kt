package com.shinpstudio.phonetransfer.data

import android.content.Context
import com.shinpstudio.phonetransfer.domain.TextMessageRules
import com.shinpstudio.phonetransfer.protocol.ApiError
import com.shinpstudio.phonetransfer.protocol.SendText
import com.shinpstudio.phonetransfer.protocol.TextEntry
import com.shinpstudio.phonetransfer.security.ClientIdentity
import com.shinpstudio.phonetransfer.security.PinnedTls
import java.io.IOException
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

internal class TextMessageRepository(context: Context) {
    private val appContext = context.applicationContext
    private val json = Json { ignoreUnknownKeys = false }

    suspend fun send(
        pc: SavedPc,
        kind: String,
        content: String,
        idempotencyKey: String = UUID.randomUUID().toString()
    ): TextEntry = withContext(Dispatchers.IO) {
        TextMessageRules.validate(kind, content)
        requireCanonicalUuid(idempotencyKey, "INVALID_IDEMPOTENCY_KEY")
        val payload = SendText(kind, content, idempotencyKey)
        val encoded = json.encodeToString(payload)
        if (encoded.toByteArray(Charsets.UTF_8).size > MAX_REQUEST_BYTES) {
            throw FileTransferException(
                "REQUEST_TOO_LARGE",
                false,
                "The text is too large to send."
            )
        }
        val identity = ClientIdentity.load(appContext)
        withClient(pc, identity) { client ->
            for (attempt in 0..1) {
                val request =
                    Request.Builder()
                        .url(url(pc, "api", "v1", "text"))
                        .post(encoded.toRequestBody(JSON_MEDIA))
                        .build()
                try {
                    val entry = executeJson<TextEntry>(client, request, 201)
                    validateEntry(entry, identity, kind, content)
                    return@withClient entry
                } catch (error: FileTransferException) {
                    throw error
                } catch (error: IOException) {
                    if (attempt == 1) throw error
                    // Retry the exact payload with the same idempotency key. The PC suppresses
                    // duplicate presentation when the first POST committed but its response was lost.
                }
            }
            error("UNREACHABLE")
        }
    }

    private fun validateEntry(
        entry: TextEntry,
        identity: ClientIdentity,
        expectedKind: String,
        expectedContent: String
    ) {
        requireCanonicalUuid(entry.id, "INVALID_TEXT_ID")
        requireCanonicalUuid(entry.sourceDeviceId, "INVALID_SOURCE_DEVICE_ID")
        check(entry.sourceDeviceId == identity.deviceId) { "SERVER_TEXT_MISMATCH" }
        check(entry.kind == expectedKind && entry.content == expectedContent) {
            "SERVER_TEXT_MISMATCH"
        }
        TextMessageRules.validate(entry.kind, entry.content)
        check(entry.createdAt.isNotBlank() && entry.createdAt.length <= 64) {
            "INVALID_TEXT_TIMESTAMP"
        }
    }

    private suspend inline fun <reified T> executeJson(
        client: OkHttpClient,
        request: Request,
        expectedCode: Int
    ): T {
        val response = await(client.newCall(request))
        response.use {
            if (it.code != expectedCode) throw apiError(it)
            val text = readBoundedText(it, MAX_RESPONSE_BYTES)
            return json.decodeFromString(text)
        }
    }

    private fun apiError(response: Response): FileTransferException {
        val text =
            try {
                readBoundedText(response, MAX_ERROR_RESPONSE_BYTES)
            } catch (_: Exception) {
                ""
            }
        val parsed =
            try {
                json.decodeFromString<ApiError>(text)
            } catch (_: Exception) {
                null
            }
        return if (parsed != null) {
            FileTransferException(parsed.code, parsed.retryable, parsed.message)
        } else {
            FileTransferException(
                "HTTP_${response.code}",
                response.code >= 500,
                "The PC returned an unexpected response."
            )
        }
    }

    private fun readBoundedText(response: Response, maximumBytes: Int): String {
        val body =
            response.body
                ?: throw FileTransferException(
                    "EMPTY_RESPONSE",
                    true,
                    "The response body was empty."
                )
        if (body.contentLength() > maximumBytes) {
            throw FileTransferException(
                "RESPONSE_TOO_LARGE",
                false,
                "The PC response exceeded the allowed size."
            )
        }
        val source = body.source()
        if (source.request(maximumBytes.toLong() + 1)) {
            throw FileTransferException(
                "RESPONSE_TOO_LARGE",
                false,
                "The PC response exceeded the allowed size."
            )
        }
        return source.readUtf8()
    }

    private suspend fun await(call: Call): Response = suspendCancellableCoroutine { continuation ->
        continuation.invokeOnCancellation { call.cancel() }
        call.enqueue(
            object : Callback {
                override fun onFailure(call: Call, error: IOException) {
                    if (continuation.isActive) {
                        continuation.resumeWithException(
                            IOException("CONNECTION_FAILED", error)
                        )
                    }
                }

                override fun onResponse(call: Call, response: Response) {
                    if (continuation.isActive) {
                        continuation.resume(response)
                    } else {
                        response.close()
                    }
                }
            }
        )
    }

    private suspend fun <T> withClient(
        pc: SavedPc,
        identity: ClientIdentity,
        block: suspend (OkHttpClient) -> T
    ): T {
        val client =
            PinnedTls.client(pc.lastKnownEndpoint, pc.pin, identity)
                .newBuilder()
                .callTimeout(20, TimeUnit.SECONDS)
                .readTimeout(20, TimeUnit.SECONDS)
                .writeTimeout(20, TimeUnit.SECONDS)
                .build()
        try {
            return block(client)
        } finally {
            client.dispatcher.cancelAll()
            client.connectionPool.evictAll()
            client.dispatcher.executorService.shutdown()
        }
    }

    private fun url(pc: SavedPc, vararg segments: String): HttpUrl {
        val builder = pc.lastKnownEndpoint.toHttpUrl().newBuilder()
        segments.forEach(builder::addPathSegment)
        return builder.build()
    }

    private fun requireCanonicalUuid(value: String, code: String) {
        try {
            require(UUID.fromString(value).toString() == value && UUID.fromString(value) != UUID(0, 0)) {
                code
            }
        } catch (error: IllegalArgumentException) {
            throw FileTransferException(code, false, "The UUID value is invalid.", error)
        }
    }

    companion object {
        private const val MAX_REQUEST_BYTES = 128 * 1024
        private const val MAX_RESPONSE_BYTES = 128 * 1024
        private const val MAX_ERROR_RESPONSE_BYTES = 128 * 1024
        private val JSON_MEDIA = "application/json".toMediaType()
    }
}
