package com.shinpstudio.phonetransfer.ui

import android.app.Application
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import androidx.core.net.toUri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.shinpstudio.phonetransfer.data.DurableTransferKind
import com.shinpstudio.phonetransfer.data.FileTransferException
import com.shinpstudio.phonetransfer.data.FileTransferRepository
import com.shinpstudio.phonetransfer.data.NsdDiscovery
import com.shinpstudio.phonetransfer.data.PairingRepository
import com.shinpstudio.phonetransfer.data.PersistedTransferOperation
import com.shinpstudio.phonetransfer.data.PersistedUploadQueue
import com.shinpstudio.phonetransfer.data.QueuedUploadState
import com.shinpstudio.phonetransfer.data.SavedPc
import com.shinpstudio.phonetransfer.data.TextMessageRepository
import com.shinpstudio.phonetransfer.data.TransferOperationStore
import com.shinpstudio.phonetransfer.data.UploadQueuePersistence
import com.shinpstudio.phonetransfer.data.UploadQueueStore
import com.shinpstudio.phonetransfer.domain.RemotePathRules
import com.shinpstudio.phonetransfer.domain.TextMessageRules
import com.shinpstudio.phonetransfer.protocol.FileEntry
import com.shinpstudio.phonetransfer.protocol.Share
import com.shinpstudio.phonetransfer.transfer.FileTransferService
import com.shinpstudio.phonetransfer.transfer.TransferKind
import com.shinpstudio.phonetransfer.transfer.TransferRuntimeBus
import com.shinpstudio.phonetransfer.transfer.TransferServiceState
import com.shinpstudio.phonetransfer.transfer.TransferStatusBus
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

data class UploadQueueFailure(val displayName: String, val code: String)

data class UploadQueueSummary(
    val totalCount: Int,
    val completedCount: Int,
    val cancelledCount: Int,
    val failures: List<UploadQueueFailure>,
    val currentName: String?,
    val currentIndex: Int?,
    val remainingCount: Int,
    val cancelRequested: Boolean
)

data class HomeState(
    val connectionLabel: String = "PC未接続",
    val busy: Boolean = false,
    val comparisonCode: String? = null,
    val pcs: List<SavedPc> = emptyList(),
    val activePcId: String? = null,
    val share: Share? = null,
    val entries: List<FileEntry> = emptyList(),
    val currentPath: String = "",
    val transfer: TransferServiceState = TransferServiceState.Idle,
    val uploadQueue: UploadQueueSummary? = null
)

class HomeViewModel(application: Application) : AndroidViewModel(application) {
    private val repository = PairingRepository(application)
    private val fileRepository = FileTransferRepository(application)
    private val textRepository = TextMessageRepository(application)
    private val discovery = NsdDiscovery(application)
    private val transferOperations = TransferOperationStore.get(application)
    private val uploadQueueStore = UploadQueueStore.get(application)
    private val mutableState = MutableStateFlow(HomeState())
    val state = mutableState.asStateFlow()
    private var operation: Job? = null
    private val queueDriveMutex = Mutex()
    private var recoveryReady = false
    private var queueStartRequested: String? = null

