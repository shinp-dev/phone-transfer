package com.shinpstudio.phonetransfer.data

import android.content.Context
import android.util.AtomicFile
import com.shinpstudio.phonetransfer.domain.RemotePathRules
import com.shinpstudio.phonetransfer.domain.TransferWireRules
import java.io.File
import java.util.UUID
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

@Serializable
internal enum class DurableTransferKind {
    @SerialName("upload")
    Upload,

    @SerialName("download")
    Download
}

@Serializable
internal data class PersistedTransferOperation(
    val operationId: String,
    val kind: DurableTransferKind,
    val deviceId: String,
    val shareId: String,
    val remotePath: String,
    val uri: String,
    val persistedGrant: Boolean,
    val idempotencyKey: String? = null,
    val serverTransferId: String? = null,
    val sourceName: String? = null,
    val totalSize: Long? = null,
    val sha256: String? = null,
    val committedOffset: Long = 0,
    val cancelRequested: Boolean = false,
    val updatedAtEpochMillis: Long
) {
    fun withGrant(persisted: Boolean, now: Long): PersistedTransferOperation =
        copy(persistedGrant = persisted, updatedAtEpochMillis = now)

    fun withSource(name: String, size: Long, hash: String, now: Long): PersistedTransferOperation =
        copy(sourceName = name, totalSize = size, sha256 = hash, updatedAtEpochMillis = now)

    fun withServer(transferId: String, offset: Long, now: Long): PersistedTransferOperation =
        copy(serverTransferId = transferId, committedOffset = offset, updatedAtEpochMillis = now)

    fun withOffset(offset: Long, now: Long): PersistedTransferOperation =
        copy(committedOffset = offset, updatedAtEpochMillis = now)

    fun requestingCancel(now: Long): PersistedTransferOperation =
        copy(cancelRequested = true, updatedAtEpochMillis = now)

    companion object {
        fun upload(
            operationId: String,
            deviceId: String,
            shareId: String,
            directory: String,
            uri: String,
            idempotencyKey: String,
            now: Long
        ) = PersistedTransferOperation(
            operationId = operationId,
            kind = DurableTransferKind.Upload,
            deviceId = deviceId,
            shareId = shareId,
            remotePath = directory,
            uri = uri,
            persistedGrant = false,
            idempotencyKey = idempotencyKey,
            updatedAtEpochMillis = now
        )

        fun download(
            operationId: String,
            deviceId: String,
            shareId: String,
            remotePath: String,
            uri: String,
            now: Long
        ) = PersistedTransferOperation(
            operationId = operationId,
            kind = DurableTransferKind.Download,
            deviceId = deviceId,
            shareId = shareId,
            remotePath = remotePath,
            uri = uri,
            persistedGrant = false,
            updatedAtEpochMillis = now
        )
    }
}

@Serializable
private data class TransferOperationDocument(
    val version: Int,
    val operations: List<PersistedTransferOperation>
)

internal object TransferOperationPersistence {
    const val MAX_BYTES = 256 * 1024
    const val MAX_OPERATIONS = 1
    private const val VERSION = 1
    private val json = Json { ignoreUnknownKeys = false }

    fun encode(operations: List<PersistedTransferOperation>): String {
        validate(operations)
        val encoded = json.encodeToString(TransferOperationDocument(VERSION, operations))
        check(encoded.toByteArray(Charsets.UTF_8).size <= MAX_BYTES) { "TRANSFER_JOURNAL_TOO_LARGE" }
        return encoded
    }

    fun decode(bytes: ByteArray): List<PersistedTransferOperation> {
        check(bytes.size <= MAX_BYTES) { "TRANSFER_JOURNAL_TOO_LARGE" }
        val document = json.decodeFromString<TransferOperationDocument>(bytes.toString(Charsets.UTF_8))
        check(document.version == VERSION) { "TRANSFER_JOURNAL_VERSION" }
        validate(document.operations)
        return document.operations
    }

    fun validate(operations: List<PersistedTransferOperation>) {
        check(operations.size <= MAX_OPERATIONS) { "TRANSFER_OPERATION_LIMIT" }
        check(operations.map { it.operationId }.distinct().size == operations.size) {
            "DUPLICATE_TRANSFER_OPERATION"
        }
        operations.forEach(::validateOperation)
    }

