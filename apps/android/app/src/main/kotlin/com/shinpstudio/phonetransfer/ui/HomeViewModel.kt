package com.shinpstudio.phonetransfer.ui

import android.app.Application
import android.net.Uri
import androidx.core.net.toUri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.shinpstudio.phonetransfer.data.DurableTransferKind
import com.shinpstudio.phonetransfer.data.FileTransferException
import com.shinpstudio.phonetransfer.data.FileTransferRepository
import com.shinpstudio.phonetransfer.data.NsdDiscovery
import com.shinpstudio.phonetransfer.data.PairingRepository
import com.shinpstudio.phonetransfer.data.PersistedTransferOperation
import com.shinpstudio.phonetransfer.data.SavedPc
import com.shinpstudio.phonetransfer.data.TextMessageRepository
import com.shinpstudio.phonetransfer.data.TransferOperationStore
import com.shinpstudio.phonetransfer.domain.RemotePathRules
import com.shinpstudio.phonetransfer.domain.TextMessageRules
import com.shinpstudio.phonetransfer.protocol.FileEntry
import com.shinpstudio.phonetransfer.protocol.Share
import com.shinpstudio.phonetransfer.transfer.FileTransferService
import com.shinpstudio.phonetransfer.transfer.TransferKind
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
import kotlinx.coroutines.withContext

data class HomeState(
    val connectionLabel: String = "PC未接続",
    val busy: Boolean = false,
    val comparisonCode: String? = null,
    val pcs: List<SavedPc> = emptyList(),
    val activePcId: String? = null,
    val share: Share? = null,
    val entries: List<FileEntry> = emptyList(),
    val currentPath: String = "",
    val transfer: TransferServiceState = TransferServiceState.Idle
)

class HomeViewModel(application: Application) : AndroidViewModel(application) {
    private val repository = PairingRepository(application)
    private val fileRepository = FileTransferRepository(application)
    private val textRepository = TextMessageRepository(application)
    private val discovery = NsdDiscovery(application)
    private val transferOperations = TransferOperationStore.get(application)
    private val mutableState = MutableStateFlow(HomeState())
    val state = mutableState.asStateFlow()
    private var operation: Job? = null

    init {
        runOperation {
            restoreInterruptedTransfer()
            mutableState.update { it.copy(pcs = repository.saved()) }
        }
        viewModelScope.launch {
            TransferStatusBus.state.collect { transfer ->
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

    fun upload(uri: Uri) {
        val current = state.value
        val share = current.share ?: return
        val deviceId = current.activePcId ?: return
        if (!share.writable || current.transfer.blocksNewTransfer()) return
        try {
            FileTransferService.startUpload(
                getApplication(),
                deviceId,
                share.id,
                current.currentPath,
                uri
            )
            mutableState.update {
                it.copy(connectionLabel = "PCへの送信を開始しました")
            }
        } catch (_: RuntimeException) {
            mutableState.update {
                it.copy(connectionLabel = "ファイル転送サービスを開始できませんでした")
            }
        }
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
            (blockWhenTransferPending && state.value.transfer.blocksNewTransfer())
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
}