    init {
        runOperation {
            restoreQueuedCancellation()
            restoreInterruptedTransfer()
            recoveryReady = true
            handleUploadQueueState(TransferStatusBus.state.value)
            driveUploadQueue()
            mutableState.update { it.copy(pcs = repository.saved()) }
        }
        viewModelScope.launch {
            TransferStatusBus.state.collect { transfer ->
                if (
                    transfer is TransferServiceState.Running ||
                    transfer is TransferServiceState.Resumable ||
                    transfer is TransferServiceState.Completed ||
                    transfer is TransferServiceState.Failed ||
                    transfer is TransferServiceState.Cancelled
                ) {
                    val operationId = when (transfer) {
                        is TransferServiceState.Running -> transfer.operationId
                        is TransferServiceState.Resumable -> transfer.operationId
                        is TransferServiceState.Completed -> transfer.operationId
                        is TransferServiceState.Failed -> transfer.operationId
                        is TransferServiceState.Cancelled -> transfer.operationId
                        else -> error("unreachable")
                    }
                    if (queueStartRequested == operationId) queueStartRequested = null
                }
                mutableState.update { current ->
                    val label =
                        when (transfer) {
                            is TransferServiceState.Resumable -> {
                                if (transfer.canResume) {
                                    "中断したファイル転送があります。内容を確認して再開できます"
                                } else {
                                    "中断した転送は再開せず、中止処理を完了する必要があります"
                                }
                            }

                            is TransferServiceState.RecoveryBlocked -> {
                                "転送の復旧情報を安全に読み取れないため、新しい転送を停止しています (${transfer.code})"
                            }

                            is TransferServiceState.Completed -> {
                                val action =
                                    if (transfer.kind == TransferKind.Upload) {
                                        "送信"
                                    } else {
                                        "受信"
                                    }
                                "${action}が完了しました: ${transfer.fileName}"
                            }

                            is TransferServiceState.Failed -> {
                                "ファイル転送に失敗しました (${transfer.code})"
                            }

                            is TransferServiceState.Cancelled -> "ファイル転送を中止しました"
                            else -> current.connectionLabel
                        }
                    current.copy(transfer = transfer, connectionLabel = label)
                }
                if (recoveryReady) handleUploadQueueState(transfer)
            }
        }
        viewModelScope.launch {
            TransferRuntimeBus.activeOperationId.collect { activeOperationId ->
                if (recoveryReady && activeOperationId == null) {
                    handleUploadQueueState(TransferStatusBus.state.value)
                    driveUploadQueue()
                }
            }
        }
        viewModelScope.launch {
            try {
                discovery.discover().collect { candidate ->
                    try {
                        val refreshed = repository.acceptDiscovery(candidate) ?: return@collect
                        mutableState.update { current ->
                            val label =
                                if (current.connectionLabel == "PC未接続") {
                                    "${refreshed.displayName} をLAN上で再検出しました"
                                } else {
                                    current.connectionLabel
                                }
                            current.copy(
                                pcs = repository.saved(),
                                connectionLabel = label
                            )
                        }
                    } catch (error: CancellationException) {
                        throw error
                    } catch (_: Exception) {
                        // Discovery is not trusted. Ignore candidates that fail the stored
                        // pin/device identity check.
                    }
                }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                // QR/manual endpoints remain available when NSD is unavailable on the current network.
            }
        }
    }

    fun pair(payload: String) = runOperation {
        mutableState.update { it.copy(connectionLabel = "登録要求を送信中") }
        val pc =
            repository.pair(payload) { code ->
                withContext(Dispatchers.Main.immediate) {
                    mutableState.update {
                        it.copy(
                            comparisonCode = code,
                            connectionLabel = "PCの番号を確認してPC側で承認してください"
                        )
                    }
                }
            }
        mutableState.update { it.copy(pcs = repository.saved()) }
        loadRemote(pc, "")
    }

    fun connect(pc: SavedPc) = runOperation {
        repository.connect(pc)
        loadRemote(pc, "")
    }

    fun sendText(kind: String, content: String) = runOperation(blockWhenTransferPending = false) {
        TextMessageRules.validate(kind, content)
        val pc = activePc() ?: error("PC_NOT_CONNECTED")
        textRepository.send(pc, kind, content)
        mutableState.update {
            it.copy(
                connectionLabel =
                if (kind == TextMessageRules.URL) {
                    "PCへURLを送りました"
                } else {
                    "PCへテキストを送りました"
                }
            )
        }
    }

    fun refreshFiles() = runOperation {
        val pc = activePc() ?: error("PC_NOT_CONNECTED")
        loadRemote(pc, state.value.currentPath)
    }

    fun openDirectory(entry: FileEntry) = runOperation {
        check(entry.kind == "directory") { "NOT_A_DIRECTORY" }
        val pc = activePc() ?: error("PC_NOT_CONNECTED")
        val share = state.value.share ?: error("SHARE_NOT_AVAILABLE")
        RemotePathRules.validate(entry.relativePath)
        val entries = fileRepository.listEntries(pc, share.id, entry.relativePath)
        mutableState.update {
            it.copy(
                entries = entries,
                currentPath = entry.relativePath,
                connectionLabel = "${pc.displayName} を参照中"
            )
        }
    }

