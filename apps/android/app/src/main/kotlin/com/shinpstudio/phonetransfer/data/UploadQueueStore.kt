package com.shinpstudio.phonetransfer.data

import android.content.Context
import android.system.Os
import android.system.OsConstants
import com.shinpstudio.phonetransfer.domain.RemotePathRules
import java.io.File
import java.util.UUID
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

@Serializable
internal enum class QueuedUploadState {
    @SerialName("queued")
    Queued,

    @SerialName("active")
    Active,

    @SerialName("completed")
    Completed,

    @SerialName("failed")
    Failed,

    @SerialName("cancelled")
    Cancelled
}

@Serializable
internal data class QueuedUpload(
    val operationId: String,
    val uri: String,
    val displayName: String,
    val state: QueuedUploadState = QueuedUploadState.Queued,
    val resultFileName: String? = null,
    val errorCode: String? = null
)

@Serializable
internal data class PersistedUploadQueue(
    val batchId: String,
    val deviceId: String,
    val shareId: String,
    val directory: String,
    val cancelRequested: Boolean = false,
    val items: List<QueuedUpload>,
    val createdAtEpochMillis: Long,
    val updatedAtEpochMillis: Long
) {
    val active: QueuedUpload?
        get() = items.singleOrNull { it.state == QueuedUploadState.Active }

    val hasQueued: Boolean
        get() = items.any { it.state == QueuedUploadState.Queued }

    val isFinished: Boolean
        get() = items.all { it.state.isTerminal() }

    companion object {
        fun create(
            deviceId: String,
            shareId: String,
            directory: String,
            sources: List<Pair<String, String>>,
            now: Long
        ): PersistedUploadQueue = PersistedUploadQueue(
            batchId = UUID.randomUUID().toString(),
            deviceId = deviceId,
            shareId = shareId,
            directory = directory,
            items = sources.map { (uri, displayName) ->
                QueuedUpload(UUID.randomUUID().toString(), uri, displayName)
            },
            createdAtEpochMillis = now,
            updatedAtEpochMillis = now
        ).also(UploadQueuePersistence::validate)
    }
}

internal object UploadQueuePersistence {
    const val VERSION = 1
    const val MAX_ITEMS = 100
    const val MAX_BYTES = TransferOperationPersistence.MAX_BYTES
    private const val MAX_URI_LENGTH = 16 * 1024
    private const val MAX_DISPLAY_NAME_LENGTH = 512
    private val json = Json { ignoreUnknownKeys = false }

    fun encode(queue: PersistedUploadQueue): ByteArray {
        validate(queue)
        val bytes = json.encodeToString(UploadQueueDocument(VERSION, queue))
            .toByteArray(Charsets.UTF_8)
        check(bytes.size <= MAX_BYTES) { "UPLOAD_QUEUE_TOO_LARGE" }
        return bytes
    }

    fun decode(bytes: ByteArray): PersistedUploadQueue {
        check(bytes.size <= MAX_BYTES) { "UPLOAD_QUEUE_TOO_LARGE" }
        val document = json.decodeFromString<UploadQueueDocument>(bytes.toString(Charsets.UTF_8))
        check(document.version == VERSION) { "UNSUPPORTED_UPLOAD_QUEUE_VERSION" }
        validate(document.queue)
        return document.queue
    }

    fun validate(queue: PersistedUploadQueue) {
        requireCanonicalUuid(queue.batchId, "INVALID_UPLOAD_BATCH_ID")
        requireCanonicalUuid(queue.deviceId, "INVALID_UPLOAD_DEVICE_ID")
        requireCanonicalUuid(queue.shareId, "INVALID_UPLOAD_SHARE_ID")
        RemotePathRules.validate(queue.directory, allowRoot = true)
        check(queue.items.isNotEmpty() && queue.items.size <= MAX_ITEMS) {
            "UPLOAD_QUEUE_ITEM_LIMIT"
        }
        check(queue.items.count { it.state == QueuedUploadState.Active } <= 1) {
            "MULTIPLE_ACTIVE_UPLOADS"
        }
        check(queue.items.map { it.operationId }.distinct().size == queue.items.size) {
            "DUPLICATE_UPLOAD_OPERATION"
        }
        check(queue.items.map { it.uri }.distinct().size == queue.items.size) {
            "DUPLICATE_UPLOAD_SOURCE"
        }
        if (queue.cancelRequested) {
            check(queue.items.none { it.state == QueuedUploadState.Queued }) {
                "QUEUED_UPLOAD_AFTER_CANCEL"
            }
        }
        if (queue.items.any { it.state == QueuedUploadState.Cancelled }) {
            check(queue.cancelRequested) { "CANCELLED_UPLOAD_WITHOUT_BATCH_CANCEL" }
        }
        queue.items.forEach { item ->
            requireCanonicalUuid(item.operationId, "INVALID_UPLOAD_OPERATION_ID")
            check(item.uri.startsWith("content://") && item.uri.length <= MAX_URI_LENGTH) {
                "INVALID_UPLOAD_URI"
            }
            check(
                item.displayName.isNotBlank() && item.displayName.length <= MAX_DISPLAY_NAME_LENGTH
            ) {
                "INVALID_UPLOAD_DISPLAY_NAME"
            }
            when (item.state) {
                QueuedUploadState.Completed -> {
                    check(!item.resultFileName.isNullOrBlank() && item.errorCode == null) {
                        "INVALID_COMPLETED_UPLOAD"
                    }
                }

                QueuedUploadState.Failed -> {
                    check(!item.errorCode.isNullOrBlank() && item.resultFileName == null) {
                        "INVALID_FAILED_UPLOAD"
                    }
                }

                QueuedUploadState.Queued,
                QueuedUploadState.Active,
                QueuedUploadState.Cancelled -> {
                    check(item.resultFileName == null && item.errorCode == null) {
                        "INVALID_PENDING_UPLOAD"
                    }
                }
            }
        }
        check(
            queue.createdAtEpochMillis >= 0 &&
                queue.updatedAtEpochMillis >= queue.createdAtEpochMillis
        ) {
            "INVALID_UPLOAD_QUEUE_TIME"
        }
    }

