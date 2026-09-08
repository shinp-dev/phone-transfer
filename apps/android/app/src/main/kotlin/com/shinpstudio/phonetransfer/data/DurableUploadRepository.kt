package com.shinpstudio.phonetransfer.data

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import com.shinpstudio.phonetransfer.domain.RemotePathRules
import com.shinpstudio.phonetransfer.domain.TransferWireRules
import com.shinpstudio.phonetransfer.protocol.ApiError
import com.shinpstudio.phonetransfer.protocol.CreateTransfer
import com.shinpstudio.phonetransfer.protocol.Transfer
import com.shinpstudio.phonetransfer.security.ClientIdentity
import com.shinpstudio.phonetransfer.security.PinnedTls
import java.io.IOException
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
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

data class DurableUploadSource(val name: String, val size: Long, val sha256: String)

class DurableUploadRepository(context: Context) {
    private val appContext = context.applicationContext
    private val resolver = appContext.contentResolver
    private val json = Json { ignoreUnknownKeys = false }

    suspend fun inspectSource(uri: Uri): DurableUploadSource = withContext(Dispatchers.IO) {
        val name = queryDisplayName(uri)
        try {
            RemotePathRules.validateName(name)
        } catch (error: IllegalArgumentException) {
            throw FileTransferException(
                "INVALID_SOURCE_NAME",
                false,
                "The selected filename cannot be used on Windows.",
                error
            )
        }

        val digest = MessageDigest.getInstance("SHA-256")
        var size = 0L
        val input = resolver.openInputStream(uri)
            ?: throw FileTransferException(
                "SOURCE_UNAVAILABLE",
                false,
                "The selected source cannot be opened."
            )
        input.use { stream ->
            val buffer = ByteArray(TransferWireRules.CHUNK_BYTES)
            while (true) {
                currentCoroutineContext().ensureActive()
                val count = stream.read(buffer)
                if (count < 0) break
                if (count == 0) continue
                size = Math.addExact(size, count.toLong())
                if (size > TransferWireRules.MAX_FILE_BYTES) {
                    throw FileTransferException(
                        "FILE_TOO_LARGE",
                        false,
                        "The selected file exceeds the transfer limit."
                    )
                }
                digest.update(buffer, 0, count)
            }
        }
        DurableUploadSource(name, size, digest.digest().toLowerHex())
    }

    suspend fun createOrRecover(
        pc: SavedPc,
        shareId: String,
        directory: String,
        source: DurableUploadSource,
        idempotencyKey: String
    ): Transfer = withContext(Dispatchers.IO) {
        requireCanonicalUuid(shareId, "INVALID_SHARE_ID")
        requireCanonicalUuid(idempotencyKey, "INVALID_IDEMPOTENCY_KEY")
        RemotePathRules.validate(directory, allowRoot = true)
        RemotePathRules.validateName(source.name)
        val destination = RemotePathRules.join(directory, source.name)
        val create =
            CreateTransfer(shareId, destination, source.size, source.sha256, idempotencyKey)

        withClient(pc) { client ->
            val encoded = json.encodeToString(create)
            for (attempt in 0..1) {
                val request = Request.Builder()
                    .url(url(pc, "api", "v1", "transfers"))
                    .post(encoded.toRequestBody(JSON_MEDIA))
                    .build()
                try {
                    val transfer = executeJson<Transfer>(client, request, 201)
                    validateTransfer(transfer, source)
                    return@withClient transfer
                } catch (error: FileTransferException) {
                    throw error
                } catch (error: IOException) {
                    if (attempt == 1) throw error
                }
            }
            error("UNREACHABLE")
        }
    }

    suspend fun status(pc: SavedPc, transferId: String): Transfer = withContext(Dispatchers.IO) {
        requireCanonicalUuid(transferId, "INVALID_TRANSFER_ID")
        withClient(pc) { client -> getTransfer(client, pc, transferId) }
    }