    fun goUp() = runOperation {
        val pc = activePc() ?: error("PC_NOT_CONNECTED")
        val share = state.value.share ?: error("SHARE_NOT_AVAILABLE")
        val parent = RemotePathRules.parent(state.value.currentPath)
        val entries = fileRepository.listEntries(pc, share.id, parent)
        mutableState.update {
            it.copy(entries = entries, currentPath = parent)
        }
    }

    fun upload(uris: List<Uri>) = runOperation {
        val current = state.value
        val share = current.share ?: return@runOperation
        val deviceId = current.activePcId ?: return@runOperation
        if (!share.writable ||
            current.transfer.blocksNewTransfer() ||
            current.hasPendingUploads()
        ) {
            return@runOperation
        }
        val unique = uris.distinctBy(Uri::toString)
        if (unique.isEmpty()) return@runOperation
        if (unique.size > UploadQueuePersistence.MAX_ITEMS) {
            mutableState.update {
                it.copy(
                    connectionLabel =
                    "一度に選べるのは${UploadQueuePersistence.MAX_ITEMS}ファイルまでです"
                )
            }
            return@runOperation
        }

        val newlyGranted = mutableListOf<Uri>()
        val accepted = withContext(Dispatchers.IO) {
            unique.mapNotNull { uri ->
                val alreadyGranted = hasPersistedReadGrant(uri)
                val granted = alreadyGranted || takePersistableReadGrant(uri)
                if (!granted) return@mapNotNull null
                if (!alreadyGranted) newlyGranted += uri
                uri.toString() to displayName(uri)
            }
        }
        if (accepted.isEmpty()) {
            mutableState.update {
                it.copy(connectionLabel = "選択したファイルの永続読み取り権限を取得できませんでした")
            }
            return@runOperation
        }

        val queue = PersistedUploadQueue.create(
            deviceId,
            share.id,
            current.currentPath,
            accepted,
            System.currentTimeMillis()
        )
        try {
            withContext(Dispatchers.IO) { uploadQueueStore.create(queue) }
        } catch (error: Exception) {
            withContext(Dispatchers.IO) {
                newlyGranted.forEach(::releasePersistableReadGrant)
            }
            throw error
        }
        publishUploadQueue(queue)
        val rejected = unique.size - accepted.size
        mutableState.update {
            it.copy(
                connectionLabel =
                if (rejected == 0) {
                    "${accepted.size}ファイルを送信キューへ追加しました"
                } else {
                    "${accepted.size}ファイルを追加しました（権限を保持できない${rejected}件は除外）"
                }
            )
        }
        driveUploadQueue()
    }

    fun download(entry: FileEntry, destination: Uri) {
        val current = state.value
        val share = current.share ?: return
        val deviceId = current.activePcId ?: return
        if (entry.kind != "file" || current.transfer.blocksNewTransfer()) return
        try {
            RemotePathRules.validate(entry.relativePath)
            FileTransferService.startDownload(
                getApplication(),
                deviceId,
                share.id,
                entry.relativePath,
                destination
            )
            mutableState.update {
                it.copy(connectionLabel = "PCからの受信を開始しました")
            }
        } catch (_: RuntimeException) {
            mutableState.update {
                it.copy(connectionLabel = "ファイル転送サービスを開始できませんでした")
            }
        }
    }

    fun resumeTransfer() {
        val transfer = state.value.transfer as? TransferServiceState.Resumable ?: return
        if (!transfer.canResume) return
        try {
            FileTransferService.resume(getApplication(), transfer.operationId)
            mutableState.update {
                it.copy(connectionLabel = "中断したファイル転送を再確認しています")
            }
        } catch (_: RuntimeException) {
            mutableState.update {
                it.copy(connectionLabel = "ファイル転送サービスを再開できませんでした")
            }
        }
    }

    fun retryUploadQueue() {
        viewModelScope.launch { driveUploadQueue() }
    }