    private fun requireCanonicalUuid(value: String, code: String) {
        val parsed = try {
            UUID.fromString(value)
        } catch (error: IllegalArgumentException) {
            throw IllegalStateException(code, error)
        }
        check(parsed.toString() == value) { code }
    }

    @Serializable
    private data class UploadQueueDocument(val version: Int, val queue: PersistedUploadQueue)
}

internal class UploadQueueStore internal constructor(private val file: DurableJournalFile) {
    @Synchronized
    fun read(): PersistedUploadQueue? = file.read()?.let(UploadQueuePersistence::decode)

    @Synchronized
    fun create(queue: PersistedUploadQueue): PersistedUploadQueue {
        val existing = readUnlocked()
        check(existing == null || existing.isFinished) { "UPLOAD_QUEUE_ALREADY_ACTIVE" }
        writeUnlocked(queue)
        return queue
    }

    @Synchronized
    fun activateNext(now: Long): PersistedUploadQueue? {
        val current = readUnlocked() ?: return null
        if (current.cancelRequested || current.active != null) return current
        val index = current.items.indexOfFirst { it.state == QueuedUploadState.Queued }
        if (index < 0) return current
        val items = current.items.toMutableList()
        items[index] = items[index].copy(state = QueuedUploadState.Active)
        return current.copy(items = items, updatedAtEpochMillis = now).also(::writeUnlocked)
    }

    @Synchronized
    fun complete(operationId: String, fileName: String, now: Long): PersistedUploadQueue? =
        updateActive(operationId, now) { item ->
            item.copy(
                state = QueuedUploadState.Completed,
                resultFileName = fileName
            )
        }

    @Synchronized
    fun fail(operationId: String, code: String, now: Long): PersistedUploadQueue? =
        updateActive(operationId, now) { item ->
            item.copy(state = QueuedUploadState.Failed, errorCode = code)
        }

    @Synchronized
    fun cancelActive(operationId: String, now: Long): PersistedUploadQueue? =
        updateActive(operationId, now) { item ->
            item.copy(state = QueuedUploadState.Cancelled)
        }

    @Synchronized
    fun requestCancel(now: Long): PersistedUploadQueue? {
        val current = readUnlocked() ?: return null
        if (current.cancelRequested) return current
        val updated = current.copy(
            cancelRequested = true,
            items = current.items.map { item ->
                if (item.state == QueuedUploadState.Queued) {
                    item.copy(state = QueuedUploadState.Cancelled)
                } else {
                    item
                }
            },
            updatedAtEpochMillis = now
        )
        writeUnlocked(updated)
        return updated
    }

    private fun updateActive(
        operationId: String,
        now: Long,
        transform: (QueuedUpload) -> QueuedUpload
    ): PersistedUploadQueue? {
        val current = readUnlocked() ?: return null
        val index = current.items.indexOfFirst { it.operationId == operationId }
        if (index < 0 || current.items[index].state.isTerminal()) return current
        check(current.items[index].state == QueuedUploadState.Active) {
            "UPLOAD_OPERATION_NOT_ACTIVE"
        }
        val items = current.items.toMutableList()
        items[index] = transform(items[index])
        val updated = current.copy(items = items, updatedAtEpochMillis = now)
        writeUnlocked(updated)
        return updated
    }

    private fun readUnlocked(): PersistedUploadQueue? =
        file.read()?.let(UploadQueuePersistence::decode)

    private fun writeUnlocked(queue: PersistedUploadQueue) {
        file.write(UploadQueuePersistence.encode(queue))
    }

    companion object {
        @Volatile
        private var instance: UploadQueueStore? = null

        fun get(context: Context): UploadQueueStore = instance ?: synchronized(this) {
            instance ?: UploadQueueStore(
                DurableJournalFile(
                    File(context.applicationContext.filesDir, "upload-queue.json"),
                    syncDirectory = { directory ->
                        val descriptor = Os.open(directory.path, OsConstants.O_RDONLY, 0)
                        try {
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

internal fun QueuedUploadState.isTerminal(): Boolean = this == QueuedUploadState.Completed ||
    this == QueuedUploadState.Failed ||
    this == QueuedUploadState.Cancelled
