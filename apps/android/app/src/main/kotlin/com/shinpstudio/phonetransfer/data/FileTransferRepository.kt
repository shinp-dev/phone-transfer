package com.shinpstudio.phonetransfer.data

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import com.shinpstudio.phonetransfer.domain.RemotePathRules
import com.shinpstudio.phonetransfer.domain.TransferWireRules
import com.shinpstudio.phonetransfer.protocol.ApiError
import com.shinpstudio.phonetransfer.protocol.CreateTransfer
import com.shinpstudio.phonetransfer.protocol.FileEntry
import com.shinpstudio.phonetransfer.protocol.FileList
import com.shinpstudio.phonetransfer.protocol.Share
import com.shinpstudio.phonetransfer.protocol.ShareList
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
import kotlinx.coroutines.NonCancellable
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

class FileTransferException(
    val code: String,
    val retryable: Boolean,
    message: String,
    cause: Throwable? = null
) : IOException(message, cause)

data class DownloadResult(val fileName: String, val size: Long, val sha256: String)

class FileTransferRepository(context: Context) {
    private val appContext = context.applicationContext
    private val resolver = appContext.contentResolver
    private val json = Json { ignoreUnknownKeys = false }

    suspend fun listShares(pc: SavedPc): List<Share> = withContext(Dispatchers.IO) {
        withClient(pc) { client ->
            val request = Request.Builder().url(url(pc, "api", "v1", "shares")).get().build()
            val result = executeJson<ShareList>(client, request, 200)
            require(result.items.size <= MAX_SHARES) { "TOO_MANY_SHARES" }
            result.items.onEach(::validateShare)
        }
    }

    suspend fun listEntries(pc: SavedPc, shareId: String, path: String): List<FileEntry> =
        withContext(Dispatchers.IO) {
            RemotePathRules.validate(path, allowRoot = true)
            requireCanonicalUuid(shareId, "INVALID_SHARE_ID")
            withClient(pc) { client ->
                val target = url(pc, "api", "v1", "shares", shareId, "entries").newBuilder()
                    .addQueryParameter("path", path)
                    .build()
                val request = Request.Builder().url(target).get().build()
                val result = executeJson<FileList>(client, request, 200)
                check(result.nextCursor.isEmpty()) { "UNEXPECTED_CURSOR" }
                check(result.items.size <= MAX_ENTRIES) { "TOO_MANY_ENTRIES" }
                result.items.onEach(::validateEntry)
            }
        }