    fun merge(
        previous: PersistedTransferOperation,
        next: PersistedTransferOperation
    ): PersistedTransferOperation {
        validateOperation(previous)
        validateOperation(next)
        check(previous.operationId == next.operationId) { "TRANSFER_OPERATION_ID_CHANGED" }
        check(previous.kind == next.kind) { "TRANSFER_OPERATION_KIND_CHANGED" }
        check(previous.deviceId == next.deviceId) { "TRANSFER_OPERATION_DEVICE_CHANGED" }
        check(previous.shareId == next.shareId) { "TRANSFER_OPERATION_SHARE_CHANGED" }
        check(previous.remotePath == next.remotePath) { "TRANSFER_OPERATION_PATH_CHANGED" }
        check(previous.uri == next.uri) { "TRANSFER_OPERATION_URI_CHANGED" }
        check(previous.idempotencyKey == next.idempotencyKey) { "TRANSFER_OPERATION_IDEMPOTENCY_CHANGED" }
        check(!previous.persistedGrant || next.persistedGrant) { "TRANSFER_OPERATION_GRANT_REGRESSED" }
        if (previous.sourceName != null) {
            check(previous.sourceName == next.sourceName) { "TRANSFER_SOURCE_NAME_CHANGED" }
            check(previous.totalSize == next.totalSize) { "TRANSFER_SOURCE_SIZE_CHANGED" }
            check(previous.sha256 == next.sha256) { "TRANSFER_SOURCE_HASH_CHANGED" }
        }
        if (previous.serverTransferId != null) {
            check(previous.serverTransferId == next.serverTransferId) { "SERVER_TRANSFER_ID_CHANGED" }
        }
        check(next.committedOffset >= previous.committedOffset) { "LOCAL_OFFSET_REGRESSED" }
        val merged = if (previous.cancelRequested) next.copy(cancelRequested = true) else next
        validateOperation(merged)
        return merged
    }

    private fun validateOperation(operation: PersistedTransferOperation) {
        requireUuid(operation.operationId, "INVALID_OPERATION_ID")
        requireUuid(operation.deviceId, "INVALID_DEVICE_ID")
        requireUuid(operation.shareId, "INVALID_SHARE_ID")
        check(operation.uri.startsWith("content://") && operation.uri.length <= MAX_URI_LENGTH) {
            "INVALID_TRANSFER_URI"
        }
        check(operation.updatedAtEpochMillis >= 0) { "INVALID_TRANSFER_TIMESTAMP" }

        when (operation.kind) {
            DurableTransferKind.Upload -> validateUpload(operation)
            DurableTransferKind.Download -> validateDownload(operation)
        }
    }

    private fun validateUpload(operation: PersistedTransferOperation) {
        RemotePathRules.validate(operation.remotePath, allowRoot = true)
        val idempotency = checkNotNull(operation.idempotencyKey) { "MISSING_IDEMPOTENCY_KEY" }
        requireUuid(idempotency, "INVALID_IDEMPOTENCY_KEY")
        operation.serverTransferId?.let { requireUuid(it, "INVALID_TRANSFER_ID") }

        val metadataCount = listOf(operation.sourceName, operation.totalSize, operation.sha256).count { it != null }
        check(metadataCount == 0 || metadataCount == 3) { "INCOMPLETE_SOURCE_METADATA" }
        if (metadataCount == 0) {
            check(operation.serverTransferId == null && operation.committedOffset == 0L) {
                "UPLOAD_METADATA_REQUIRED"
            }
            return
        }

        val name = checkNotNull(operation.sourceName)
        val size = checkNotNull(operation.totalSize)
        val hash = checkNotNull(operation.sha256)
        RemotePathRules.validateName(name)
        check(size in 0..TransferWireRules.MAX_FILE_BYTES) { "INVALID_SOURCE_SIZE" }
        check(hash.matches(Regex("[a-f0-9]{64}"))) { "INVALID_SOURCE_SHA256" }
        check(operation.committedOffset in 0..size) { "INVALID_LOCAL_OFFSET" }
    }