    fun forget(pc: SavedPc) = runOperation {
        repository.forget(pc.deviceId)
        mutableState.update { current ->
            val active = current.activePcId == pc.deviceId
            current.copy(
                pcs = repository.saved(),
                activePcId = if (active) null else current.activePcId,
                share = if (active) null else current.share,
                entries = if (active) emptyList() else current.entries,
                currentPath = if (active) "" else current.currentPath,
                connectionLabel =
                "スマホの登録情報を削除しました。" +
                    "再登録前にPC側でも端末を解除してください。"
            )
        }
    }

    fun cancel() {
        val transfer = state.value.transfer
        if (state.value.hasPendingUploads()) {
            viewModelScope.launch {
                try {
                    val before = withContext(Dispatchers.IO) { uploadQueueStore.read() }
                    var updated = withContext(Dispatchers.IO) {
                        uploadQueueStore.requestCancel(System.currentTimeMillis())
                    }
                    val waitingUris = before?.items
                        ?.filter { it.state == QueuedUploadState.Queued }
                        ?.map { it.uri.toUri() }
                        .orEmpty()
                    withContext(Dispatchers.IO) {
                        waitingUris.forEach(::releasePersistableReadGrant)
                    }
                    val activeUpload = before?.active
                    if (activeUpload != null) {
                        val activeOperationId = activeUpload.operationId
                        val operationStarted =
                            queueStartRequested == activeOperationId ||
                                TransferRuntimeBus.activeOperationId.value == activeOperationId ||
                                withContext(Dispatchers.IO) {
                                    transferOperations.find(activeOperationId) != null
                                }
                        if (operationStarted) {
                            FileTransferService.cancel(getApplication(), activeOperationId)
                            mutableState.update {
                                it.copy(connectionLabel = "送信キューを中止しています…")
                            }
                        } else {
                            updated = withContext(Dispatchers.IO) {
                                uploadQueueStore.cancelActive(
                                    activeOperationId,
                                    System.currentTimeMillis()
                                )
                            }
                            withContext(Dispatchers.IO) {
                                activeUpload.uri.toUri().let(::releasePersistableReadGrant)
                            }
                            mutableState.update {
                                it.copy(connectionLabel = "送信キューを中止しました")
                            }
                        }
                    } else {
                        mutableState.update {
                            it.copy(connectionLabel = "送信キューを中止しました")
                        }
                    }
                    publishUploadQueue(updated)
                } catch (_: Exception) {
                    mutableState.update {
                        it.copy(connectionLabel = "送信キューの中止状態を保存できませんでした")
                    }
                }
            }
            return
        }
        if (transfer is TransferServiceState.Running) {
            FileTransferService.cancel(getApplication(), transfer.operationId)
            mutableState.update {
                it.copy(connectionLabel = "ファイル転送を中止しています…")
            }
            return
        }
        if (transfer is TransferServiceState.Resumable) {
            FileTransferService.cancel(getApplication(), transfer.operationId)
            mutableState.update {
                it.copy(connectionLabel = "中断した転送の中止処理を行っています…")
            }
            return
        }
        operation?.cancel()
        mutableState.update {
            it.copy(
                comparisonCode = null,
                connectionLabel =
                "中止しました。PC側で承認済みの場合はPCの端末一覧から解除してください。"
            )
        }
    }

    private suspend fun restoreInterruptedTransfer() {
        val pending = withContext(Dispatchers.IO) {
            try {
                transferOperations.read().firstOrNull()
            } catch (_: Exception) {
                TransferStatusBus.recoveryBlocked("LOCAL_JOURNAL_INVALID")
                null
            }
        } ?: return
        val kind = pending.kind.toTransferKind()
        val reason = if (pending.cancelRequested) "CANCEL_PENDING" else "PROCESS_INTERRUPTED"
        // Provider access happens outside the journal monitor. Publish only if this snapshot
        // still exists, while serialized with completion/removal/new-operation persistence.
        val grantAvailable = try {
            pending.completedFileName == null && hasPersistedGrant(pending)
        } catch (_: Exception) {
            false
        }
        withContext(Dispatchers.IO) {
            try {
                transferOperations.ifCurrent(pending) {
                    val completedName = pending.completedFileName
                    if (completedName != null) {
                        TransferStatusBus.restoreCompleted(pending.operationId, kind, completedName)
                    } else {
                        TransferStatusBus.restoreResumable(
                            pending.operationId,
                            kind,
                            pending.committedOffset,
                            pending.totalSize ?: 0L,
                            reason,
                            grantAvailable && !pending.cancelRequested
                        )
                    }
                }
            } catch (_: Exception) {
                TransferStatusBus.recoveryBlocked("LOCAL_JOURNAL_INVALID")
            }
        }
        if (pending.cancelRequested && pending.completedFileName == null) {
            FileTransferService.cancel(getApplication(), pending.operationId)
        }
    }