    suspend fun upload(
        pc: SavedPc,
        shareId: String,
        directory: String,
        sourceUri: Uri,
        onProgress: (Long, Long) -> Unit
    ): Transfer = withContext(Dispatchers.IO) {
        requireCanonicalUuid(shareId, "INVALID_SHARE_ID")
        RemotePathRules.validate(directory, allowRoot = true)
        val source = inspectSource(sourceUri)
        val destination = RemotePathRules.join(directory, source.name)
        onProgress(0, source.size)

        withClient(pc) { client ->
            var transferId: String? = null
            var completed = false
            try {
                val create = CreateTransfer(
                    shareId,
                    destination,
                    source.size,
                    source.sha256,
                    UUID.randomUUID().toString()
                )
                val created = executeJson<Transfer>(
                    client,
                    Request.Builder()
                        .url(url(pc, "api", "v1", "transfers"))
                        .post(json.encodeToString(create).toRequestBody(JSON_MEDIA))
                        .build(),
                    201
                )
                validateTransfer(created, source.size, source.sha256)
                transferId = created.transferId

                var offset = created.transferredBytes
                check(offset == 0L) { "UNEXPECTED_INITIAL_OFFSET" }
                val buffer = ByteArray(TransferWireRules.CHUNK_BYTES)
                val input = resolver.openInputStream(sourceUri)
                    ?: throw FileTransferException("SOURCE_UNAVAILABLE", false, "The selected source cannot be opened.")
                input.use { stream ->
                    while (offset < source.size) {
                        currentCoroutineContext().ensureActive()
                        val count = stream.read(buffer, 0, minOf(buffer.size.toLong(), source.size - offset).toInt())
                        if (count <= 0) {
                            throw FileTransferException("SOURCE_CHANGED", false, "The selected source ended before upload completed.")
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
                        offset = updated.transferredBytes
                        onProgress(offset, source.size)
                    }
                    if (stream.read() != -1) {
                        throw FileTransferException("SOURCE_CHANGED", false, "The selected source changed while it was being uploaded.")
                    }
                }

                val result = completeWithStatusRecovery(client, pc, transferId)
                check(result.status == "completed") { "UNEXPECTED_TRANSFER_STATE" }
                completed = true
                onProgress(source.size, source.size)
                result
            } catch (error: Exception) {
                if (transferId != null && !completed) {
                    withContext(NonCancellable) {
                        try {
                            cancelTransfer(client, pc, transferId)
                        } catch (_: Exception) {
                            // Server-side shutdown/recovery cleanup remains the final fallback.
                        }
                    }
                }
                throw error
            }
        }
    }

    suspend fun download(
        pc: SavedPc,
        shareId: String,
        remotePath: String,
        destinationUri: Uri,
        onProgress: (Long, Long) -> Unit
    ): DownloadResult = withContext(Dispatchers.IO) {
        requireCanonicalUuid(shareId, "INVALID_SHARE_ID")
        RemotePathRules.validate(remotePath)
        withClient(pc) { client ->
            val target = url(pc, "api", "v1", "shares", shareId, "content").newBuilder()
                .addQueryParameter("path", remotePath)
                .build()
            val call = client.newCall(Request.Builder().url(target).get().build())
            val response = await(call)
            var destinationOpened = false
            try {
                response.use {
                    if (it.code != 200) throw apiError(it)
                    val body = it.body ?: throw FileTransferException("EMPTY_RESPONSE", true, "The download response was empty.")
                    val length = body.contentLength()
                    if (length !in 0..TransferWireRules.MAX_FILE_BYTES) {
                        throw FileTransferException("INVALID_DOWNLOAD_SIZE", false, "The download size is invalid.")
                    }
                    val expectedHash = try {
                        TransferWireRules.strongSha256Etag(it.header("ETag"))
                    } catch (error: IllegalArgumentException) {
                        throw FileTransferException("INVALID_ETAG", false, "The download ETag is invalid.", error)
                    }
                    val output = resolver.openOutputStream(destinationUri, "w")
                        ?: throw FileTransferException("DESTINATION_UNAVAILABLE", false, "The selected destination cannot be opened.")
                    destinationOpened = true
                    val digest = MessageDigest.getInstance("SHA-256")
                    var written = 0L
                    val buffer = ByteArray(TransferWireRules.CHUNK_BYTES)
                    body.byteStream().use { input ->
                        output.use { destination ->
                            while (true) {
                                currentCoroutineContext().ensureActive()
                                val count = input.read(buffer)
                                if (count < 0) break
                                if (count == 0) continue
                                written = Math.addExact(written, count.toLong())
                                if (written > length || written > TransferWireRules.MAX_FILE_BYTES) {
                                    throw FileTransferException("DOWNLOAD_SIZE_MISMATCH", false, "The download exceeded its declared size.")
                                }
                                destination.write(buffer, 0, count)
                                digest.update(buffer, 0, count)
                                onProgress(written, length)
                            }
                            destination.flush()
                        }
                    }
                    if (written != length) {
                        throw FileTransferException("DOWNLOAD_SIZE_MISMATCH", true, "The download ended before all bytes arrived.")
                    }
                    val actualHash = digest.digest().toLowerHex()
                    if (actualHash != expectedHash) {
                        throw FileTransferException("DOWNLOAD_HASH_MISMATCH", false, "The downloaded bytes failed SHA-256 verification.")
                    }
                    DownloadResult(RemotePathRules.fileName(remotePath), written, actualHash)
                }
            } catch (error: Exception) {
                if (destinationOpened) clearDestinationBestEffort(destinationUri)
                throw error
            } finally {
                call.cancel()
            }
        }
    }

    private suspend fun inspectSource(uri: Uri): SourceDescriptor {
        val name = queryDisplayName(uri)
        try {
            RemotePathRules.validateName(name)
        } catch (error: IllegalArgumentException) {
            throw FileTransferException("INVALID_SOURCE_NAME", false, "The selected filename cannot be used on Windows.", error)
        }

        val digest = MessageDigest.getInstance("SHA-256")
        var size = 0L
        val input = resolver.openInputStream(uri)
            ?: throw FileTransferException("SOURCE_UNAVAILABLE", false, "The selected source cannot be opened.")
        input.use { stream ->
            val buffer = ByteArray(TransferWireRules.CHUNK_BYTES)
            while (true) {
                currentCoroutineContext().ensureActive()
                val count = stream.read(buffer)
                if (count < 0) break
                if (count == 0) continue
                size = Math.addExact(size, count.toLong())
                if (size > TransferWireRules.MAX_FILE_BYTES) {
                    throw FileTransferException("FILE_TOO_LARGE", false, "The selected file exceeds the transfer limit.")
                }
                digest.update(buffer, 0, count)
            }
        }
        return SourceDescriptor(name, size, digest.digest().toLowerHex())
    }

    private fun queryDisplayName(uri: Uri): String {
        resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
            val index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            if (index >= 0 && cursor.moveToFirst()) {
                val value = cursor.getString(index)
                if (!value.isNullOrBlank()) return value
            }
        }
        throw FileTransferException("SOURCE_NAME_UNAVAILABLE", false, "The selected document has no usable display name.")
    }

