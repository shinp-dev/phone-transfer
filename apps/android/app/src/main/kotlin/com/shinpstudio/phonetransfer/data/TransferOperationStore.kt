package com.shinpstudio.phonetransfer.data

import android.content.Context
import android.system.Os
import android.system.OsConstants
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
    val completedFileName: String? = null,
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
    private const val VERSION = 2
    private val json = Json { ignoreUnknownKeys = false }

    fun encode(operations: List<PersistedTransferOperation>): String {
        validate(operations)
        val encoded = json.encodeToString(TransferOperationDocument(VERSION, operations))
        check(encoded.toByteArray(Charsets.UTF_8).size <= MAX_BYTES) {
            "TRANSFER_JOURNAL_TOO_LARGE"
        }
        return encoded
    }

    fun decode(bytes: ByteArray): List<PersistedTransferOperation> {
        check(bytes.size <= MAX_BYTES) { "TRANSFER_JOURNAL_TOO_LARGE" }
        val document = json.decodeFromString<TransferOperationDocument>(
            bytes.toString(Charsets.UTF_8)
        )
        check(document.version == 1 || document.version == VERSION) { "TRANSFER_JOURNAL_VERSION" }
        check(document.version != 1 || document.operations.all { it.completedFileName == null }) {
            "TRANSFER_JOURNAL_VERSION"
        }
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
        check(previous.completedFileName == null || previous == next) {
            "TRANSFER_ALREADY_COMPLETED"
        }
        check(previous.operationId == next.operationId) { "TRANSFER_OPERATION_ID_CHANGED" }
        check(previous.kind == next.kind) { "TRANSFER_OPERATION_KIND_CHANGED" }
        check(previous.deviceId == next.deviceId) { "TRANSFER_OPERATION_DEVICE_CHANGED" }
        check(previous.shareId == next.shareId) { "TRANSFER_OPERATION_SHARE_CHANGED" }
        check(previous.remotePath == next.remotePath) { "TRANSFER_OPERATION_PATH_CHANGED" }
        check(previous.uri == next.uri) { "TRANSFER_OPERATION_URI_CHANGED" }
        check(previous.idempotencyKey == next.idempotencyKey) {
            "TRANSFER_OPERATION_IDEMPOTENCY_CHANGED"
        }
        check(!previous.persistedGrant || next.persistedGrant) {
            "TRANSFER_OPERATION_GRANT_REGRESSED"
        }
        if (previous.sourceName != null) {
            check(previous.sourceName == next.sourceName) { "TRANSFER_SOURCE_NAME_CHANGED" }
            check(previous.totalSize == next.totalSize) { "TRANSFER_SOURCE_SIZE_CHANGED" }
            check(previous.sha256 == next.sha256) { "TRANSFER_SOURCE_HASH_CHANGED" }
        }
        if (previous.serverTransferId != null) {
            check(previous.serverTransferId == next.serverTransferId) {
                "SERVER_TRANSFER_ID_CHANGED"
            }
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
        operation.completedFileName?.let { name ->
            RemotePathRules.validateName(name)
            if (operation.kind == DurableTransferKind.Upload) {
                check(operation.serverTransferId != null && name == operation.sourceName) {
                    "INVALID_COMPLETION_RECEIPT"
                }
                check(operation.committedOffset == operation.totalSize) {
                    "INVALID_COMPLETION_OFFSET"
                }
            } else {
                check(name == RemotePathRules.fileName(operation.remotePath)) {
                    "INVALID_COMPLETION_RECEIPT"
                }
            }
        }
    }

    private fun validateUpload(operation: PersistedTransferOperation) {
        RemotePathRules.validate(operation.remotePath, allowRoot = true)
        val idempotency = checkNotNull(operation.idempotencyKey) { "MISSING_IDEMPOTENCY_KEY" }
        requireUuid(idempotency, "INVALID_IDEMPOTENCY_KEY")
        operation.serverTransferId?.let { requireUuid(it, "INVALID_TRANSFER_ID") }

        val metadataCount = listOf(
            operation.sourceName,
            operation.totalSize,
            operation.sha256
        ).count {
            it !=
                null
        }
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
        check(operation.committedOffset == 0L || operation.serverTransferId != null) {
            "UPLOAD_TRANSFER_ID_REQUIRED"
        }
    }

    private fun validateDownload(operation: PersistedTransferOperation) {
        RemotePathRules.validate(operation.remotePath)
        check(operation.idempotencyKey == null && operation.serverTransferId == null) {
            "DOWNLOAD_SERVER_STATE_UNEXPECTED"
        }
        check(
            operation.sourceName == null && operation.totalSize == null && operation.sha256 == null
        ) {
            "DOWNLOAD_SOURCE_METADATA_UNEXPECTED"
        }
        check(operation.committedOffset == 0L) { "DOWNLOAD_OFFSET_UNSUPPORTED" }
    }

    private fun requireUuid(value: String, code: String) {
        try {
            check(UUID.fromString(value).toString() == value) { code }
            check(UUID.fromString(value) != UUID(0, 0)) { code }
        } catch (error: IllegalArgumentException) {
            throw IllegalStateException(code, error)
        }
    }

    private const val MAX_URI_LENGTH = 16 * 1024
}