    private suspend fun restoreQueuedCancellation() {
        val queue = try {
            withContext(Dispatchers.IO) { uploadQueueStore.read() }
        } catch (_: Exception) {
            TransferStatusBus.recoveryBlocked("UPLOAD_QUEUE_INVALID")
            return
        }
        val active = queue?.takeIf { it.cancelRequested }?.active ?: return
        val operation = try {
            withContext(Dispatchers.IO) { transferOperations.find(active.operationId) }
        } catch (_: Exception) {
            TransferStatusBus.recoveryBlocked("LOCAL_JOURNAL_INVALID")
            return
        }
        if (operation != null) {
            try {
                withContext(Dispatchers.IO) {
                    transferOperations.requestCancel(active.operationId, System.currentTimeMillis())
                }
            } catch (_: Exception) {
                TransferStatusBus.recoveryBlocked("LOCAL_JOURNAL_UNAVAILABLE")
            }
            return
        }
        try {
            val updated = withContext(Dispatchers.IO) {
                uploadQueueStore.cancelActive(active.operationId, System.currentTimeMillis())
            }
            withContext(Dispatchers.IO) {
                active.uri.toUri().let(::releasePersistableReadGrant)
            }
            publishUploadQueue(updated)
        } catch (_: Exception) {
            TransferStatusBus.recoveryBlocked("UPLOAD_QUEUE_UNAVAILABLE")
        }
    }

    private suspend fun handleUploadQueueState(transfer: TransferServiceState) {
        val terminal = when (transfer) {
            is TransferServiceState.Completed -> Triple(
                transfer.operationId,
                transfer.fileName,
                null
            )

            is TransferServiceState.Failed -> Triple(
                transfer.operationId,
                null,
                transfer.code
            )

            is TransferServiceState.Cancelled -> Triple(
                transfer.operationId,
                null,
                null
            )

            else -> return
        }
        val queue = try {
            withContext(Dispatchers.IO) { uploadQueueStore.read() }
        } catch (_: Exception) {
            TransferStatusBus.recoveryBlocked("UPLOAD_QUEUE_INVALID")
            return
        }
        if (queue?.active?.operationId != terminal.first) {
            return
        }
        val resultFileName = terminal.second
        val errorCode = terminal.third

        val updated = try {
            withContext(Dispatchers.IO) {
                when {
                    resultFileName != null -> uploadQueueStore.complete(
                        terminal.first,
                        resultFileName,
                        System.currentTimeMillis()
                    )

                    errorCode != null -> uploadQueueStore.fail(
                        terminal.first,
                        errorCode,
                        System.currentTimeMillis()
                    )

                    else -> uploadQueueStore.cancelActive(
                        terminal.first,
                        System.currentTimeMillis()
                    )
                }
            }
        } catch (_: Exception) {
            TransferStatusBus.recoveryBlocked("UPLOAD_QUEUE_UNAVAILABLE")
            return
        }
        publishUploadQueue(updated)
        if (updated?.isFinished == true && updated.cancelRequested.not()) {
            refreshAfterUploadBatch(updated)
        }
    }