    private suspend fun appendWithStatusRecovery(
        client: OkHttpClient,
        pc: SavedPc,
        transferId: String,
        offset: Long,
        bytes: ByteArray,
        expectedOffset: Long
    ): Transfer {
        val target = url(pc, "api", "v1", "transfers", transferId, "content")
        val request = Request.Builder()
            .url(target)
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
            throw FileTransferException("SERVER_OFFSET_MISMATCH", true, "The server committed an unexpected upload offset.")
        }
        return updated
    }

    private suspend fun completeWithStatusRecovery(client: OkHttpClient, pc: SavedPc, transferId: String): Transfer {
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

    private suspend fun getTransfer(client: OkHttpClient, pc: SavedPc, transferId: String): Transfer {
        val request = Request.Builder().url(url(pc, "api", "v1", "transfers", transferId)).get().build()
        return executeJson(client, request, 200)
    }

    private suspend fun cancelTransfer(client: OkHttpClient, pc: SavedPc, transferId: String) {
        val request = Request.Builder().url(url(pc, "api", "v1", "transfers", transferId)).delete().build()
        executeJson<Transfer>(client, request, 200)
    }

    private fun validateShare(share: Share) {
        requireCanonicalUuid(share.id, "INVALID_SHARE_ID")
        check(share.name.length <= 128) { "INVALID_SHARE_NAME" }
    }

    private fun validateEntry(entry: FileEntry) {
        check(entry.kind == "file" || entry.kind == "directory") { "INVALID_ENTRY_KIND" }
        RemotePathRules.validateName(entry.name)
        RemotePathRules.validate(entry.relativePath)
        check(entry.relativePath.substringAfterLast('/') == entry.name) { "INVALID_ENTRY_PATH" }
        check(entry.size >= 0) { "INVALID_ENTRY_SIZE" }
    }

    private fun validateTransfer(transfer: Transfer, expectedSize: Long, expectedHash: String) {
        requireCanonicalUuid(transfer.transferId, "INVALID_TRANSFER_ID")
        check(transfer.totalSize == expectedSize && transfer.sha256 == expectedHash) { "SERVER_TRANSFER_MISMATCH" }
        check(transfer.transferredBytes in 0..transfer.totalSize) { "SERVER_TRANSFER_MISMATCH" }
    }

    private suspend inline fun <reified T> executeJson(client: OkHttpClient, request: Request, expectedCode: Int): T {
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
            FileTransferException("HTTP_${response.code}", response.code >= 500, "The PC returned an unexpected response.")
        }
    }

    private fun readBoundedText(response: Response, maximumBytes: Int): String {
        val body = response.body ?: throw FileTransferException("EMPTY_RESPONSE", true, "The response body was empty.")
        if (body.contentLength() > maximumBytes) {
            throw FileTransferException("RESPONSE_TOO_LARGE", false, "The PC response exceeded the allowed size.")
        }
        val source = body.source()
        if (source.request(maximumBytes.toLong() + 1)) {
            throw FileTransferException("RESPONSE_TOO_LARGE", false, "The PC response exceeded the allowed size.")
        }
        return source.readUtf8()
    }

    private suspend fun await(call: Call): Response = suspendCancellableCoroutine { continuation ->
        continuation.invokeOnCancellation { call.cancel() }
        call.enqueue(object : Callback {
            override fun onFailure(call: Call, error: IOException) {
                if (continuation.isActive) continuation.resumeWithException(IOException("CONNECTION_FAILED", error))
            }

            override fun onResponse(call: Call, response: Response) {
                if (continuation.isActive) continuation.resume(response) else response.close()
            }
        })
    }

    private suspend fun <T> withClient(pc: SavedPc, block: suspend (OkHttpClient) -> T): T {
        val identity = ClientIdentity.load(appContext)
        val client = PinnedTls.client(pc.lastKnownEndpoint, pc.pin, identity).newBuilder()
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

    private fun clearDestinationBestEffort(uri: Uri) {
        try {
            resolver.openOutputStream(uri, "w")?.use { }
        } catch (_: Exception) {
            // Some SAF providers cannot truncate a partially written destination. Never claim success in that case.
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

    private data class SourceDescriptor(val name: String, val size: Long, val sha256: String)

    companion object {
        private const val MAX_SHARES = 16
        private const val MAX_ENTRIES = 200
        private const val MAX_JSON_RESPONSE_BYTES = 2 * 1024 * 1024
        private const val MAX_ERROR_RESPONSE_BYTES = 128 * 1024
        private val JSON_MEDIA = "application/json".toMediaType()
        private val OCTET_MEDIA = "application/octet-stream".toMediaType()
        private val HEX = "0123456789abcdef".toCharArray()
    }
}