internal class TransferOperationStore internal constructor(private val file: DurableJournalFile) {

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
        val pending = current.filter { it.completedFileName == null }
        check(pending.size < TransferOperationPersistence.MAX_OPERATIONS) {
            "TRANSFER_OPERATION_LIMIT"
        }
        // A new explicit user transfer retires the previous single completion receipt atomically.
        writeUnlocked(pending + operation)
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
        if (current[index].completedFileName != null) return current[index]
        val updated = current[index].requestingCancel(now)
        val next = current.toMutableList().also { it[index] = updated }
        writeUnlocked(next)
        return updated
    }

    @Synchronized
    fun remove(operationId: String) {
        val current = readUnlocked()
        if (current.none { it.operationId == operationId }) return
        check(current.none { it.operationId == operationId && it.completedFileName != null }) {
            "TRANSFER_ALREADY_COMPLETED"
        }
        writeUnlocked(current.filterNot { it.operationId == operationId })
    }

    @Synchronized
    fun complete(operationId: String, fileName: String, now: Long): PersistedTransferOperation {
        val current = readUnlocked().single { it.operationId == operationId }
        if (current.completedFileName != null) {
            check(current.completedFileName == fileName) { "COMPLETION_IDENTITY_CHANGED" }
            return current
        }
        val completed = current.copy(
            committedOffset = current.totalSize ?: 0L,
            completedFileName = fileName,
            updatedAtEpochMillis = now
        )
        writeUnlocked(listOf(completed))
        return completed
    }

    @Synchronized
    fun ifCurrent(snapshot: PersistedTransferOperation, publish: () -> Unit) {
        if (readUnlocked().singleOrNull() == snapshot) publish()
    }

    private fun readUnlocked(): List<PersistedTransferOperation> {
        val bytes = file.read() ?: return emptyList()
        return TransferOperationPersistence.decode(bytes)
    }

    private fun writeUnlocked(operations: List<PersistedTransferOperation>) {
        val bytes = TransferOperationPersistence.encode(operations).toByteArray(Charsets.UTF_8)
        file.write(bytes)
    }

    companion object {
        @Volatile
        private var instance: TransferOperationStore? = null

        fun get(context: Context): TransferOperationStore = instance ?: synchronized(this) {
            instance ?: TransferOperationStore(
                DurableJournalFile(
                    File(context.applicationContext.filesDir, "transfer-operations.json"),
                    syncDirectory = { directory ->
                        val descriptor = Os.open(
                            directory.path,
                            OsConstants.O_RDONLY,
                            0
                        )
                        try {
                            check(OsConstants.S_ISDIR(Os.fstat(descriptor).st_mode)) {
                                "TRANSFER_JOURNAL_PARENT_NOT_DIRECTORY"
                            }
                            Os.fsync(descriptor)
                        } finally {
                            Os.close(descriptor)
                        }
                    }
                )
            ).also { instance = it }
        }
    }
}