    suspend fun resume(
        pc: SavedPc,
        transferId: String,
        sourceUri: Uri,
        source: DurableUploadSource,
        startOffset: Long,
        onProgress: (Long, Long) -> Unit,
        onCommitted: (Long) -> Unit
    ): Transfer = withContext(Dispatchers.IO) {
        requireCanonicalUuid(transferId, "INVALID_TRANSFER_ID")
        require(startOffset in 0..source.size) { "INVALID_RESUME_OFFSET" }
        onProgress(startOffset, source.size)

        withClient(pc) { client ->
            var offset = startOffset
            if (offset < source.size) {
                val input = resolver.openInputStream(sourceUri)
                    ?: throw FileTransferException(
                        "SOURCE_UNAVAILABLE",
                        false,
                        "The selected source cannot be reopened."
                    )
                input.use { stream ->
                    val buffer = ByteArray(TransferWireRules.CHUNK_BYTES)
                    discardExactly(stream, buffer, offset)
                    while (offset < source.size) {
                        currentCoroutineContext().ensureActive()
                        val count = stream.read(
                            buffer,
                            0,
                            minOf(buffer.size.toLong(), source.size - offset).toInt()
                        )
                        if (count <= 0) {
                            throw FileTransferException(
                                "SOURCE_CHANGED",
                                false,
                                "The selected source ended before upload completed."
                            )
                        }
                        val expected = TransferWireRules.expectedOffset(offset, count, source.size)
                        val updated = appendWithStatusRecovery(
                            client,
                            pc,
                            transferId,
                            offset,
                            buffer.copyOf(count),
                            expected
                        )
                        validateTransfer(updated, source)
                        offset = updated.transferredBytes
                        onCommitted(offset)
                        onProgress(offset, source.size)
                    }
                    if (stream.read() != -1) {
                        throw FileTransferException(
                            "SOURCE_CHANGED",
                            false,
                            "The selected source changed while it was being uploaded."
                        )
                    }
                }
            }

            val result = completeWithStatusRecovery(client, pc, transferId)
            validateTransfer(result, source)
            if (result.status != "completed") {
                throw FileTransferException(
                    "UNEXPECTED_TRANSFER_STATE",
                    true,
                    "The server did not complete the upload."
                )
            }
            onProgress(source.size, source.size)
            result
        }
    }

    suspend fun cancel(pc: SavedPc, transferId: String) = withContext(Dispatchers.IO) {
        requireCanonicalUuid(transferId, "INVALID_TRANSFER_ID")
        withClient(pc) { client ->
            val request = Request.Builder()
                .url(url(pc, "api", "v1", "transfers", transferId))
                .delete()
                .build()
            executeJson<Transfer>(client, request, 200)
        }
    }

    private suspend fun appendWithStatusRecovery(
        client: OkHttpClient,
        pc: SavedPc,
        transferId: String,
        offset: Long,
        bytes: ByteArray,
        expectedOffset: Long
    ): Transfer {
        val request = Request.Builder()
            .url(url(pc, "api", "v1", "transfers", transferId, "content"))
            .header("Upload-Offset", offset.toString())
            .patch(bytes.toRequestBody(OCTET_MEDIA))
            .build()
        val updated = try {
            executeJson<Transfer>(client, request, 200)
        } catch (error: IOException) {
            val status = try {
                getTransfer(client, pc, transferId)
            } catch (_: Exception) {
                null
            }
            if (status?.transferredBytes == expectedOffset) status else throw error
        }
        if (updated.transferredBytes != expectedOffset) {
            throw FileTransferException(
                "SERVER_OFFSET_MISMATCH",
                true,
                "The server committed an unexpected upload offset."
            )
        }
        return updated
    }

    private suspend fun completeWithStatusRecovery(
        client: OkHttpClient,
        pc: SavedPc,
        transferId: String
    ): Transfer {
        val request = Request.Builder()
            .url(url(pc, "api", "v1", "transfers", transferId, "complete"))
            .post(ByteArray(0).toRequestBody())
            .build()
        return try {
            executeJson(client, request, 200)
        } catch (error: IOException) {
            val status = try {
                getTransfer(client, pc, transferId)
            } catch (_: Exception) {
                null
            }
            if (status?.status == "completed") status else throw error
        }
    }