    private suspend fun driveUploadQueue() = queueDriveMutex.withLock {
        val initial = try {
            withContext(Dispatchers.IO) { uploadQueueStore.read() }
        } catch (_: Exception) {
            TransferStatusBus.recoveryBlocked("UPLOAD_QUEUE_INVALID")
            return@withLock
        }
        publishUploadQueue(initial)
        if (initial == null || initial.isFinished || initial.cancelRequested) return@withLock
        if (TransferRuntimeBus.activeOperationId.value != null) return@withLock
        if (TransferStatusBus.state.value.blocksNewTransfer()) return@withLock

        val queue = if (initial.active == null) {
            try {
                withContext(Dispatchers.IO) {
                    uploadQueueStore.activateNext(System.currentTimeMillis())
                }
            } catch (_: Exception) {
                TransferStatusBus.recoveryBlocked("UPLOAD_QUEUE_UNAVAILABLE")
                return@withLock
            }
        } else {
            initial
        } ?: return@withLock
        publishUploadQueue(queue)
        val active = queue.active ?: return@withLock

        val durableOperation = try {
            withContext(Dispatchers.IO) { transferOperations.read().singleOrNull() }
        } catch (_: Exception) {
            TransferStatusBus.recoveryBlocked("LOCAL_JOURNAL_INVALID")
            return@withLock
        }
        if (durableOperation?.operationId == active.operationId) {
            if (durableOperation.completedFileName != null) {
                TransferStatusBus.restoreCompleted(
                    active.operationId,
                    TransferKind.Upload,
                    durableOperation.completedFileName
                )
            } else {
                val reason =
                    if (durableOperation.cancelRequested) {
                        "CANCEL_PENDING"
                    } else {
                        "PROCESS_INTERRUPTED"
                    }
                TransferStatusBus.restoreResumable(
                    active.operationId,
                    TransferKind.Upload,
                    durableOperation.committedOffset,
                    durableOperation.totalSize ?: 0L,
                    reason,
                    !durableOperation.cancelRequested && hasPersistedGrant(durableOperation)
                )
            }
            return@withLock
        }
        if (durableOperation?.completedFileName == null && durableOperation != null) return@withLock
        if (queueStartRequested == active.operationId) return@withLock

        try {
            queueStartRequested = active.operationId
            FileTransferService.startUpload(
                getApplication(),
                queue.deviceId,
                queue.shareId,
                queue.directory,
                active.uri.toUri(),
                active.operationId
            )
            mutableState.update {
                it.copy(connectionLabel = "${active.displayName} の送信を開始しました")
            }
        } catch (_: RuntimeException) {
            queueStartRequested = null
            mutableState.update {
                it.copy(connectionLabel = "ファイル転送サービスを開始できませんでした")
            }
        }
    }

    private suspend fun refreshAfterUploadBatch(queue: PersistedUploadQueue) {
        val current = state.value
        if (current.activePcId != queue.deviceId || current.share?.id != queue.shareId) return
        try {
            val pc = activePc() ?: return
            val entries = fileRepository.listEntries(pc, queue.shareId, queue.directory)
            mutableState.update {
                if (it.activePcId == queue.deviceId && it.currentPath == queue.directory) {
                    it.copy(entries = entries)
                } else {
                    it
                }
            }
        } catch (_: Exception) {
            // The completed batch remains authoritative even if refreshing the listing fails.
        }
    }

    private fun publishUploadQueue(queue: PersistedUploadQueue?) {
        mutableState.update { it.copy(uploadQueue = queue?.toSummary()) }
    }

    private fun PersistedUploadQueue.toSummary(): UploadQueueSummary {
        val activeIndex = items.indexOfFirst { it.state == QueuedUploadState.Active }
            .takeIf { it >= 0 }
        return UploadQueueSummary(
            totalCount = items.size,
            completedCount = items.count { it.state == QueuedUploadState.Completed },
            cancelledCount = items.count { it.state == QueuedUploadState.Cancelled },
            failures = items
                .filter { it.state == QueuedUploadState.Failed }
                .map { UploadQueueFailure(it.displayName, checkNotNull(it.errorCode)) },
            currentName = activeIndex?.let { items[it].displayName },
            currentIndex = activeIndex?.plus(1),
            remainingCount = items.count {
                it.state == QueuedUploadState.Queued || it.state == QueuedUploadState.Active
            },
            cancelRequested = cancelRequested
        )
    }

    private fun displayName(uri: Uri): String = try {
        getApplication<Application>().contentResolver.query(
            uri,
            arrayOf(OpenableColumns.DISPLAY_NAME),
            null,
            null,
            null
        )?.use { cursor ->
            if (cursor.moveToFirst()) {
                cursor.getString(0)?.take(512)?.takeIf(String::isNotBlank)
            } else {
                null
            }
        }
    } catch (_: Exception) {
        null
    } ?: uri.lastPathSegment?.takeLast(512)?.takeIf(String::isNotBlank) ?: "ファイル"