    private fun validateDownload(operation: PersistedTransferOperation) {
        RemotePathRules.validate(operation.remotePath)
        check(operation.idempotencyKey == null && operation.serverTransferId == null) {
            "DOWNLOAD_SERVER_STATE_UNEXPECTED"
        }
        check(operation.sourceName == null && operation.totalSize == null && operation.sha256 == null) {
            "DOWNLOAD_SOURCE_METADATA_UNEXPECTED"
        }
        check(operation.committedOffset == 0L) { "DOWNLOAD_OFFSET_UNSUPPORTED" }
    }

    private fun requireUuid(value: String, code: String) {
        try {
            check(UUID.fromString(value).toString() == value) { code }
        } catch (error: IllegalArgumentException) {
            throw IllegalStateException(code, error)
        }
    }

    private const val MAX_URI_LENGTH = 16 * 1024
}

internal class TransferOperationStore private constructor(context: Context) {
    private val file = AtomicFile(File(context.filesDir, "transfer-operations.json"))

    @Synchronized
    fun read(): List<PersistedTransferOperation> = readUnlocked()

    @Synchronized
    fun find(operationId: String): PersistedTransferOperation? =
        readUnlocked().firstOrNull { it.operationId == operationId }

    @Synchronized
    fun insert(operation: PersistedTransferOperation): PersistedTransferOperation {
        val current = readUnlocked()
        val existing = current.firstOrNull { it.operationId == operation.operationId }
        if (existing != null) {
            check(existing == operation) { "TRANSFER_OPERATION_ID_CONFLICT" }
            return existing
        }
        check(current.size < TransferOperationPersistence.MAX_OPERATIONS) { "TRANSFER_OPERATION_LIMIT" }
        writeUnlocked(current + operation)
        return operation
    }

    @Synchronized
    fun replace(operation: PersistedTransferOperation): PersistedTransferOperation {
        val current = readUnlocked()
        val index = current.indexOfFirst { it.operationId == operation.operationId }
        check(index >= 0) { "TRANSFER_OPERATION_NOT_FOUND" }
        val merged = TransferOperationPersistence.merge(current[index], operation)
        val next = current.toMutableList().also { it[index] = merged }
        writeUnlocked(next)
        return merged
    }

    @Synchronized
    fun requestCancel(operationId: String, now: Long): PersistedTransferOperation? {
        val current = readUnlocked()
        val index = current.indexOfFirst { it.operationId == operationId }
        if (index < 0) return null
        val updated = current[index].requestingCancel(now)
        val next = current.toMutableList().also { it[index] = updated }
        writeUnlocked(next)
        return updated
    }

    @Synchronized
    fun remove(operationId: String) {
        val current = readUnlocked()
        if (current.none { it.operationId == operationId }) return
        writeUnlocked(current.filterNot { it.operationId == operationId })
    }

    private fun readUnlocked(): List<PersistedTransferOperation> {
        if (!file.baseFile.exists()) return emptyList()
        val bytes = file.openRead().use { input ->
            val buffer = ByteArray(TransferOperationPersistence.MAX_BYTES + 1)
            var size = 0
            while (size < buffer.size) {
                val count = input.read(buffer, size, buffer.size - size)
                if (count <= 0) break
                size += count
            }
            check(size <= TransferOperationPersistence.MAX_BYTES) { "TRANSFER_JOURNAL_TOO_LARGE" }
            buffer.copyOf(size)
        }
        return TransferOperationPersistence.decode(bytes)
    }

    private fun writeUnlocked(operations: List<PersistedTransferOperation>) {
        val bytes = TransferOperationPersistence.encode(operations).toByteArray(Charsets.UTF_8)
        val output = file.startWrite()
        try {
            output.write(bytes)
            file.finishWrite(output)
        } catch (error: Exception) {
            file.failWrite(output)
            throw error
        }
    }

    companion object {
        @Volatile
        private var instance: TransferOperationStore? = null

        fun get(context: Context): TransferOperationStore = instance ?: synchronized(this) {
            instance ?: TransferOperationStore(context.applicationContext).also { instance = it }
        }
    }
}