    private suspend fun getTransfer(
        client: OkHttpClient,
        pc: SavedPc,
        transferId: String
    ): Transfer {
        val request = Request.Builder()
            .url(url(pc, "api", "v1", "transfers", transferId))
            .get()
            .build()
        return executeJson(client, request, 200)
    }

    private suspend fun discardExactly(input: java.io.InputStream, buffer: ByteArray, count: Long) {
        var remaining = count
        while (remaining > 0) {
            currentCoroutineContext().ensureActive()
            val read = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
            if (read <= 0) {
                throw FileTransferException(
                    "SOURCE_CHANGED",
                    false,
                    "The selected source is shorter than the committed server offset."
                )
            }
            remaining -= read
        }
    }

    private fun queryDisplayName(uri: Uri): String {
        resolver.query(
            uri,
            arrayOf(OpenableColumns.DISPLAY_NAME),
            null,
            null,
            null
        )?.use { cursor ->
            val index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            if (index >= 0 && cursor.moveToFirst()) {
                val value = cursor.getString(index)
                if (!value.isNullOrBlank()) return value
            }
        }
        throw FileTransferException(
            "SOURCE_NAME_UNAVAILABLE",
            false,
            "The selected document has no usable display name."
        )
    }

    private fun validateTransfer(transfer: Transfer, source: DurableUploadSource) {
        requireCanonicalUuid(transfer.transferId, "INVALID_TRANSFER_ID")
        if (
            transfer.fileName != source.name ||
            transfer.totalSize != source.size ||
            transfer.sha256 != source.sha256 ||
            transfer.transferredBytes !in 0..source.size
        ) {
            throw FileTransferException(
                "SERVER_TRANSFER_MISMATCH",
                false,
                "The server transfer metadata does not match the persisted upload."
            )
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
            val text = readBoundedText(it, MAX_JSON_RESPONSE_BYTES)
            return json.decodeFromString(text)
        }
    }

    private fun apiError(response: Response): FileTransferException {
        val text = try {
            readBoundedText(response, MAX_ERROR_RESPONSE_BYTES)
        } catch (_: Exception) {
            ""
        }
        val parsed = try {
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
        val body = response.body
            ?: throw FileTransferException("EMPTY_RESPONSE", true, "The response body was empty.")
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
        call.enqueue(object : Callback {
            override fun onFailure(call: Call, error: IOException) {
                if (continuation.isActive) {
                    continuation.resumeWithException(IOException("CONNECTION_FAILED", error))
                }
            }

            override fun onResponse(call: Call, response: Response) {
                if (continuation.isActive) continuation.resume(response) else response.close()
            }
        })
    }

    private suspend fun <T> withClient(pc: SavedPc, block: suspend (OkHttpClient) -> T): T {
        val identity = ClientIdentity.load(appContext)
        val client = PinnedTls.client(pc.lastKnownEndpoint, pc.pin, identity)
            .newBuilder()
            .callTimeout(0, TimeUnit.MILLISECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .writeTimeout(30, TimeUnit.SECONDS)
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
            require(UUID.fromString(value).toString() == value) { code }
        } catch (error: IllegalArgumentException) {
            throw FileTransferException(code, false, "The UUID value is invalid.", error)
        }
    }

    private fun ByteArray.toLowerHex(): String {
        val chars = CharArray(size * 2)
        forEachIndexed { index, byte ->
            val value = byte.toInt() and 0xff
            chars[index * 2] = HEX[value ushr 4]
            chars[index * 2 + 1] = HEX[value and 0x0f]
        }
        return chars.concatToString()
    }

    companion object {
        private const val MAX_JSON_RESPONSE_BYTES = 2 * 1024 * 1024
        private const val MAX_ERROR_RESPONSE_BYTES = 128 * 1024
        private val JSON_MEDIA = "application/json".toMediaType()
        private val OCTET_MEDIA = "application/octet-stream".toMediaType()
        private val HEX = "0123456789abcdef".toCharArray()
    }
}