    private fun takePersistableReadGrant(uri: Uri): Boolean = try {
        getApplication<Application>().contentResolver.takePersistableUriPermission(
            uri,
            Intent.FLAG_GRANT_READ_URI_PERMISSION
        )
        hasPersistedReadGrant(uri)
    } catch (_: SecurityException) {
        false
    }

    private fun releasePersistableReadGrant(uri: Uri) {
        try {
            getApplication<Application>().contentResolver.releasePersistableUriPermission(
                uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION
            )
        } catch (_: SecurityException) {
            // The provider may already have revoked the permission.
        }
    }

    private fun hasPersistedReadGrant(uri: Uri): Boolean =
        getApplication<Application>().contentResolver.persistedUriPermissions.any { permission ->
            permission.uri == uri && permission.isReadPermission
        }

    private fun hasPersistedGrant(operation: PersistedTransferOperation): Boolean {
        val expectedUri = operation.uri.toUri()
        val readGrant = operation.kind == DurableTransferKind.Upload
        val permissions =
            getApplication<Application>().contentResolver.persistedUriPermissions
        return permissions.any { permission ->
            if (permission.uri != expectedUri) {
                false
            } else if (readGrant) {
                permission.isReadPermission
            } else {
                permission.isWritePermission
            }
        }
    }

    private fun DurableTransferKind.toTransferKind(): TransferKind =
        if (this == DurableTransferKind.Upload) TransferKind.Upload else TransferKind.Download

    private suspend fun loadRemote(pc: SavedPc, path: String) {
        val share = fileRepository.listShares(pc).firstOrNull()
        if (share == null) {
            mutableState.update {
                it.copy(
                    activePcId = pc.deviceId,
                    share = null,
                    entries = emptyList(),
                    currentPath = "",
                    connectionLabel =
                    "${pc.displayName} に接続しました（PC側の受信フォルダは未設定です）"
                )
            }
            return
        }
        val entries = fileRepository.listEntries(pc, share.id, path)
        mutableState.update {
            it.copy(
                activePcId = pc.deviceId,
                share = share,
                entries = entries,
                currentPath = path,
                connectionLabel = "${pc.displayName} に接続しました"
            )
        }
    }

    private suspend fun activePc(): SavedPc? {
        val id = state.value.activePcId ?: return null
        return repository.saved().firstOrNull { it.deviceId == id }
    }

    private fun runOperation(blockWhenTransferPending: Boolean = true, block: suspend () -> Unit) {
        if (
            operation?.isCompleted == false ||
            (
                blockWhenTransferPending &&
                    (state.value.transfer.blocksNewTransfer() || state.value.hasPendingUploads())
                )
        ) {
            return
        }
        operation =
            viewModelScope.launch {
                mutableState.update { it.copy(busy = true) }
                try {
                    block()
                } catch (error: TimeoutCancellationException) {
                    mutableState.update {
                        it.copy(
                            connectionLabel =
                            "登録の有効期限が切れました。PCで新しいQRを表示してください。"
                        )
                    }
                } catch (error: CancellationException) {
                    throw error
                } catch (error: FileTransferException) {
                    mutableState.update {
                        it.copy(
                            connectionLabel = "PCとの操作に失敗しました (${error.code})"
                        )
                    }
                } catch (_: Exception) {
                    mutableState.update {
                        it.copy(
                            connectionLabel =
                            "接続または保存に失敗しました。LANとQR期限を確認してください。" +
                                "再登録する場合はPC側の登録を解除してください。"
                        )
                    }
                } finally {
                    mutableState.update {
                        it.copy(busy = false, comparisonCode = null)
                    }
                }
            }
    }

    private fun TransferServiceState.blocksNewTransfer(): Boolean =
        this is TransferServiceState.Running ||
            this is TransferServiceState.Resumable ||
            this is TransferServiceState.RecoveryBlocked

    private fun HomeState.hasPendingUploads(): Boolean =
        uploadQueue?.remainingCount?.let { it > 0 } == true
}
