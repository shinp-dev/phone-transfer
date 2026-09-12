package com.shinpstudio.phonetransfer.transfer

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.IBinder
import android.os.SystemClock
import androidx.core.net.toUri
import com.shinpstudio.phonetransfer.R
import com.shinpstudio.phonetransfer.data.CancellationResult
import com.shinpstudio.phonetransfer.data.DurableTransferKind
import com.shinpstudio.phonetransfer.data.DurableUploadRepository
import com.shinpstudio.phonetransfer.data.DurableUploadSource
import com.shinpstudio.phonetransfer.data.FileTransferException
import com.shinpstudio.phonetransfer.data.FileTransferRepository
import com.shinpstudio.phonetransfer.data.PairingRepository
import com.shinpstudio.phonetransfer.data.PersistedTransferOperation
import com.shinpstudio.phonetransfer.data.SavedPc
import com.shinpstudio.phonetransfer.data.SavedPcStore
import com.shinpstudio.phonetransfer.data.TransferOperationStore
import com.shinpstudio.phonetransfer.data.UploadCancellationRecovery
import com.shinpstudio.phonetransfer.data.validateUploadResponse
import com.shinpstudio.phonetransfer.data.verifyRecoveryPc
import com.shinpstudio.phonetransfer.domain.LocalUploadCheckpoint
import com.shinpstudio.phonetransfer.domain.ServerUploadStatus
import com.shinpstudio.phonetransfer.domain.UploadRecoveryDecision
import com.shinpstudio.phonetransfer.domain.UploadRecoveryRules
import com.shinpstudio.phonetransfer.protocol.Transfer
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class FileTransferService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private lateinit var operationStore: TransferOperationStore
    private var transferJob: Job? = null
    private var operationId: String? = null
    private var lastNotificationAt = 0L

    override fun onCreate() {
        super.onCreate()
        operationStore = TransferOperationStore.get(this)
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                "ファイル転送",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "PCとのファイル転送の進捗を表示します"
                setShowBadge(false)
            }
        )
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val request = intent
        if (request == null) {
            stopSelf(startId)
            return START_NOT_STICKY
        }
        if (request.action == ACTION_CANCEL) {
            handleCancel(request, startId)
            return START_NOT_STICKY
        }
        if (request.action !in setOf(ACTION_UPLOAD, ACTION_DOWNLOAD, ACTION_RESUME)) {
            stopSelf(startId)
            return START_NOT_STICKY
        }
        if (transferJob?.isCompleted == false) return START_NOT_STICKY

        val currentOperation = request.requireString(EXTRA_OPERATION_ID)
        val kind =
            if (request.action == ACTION_RESUME) {
                val persisted = safeFind(currentOperation)
                    ?: run {
                        stopSelf(startId)
                        return START_NOT_STICKY
                    }
                persisted.kind.toTransferKind()
            } else if (request.action == ACTION_UPLOAD) {
                TransferKind.Upload
            } else {
                TransferKind.Download
            }
        operationId = currentOperation
        if (!TransferStatusBus.begin(currentOperation, kind)) {
            stopSelf(startId)
            return START_NOT_STICKY
        }
        TransferRuntimeBus.begin(currentOperation)
        startForegroundCompat(notification(kind, 0, 0, "転送を準備しています"))

        // Commit identity before another main-thread command can request cancellation.
        val prepared = try {
            if (request.action == ACTION_RESUME) {
                requireOperation(currentOperation)
            } else {
                persistNewOperation(request, currentOperation, kind)
            }
        } catch (_: Exception) {
            TransferStatusBus.recoveryBlocked("LOCAL_JOURNAL_UNAVAILABLE")
            TransferRuntimeBus.finish(currentOperation)
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf(startId)
            return START_NOT_STICKY
        }
        transferJob =
            scope.launch(start = CoroutineStart.UNDISPATCHED) {
                try {
                    withContext(Dispatchers.IO) {
                        val persisted = if (request.action == ACTION_RESUME) {
                            requireOperation(currentOperation)
                        } else {
                            acquireGrant(prepared)
                        }
                        if (persisted.completedFileName != null) {
                            TransferStatusBus.complete(
                                currentOperation,
                                kind,
                                persisted.completedFileName
                            )
                        } else if (persisted.cancelRequested) {
                            if (finishCancellation(persisted)) {
                                TransferStatusBus.cancel(currentOperation, kind)
                            } else {
                                publishResumable(persisted, "CANCEL_PENDING", canResume = false)
                            }
                        } else {
                            runTransfer(persisted, request.action == ACTION_RESUME)
                        }
                    }
                } catch (error: CancellationException) {
                    val current = safeFind(currentOperation)
                    if (current?.cancelRequested == true) {
                        val converged = finishCancellation(current)
                        if (converged) {
                            TransferStatusBus.cancel(currentOperation, kind)
                        } else {
                            publishResumable(current, "CANCEL_PENDING", canResume = false)
                        }
                    } else if (current != null) {
                        publishResumable(current, "PROCESS_INTERRUPTED")
                    }
                } catch (error: FileTransferException) {
                    val current = safeFind(currentOperation)
                    if (current != null && error.retryable && !current.cancelRequested) {
                        publishResumable(current, error.code)
                    } else if (current != null) {
                        val converged = terminalize(current)
                        if (!converged) {
                            publishResumable(
                                current,
                                "CANCEL_PENDING_${error.code}",
                                canResume = false
                            )
                        } else {
                            TransferStatusBus.fail(currentOperation, kind, error.code)
                        }
                    } else {
                        TransferStatusBus.fail(currentOperation, kind, error.code)
                    }
                } catch (error: IOException) {
                    val current = safeFind(currentOperation)
                    if (current != null && !current.cancelRequested) {
                        publishResumable(current, "CONNECTION_FAILED")
                    } else if (current != null) {
                        publishResumable(current, "CANCEL_PENDING", canResume = false)
                    } else {
                        TransferStatusBus.fail(currentOperation, kind, "CONNECTION_FAILED")
                    }
                } catch (_: SecurityException) {
                    handleTerminalFailure(currentOperation, kind, "SAF_PERMISSION_DENIED")
                } catch (_: Exception) {
                    handleTerminalFailure(currentOperation, kind, "TRANSFER_FAILED")
                } finally {
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    stopSelf()
                }
            }
        transferJob?.invokeOnCompletion { TransferRuntimeBus.finish(currentOperation) }
        return START_NOT_STICKY
    }

    private suspend fun runTransfer(operation: PersistedTransferOperation, resumed: Boolean) {
        if (resumed && operation.kind == DurableTransferKind.Download) {
            throw FileTransferException(
                "DOWNLOAD_RESUME_UNSUPPORTED",
                false,
                "Choose a new destination for the interrupted download."
            )
        }
        val pc =
            SavedPcStore.get(this).read().firstOrNull { it.deviceId == operation.deviceId }
                ?: throw FileTransferException(
                    "PC_NOT_FOUND",
                    false,
                    "The paired PC is no longer saved."
                )
        val uri = operation.uri.toUri()
        if (resumed) verifyPcForResume(pc)

        if (operation.kind == DurableTransferKind.Upload) {
            if (resumed && tryConvergeUploadWithoutSource(operation, pc)) return
            val current = if (resumed) requireOperation(operation.operationId) else operation
            if (resumed) requirePersistedGrant(current, uri)
            runUpload(current, pc, uri)
        } else {
            if (resumed) requirePersistedGrant(operation, uri)
            runDownload(operation, pc, uri)
        }
    }

    private suspend fun tryConvergeUploadWithoutSource(
        operation: PersistedTransferOperation,
        pc: SavedPc
    ): Boolean {
        val name = operation.sourceName ?: return false
        val size = operation.totalSize ?: return false
        val hash = operation.sha256 ?: return false
        val repository = DurableUploadRepository(this)
        val source = DurableUploadSource(name, size, hash)
        var current = operation
        val server =
            if (current.serverTransferId == null) {
                val created = repository.createOrRecover(
                    pc,
                    current.shareId,
                    current.remotePath,
                    source,
                    checkNotNull(current.idempotencyKey)
                )
                current = persist(
                    current.withServer(
                        created.transferId,
                        created.transferredBytes,
                        System.currentTimeMillis()
                    )
                )
                created
            } else {
                repository.status(pc, current.serverTransferId)
            }

        return when (val decision = reconcile(current, server)) {
            UploadRecoveryDecision.Completed -> {
                finishSuccess(current, server.fileName)
                true
            }

            is UploadRecoveryDecision.Terminal -> {
                throw FileTransferException(
                    decision.code,
                    false,
                    "The persisted upload cannot be resumed safely."
                )
            }

            is UploadRecoveryDecision.Resume -> {
                if (decision.offset > current.committedOffset) {
                    persist(current.withOffset(decision.offset, System.currentTimeMillis()))
                }
                false
            }
        }
    }

    private suspend fun runUpload(initial: PersistedTransferOperation, pc: SavedPc, uri: Uri) {
        val repository = DurableUploadRepository(this)
        var current = initial
        val source = repository.inspectSource(uri)
        current = bindAndValidateSource(current, source)
        publishProgress(
            current.operationId,
            TransferKind.Upload,
            current.committedOffset,
            source.size
        )

        val server =
            if (current.serverTransferId == null) {
                val created = repository.createOrRecover(
                    pc,
                    current.shareId,
                    current.remotePath,
                    source,
                    checkNotNull(current.idempotencyKey)
                )
                current = persist(
                    current.withServer(
                        created.transferId,
                        created.transferredBytes,
                        System.currentTimeMillis()
                    )
                )
                created
            } else {
                repository.status(pc, current.serverTransferId)
            }

        when (val decision = reconcile(current, server)) {
            UploadRecoveryDecision.Completed -> {
                finishSuccess(current, source.name)
                return
            }

            is UploadRecoveryDecision.Terminal -> {
                throw FileTransferException(
                    decision.code,
                    false,
                    "The persisted upload cannot be resumed safely."
                )
            }

            is UploadRecoveryDecision.Resume -> {
                if (decision.offset > current.committedOffset) {
                    current = persist(
                        current.withOffset(decision.offset, System.currentTimeMillis())
                    )
                }
                val result = repository.resume(
                    pc,
                    checkNotNull(current.serverTransferId),
                    uri,
                    source,
                    decision.offset,
                    { done, total ->
                        publishProgress(current.operationId, TransferKind.Upload, done, total)
                    },
                    { committed ->
                        current = persist(
                            current.withOffset(committed, System.currentTimeMillis())
                        )
                    }
                )
                if (result.status != "completed") {
                    throw FileTransferException(
                        "UNEXPECTED_TRANSFER_STATE",
                        true,
                        "The upload did not converge to completed."
                    )
                }
                finishSuccess(current, result.fileName)
            }
        }
    }

    private suspend fun runDownload(operation: PersistedTransferOperation, pc: SavedPc, uri: Uri) {
        val repository = FileTransferRepository(this)
        val result = repository.download(pc, operation.shareId, operation.remotePath, uri) {
                done,
                total
            ->
            publishProgress(operation.operationId, TransferKind.Download, done, total)
        }
        finishSuccess(operation, result.fileName)
    }

    private fun bindAndValidateSource(
        operation: PersistedTransferOperation,
        source: DurableUploadSource
    ): PersistedTransferOperation {
        if (operation.sourceName == null) {
            return persist(
                operation.withSource(
                    source.name,
                    source.size,
                    source.sha256,
                    System.currentTimeMillis()
                )
            )
        }
        val local = operation.localCheckpoint()
        if (!UploadRecoveryRules.sourceMatches(local, source.name, source.size, source.sha256)) {
            throw FileTransferException(
                "SOURCE_CHANGED",
                false,
                "The selected source changed after the operation was persisted."
            )
        }
        return operation
    }

    private fun reconcile(
        operation: PersistedTransferOperation,
        server: Transfer
    ): UploadRecoveryDecision {
        validateUploadResponse(operation, server)
        return UploadRecoveryRules.reconcile(
            operation.localCheckpoint(),
            ServerUploadStatus(
                server.fileName,
                server.totalSize,
                server.sha256,
                server.transferredBytes,
                server.status
            )
        )
    }

    private fun PersistedTransferOperation.localCheckpoint(): LocalUploadCheckpoint =
        LocalUploadCheckpoint(
            checkNotNull(sourceName),
            checkNotNull(totalSize),
            checkNotNull(sha256),
            committedOffset,
            cancelRequested
        )

    private suspend fun verifyPcForResume(pc: SavedPc) {
        verifyRecoveryPc { PairingRepository(this).connect(pc) }
    }

    private fun persistNewOperation(
        intent: Intent,
        currentOperation: String,
        kind: TransferKind
    ): PersistedTransferOperation {
        val deviceId = intent.requireString(EXTRA_DEVICE_ID)
        val shareId = intent.requireString(EXTRA_SHARE_ID)
        val remotePath = intent.requireString(
            EXTRA_REMOTE_PATH,
            allowEmpty = kind == TransferKind.Upload
        )
        val uri = intent.requireString(EXTRA_URI).toUri()
        val now = System.currentTimeMillis()
        val operation =
            if (kind == TransferKind.Upload) {
                PersistedTransferOperation.upload(
                    currentOperation,
                    deviceId,
                    shareId,
                    remotePath,
                    uri.toString(),
                    UUID.randomUUID().toString(),
                    now
                )
            } else {
                PersistedTransferOperation.download(
                    currentOperation,
                    deviceId,
                    shareId,
                    remotePath,
                    uri.toString(),
                    now
                )
            }
        return try {
            operationStore.insert(operation)
        } catch (error: Exception) {
            throw FileTransferException(
                "LOCAL_JOURNAL_UNAVAILABLE",
                true,
                "The transfer operation could not be persisted.",
                error
            )
        }
    }

    private fun acquireGrant(operation: PersistedTransferOperation): PersistedTransferOperation {
        if (operation.cancelRequested) return operation
        val uri = operation.uri.toUri()
        val grant = grantFlag(operation.kind)
        if (!takePersistable(uri, grant)) return requireOperation(operation.operationId)
        return try {
            persist(operation.withGrant(true, System.currentTimeMillis()))
        } catch (error: FileTransferException) {
            releasePersistable(uri, grant)
            throw error
        }
    }

    private fun persist(operation: PersistedTransferOperation): PersistedTransferOperation = try {
        operationStore.replace(operation)
    } catch (error: Exception) {
        throw FileTransferException(
            "LOCAL_JOURNAL_UNAVAILABLE",
            true,
            "The transfer checkpoint could not be persisted.",
            error
        )
    }

    private fun requireOperation(operationId: String): PersistedTransferOperation =
        safeFind(operationId)
            ?: throw FileTransferException(
                "RECOVERY_OPERATION_NOT_FOUND",
                false,
                "The interrupted transfer no longer exists."
            )

    private fun safeFind(operationId: String): PersistedTransferOperation? = try {
        operationStore.find(operationId)
    } catch (_: Exception) {
        TransferStatusBus.recoveryBlocked("LOCAL_JOURNAL_INVALID")
        null
    }

    private fun requirePersistedGrant(operation: PersistedTransferOperation, uri: Uri) {
        if (!hasPersistedGrant(uri, grantFlag(operation.kind))) {
            throw FileTransferException(
                "SAF_PERMISSION_LOST",
                false,
                "The persisted SAF permission required to resume is unavailable."
            )
        }
    }

    private fun hasPersistedGrant(uri: Uri, flag: Int): Boolean =
        contentResolver.persistedUriPermissions.any { permission ->
            permission.uri == uri &&
                if (flag == Intent.FLAG_GRANT_READ_URI_PERMISSION) {
                    permission.isReadPermission
                } else {
                    permission.isWritePermission
                }
        }

    private suspend fun handleTerminalFailure(
        currentOperation: String,
        kind: TransferKind,
        code: String
    ) {
        val current = safeFind(currentOperation)
        if (current != null && !terminalize(current)) {
            publishResumable(current, "CANCEL_PENDING_$code", canResume = false)
        } else {
            TransferStatusBus.fail(currentOperation, kind, code)
        }
    }

    private suspend fun terminalize(operation: PersistedTransferOperation): Boolean =
        finishCancellation(operation)

    private suspend fun finishCancellation(operation: PersistedTransferOperation): Boolean {
        val recovery = UploadCancellationRecovery(
            operationStore,
            { deviceId -> SavedPcStore.get(this).read().firstOrNull { it.deviceId == deviceId } },
            { pc -> verifyPcForResume(pc) },
            DurableUploadRepository(this, callTimeoutMillis = 10_000)
        )
        return when (val result = recovery.cancel(operation.operationId)) {
            CancellationResult.Pending -> false
            CancellationResult.Cancelled -> {
                cleanupGrant(operation)
                true
            }
            is CancellationResult.Completed -> {
                cleanupGrant(operation)
                TransferStatusBus.complete(
                    operation.operationId,
                    operation.kind.toTransferKind(),
                    result.fileName
                )
                true
            }
        }
    }

    private fun finishSuccess(operation: PersistedTransferOperation, fileName: String) {
        try {
            operationStore.complete(operation.operationId, fileName, System.currentTimeMillis())
        } catch (error: Exception) {
            throw FileTransferException(
                "LOCAL_JOURNAL_UNAVAILABLE",
                true,
                "The completion receipt could not be persisted.",
                error
            )
        }
        TransferStatusBus.complete(operation.operationId, operation.kind.toTransferKind(), fileName)
        cleanupGrant(operation)
    }

    private fun cleanupGrant(operation: PersistedTransferOperation) {
        try {
            val uri = operation.uri.toUri()
            val grant = grantFlag(operation.kind)
            if (hasPersistedGrant(uri, grant)) releasePersistable(uri, grant)
        } catch (_: Exception) {
            // Terminal authority is already durable. Grant cleanup is best effort.
        }
    }

    private fun handleCancel(intent: Intent, startId: Int) {
        val target = intent.getStringExtra(EXTRA_OPERATION_ID)
            ?: operationId
            ?: try {
                operationStore.read().firstOrNull()?.operationId
            } catch (_: Exception) {
                null
            }
        if (target == null) {
            stopSelf(startId)
            return
        }
        val marked = try {
            operationStore.requestCancel(target, System.currentTimeMillis())
        } catch (_: Exception) {
            TransferStatusBus.recoveryBlocked("LOCAL_JOURNAL_UNAVAILABLE")
            transferJob?.cancel(CancellationException("LOCAL_JOURNAL_UNAVAILABLE"))
            null
        }
        if (marked == null) {
            if (transferJob?.isCompleted != false) stopSelf(startId)
            return
        }
        if (transferJob?.isCompleted == false) {
            if (operationId == target && transferJob?.isActive == true) {
                transferJob?.cancel(CancellationException("USER_CANCELLED"))
            }
            return
        }
        publishResumable(marked, "CANCEL_PENDING", canResume = false)
        operationId = target
        TransferRuntimeBus.begin(target)
        transferJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
            try {
                withContext(Dispatchers.IO) {
                    if (finishCancellation(marked)) {
                        TransferStatusBus.cancel(target, marked.kind.toTransferKind())
                    } else {
                        publishResumable(marked, "CANCEL_PENDING", canResume = false)
                    }
                }
            } finally {
                stopSelf(startId)
            }
        }
        transferJob?.invokeOnCompletion { TransferRuntimeBus.finish(target) }
    }

    private fun publishResumable(
        operation: PersistedTransferOperation,
        reason: String,
        canResume: Boolean = true
    ) {
        val latest = safeFind(operation.operationId) ?: return
        latest.completedFileName?.let {
            TransferStatusBus.complete(latest.operationId, latest.kind.toTransferKind(), it)
            TransferStatusBus.restoreCompleted(latest.operationId, latest.kind.toTransferKind(), it)
            return
        }
        val grantAvailable = try {
            hasPersistedGrant(latest.uri.toUri(), grantFlag(latest.kind))
        } catch (_: Exception) {
            false
        }
        TransferStatusBus.resumable(
            operation.operationId,
            operation.kind.toTransferKind(),
            latest.committedOffset,
            latest.totalSize ?: 0L,
            reason,
            canResume && grantAvailable && !latest.cancelRequested
        )
    }

    private fun publishProgress(operationId: String, kind: TransferKind, done: Long, total: Long) {
        TransferStatusBus.progress(operationId, kind, done, total)
        val now = SystemClock.elapsedRealtime()
        if (done == total || now - lastNotificationAt >= NOTIFICATION_INTERVAL_MS) {
            lastNotificationAt = now
            val text = if (kind == TransferKind.Upload) "PCへ送信中" else "PCから受信中"
            getSystemService(NotificationManager::class.java).notify(
                NOTIFICATION_ID,
                notification(kind, done, total, text)
            )
        }
    }

    private fun notification(
        kind: TransferKind,
        done: Long,
        total: Long,
        text: String
    ): Notification {
        val currentOperation = operationId
        val cancelIntent = Intent(this, FileTransferService::class.java)
            .setAction(ACTION_CANCEL)
        if (currentOperation != null) cancelIntent.putExtra(EXTRA_OPERATION_ID, currentOperation)
        val cancel =
            PendingIntent.getService(
                this,
                0,
                cancelIntent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
        val determinate = total > 0
        val progress =
            if (determinate) {
                ((done.coerceIn(0, total) * 1000) / total).toInt()
            } else {
                0
            }
        val title =
            if (kind == TransferKind.Upload) {
                "Phone Transfer - 送信"
            } else {
                "Phone Transfer - 受信"
            }
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_transfer)
            .setContentTitle(title)
            .setContentText(text)
            .setCategory(Notification.CATEGORY_PROGRESS)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setProgress(1000, progress, !determinate)
            .addAction(
                Notification.Action.Builder(
                    R.drawable.ic_transfer,
                    "中止",
                    cancel
                ).build()
            ).build()
    }

    private fun startForegroundCompat(notification: Notification) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(
                NOTIFICATION_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun takePersistable(uri: Uri, flags: Int): Boolean = try {
        contentResolver.takePersistableUriPermission(uri, flags)
        true
    } catch (_: SecurityException) {
        false
    }

    private fun releasePersistable(uri: Uri, flags: Int) {
        try {
            contentResolver.releasePersistableUriPermission(uri, flags)
        } catch (_: SecurityException) {
            // A provider may already have revoked the grant. Local state is already terminal.
        }
    }

    private fun grantFlag(kind: DurableTransferKind): Int =
        if (kind == DurableTransferKind.Upload) {
            Intent.FLAG_GRANT_READ_URI_PERMISSION
        } else {
            Intent.FLAG_GRANT_WRITE_URI_PERMISSION
        }

    private fun DurableTransferKind.toTransferKind(): TransferKind =
        if (this == DurableTransferKind.Upload) TransferKind.Upload else TransferKind.Download

    override fun onTimeout(startId: Int, fgsType: Int) {
        transferJob?.cancel(CancellationException("FOREGROUND_SERVICE_TIMEOUT"))
        stopSelf()
    }

    override fun onDestroy() {
        transferJob?.cancel(CancellationException("SERVICE_DESTROYED"))
        scope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun Intent.requireString(name: String, allowEmpty: Boolean = false): String =
        requireIntentStringValue(getStringExtra(name), allowEmpty)

    companion object {
        private const val CHANNEL_ID = "file-transfer"
        private const val NOTIFICATION_ID = 58443
        private const val NOTIFICATION_INTERVAL_MS = 500L
        private const val ACTION_UPLOAD = "com.shinpstudio.phonetransfer.action.UPLOAD"
        private const val ACTION_DOWNLOAD = "com.shinpstudio.phonetransfer.action.DOWNLOAD"
        private const val ACTION_RESUME = "com.shinpstudio.phonetransfer.action.RESUME_TRANSFER"
        private const val ACTION_CANCEL = "com.shinpstudio.phonetransfer.action.CANCEL_TRANSFER"
        private const val EXTRA_OPERATION_ID = "operationId"
        private const val EXTRA_DEVICE_ID = "deviceId"
        private const val EXTRA_SHARE_ID = "shareId"
        private const val EXTRA_REMOTE_PATH = "remotePath"
        private const val EXTRA_URI = "uri"

        fun startUpload(
            context: Context,
            deviceId: String,
            shareId: String,
            directory: String,
            source: Uri,
            operationId: String = UUID.randomUUID().toString()
        ) {
            val intent =
                Intent(context, FileTransferService::class.java)
                    .setAction(ACTION_UPLOAD)
                    .putExtra(EXTRA_OPERATION_ID, operationId)
                    .putExtra(EXTRA_DEVICE_ID, deviceId)
                    .putExtra(EXTRA_SHARE_ID, shareId)
                    .putExtra(EXTRA_REMOTE_PATH, directory)
                    .putExtra(EXTRA_URI, source.toString())
            context.startForegroundService(intent)
        }

        fun startDownload(
            context: Context,
            deviceId: String,
            shareId: String,
            remotePath: String,
            destination: Uri
        ) {
            val intent =
                Intent(context, FileTransferService::class.java)
                    .setAction(ACTION_DOWNLOAD)
                    .putExtra(EXTRA_OPERATION_ID, UUID.randomUUID().toString())
                    .putExtra(EXTRA_DEVICE_ID, deviceId)
                    .putExtra(EXTRA_SHARE_ID, shareId)
                    .putExtra(EXTRA_REMOTE_PATH, remotePath)
                    .putExtra(EXTRA_URI, destination.toString())
            context.startForegroundService(intent)
        }

        fun resume(context: Context, operationId: String) {
            context.startForegroundService(
                Intent(context, FileTransferService::class.java)
                    .setAction(ACTION_RESUME)
                    .putExtra(EXTRA_OPERATION_ID, operationId)
            )
        }

        fun cancel(context: Context, operationId: String? = null) {
            val intent = Intent(context, FileTransferService::class.java).setAction(ACTION_CANCEL)
            if (operationId != null) intent.putExtra(EXTRA_OPERATION_ID, operationId)
            context.startService(intent)
        }
    }
}

internal fun requireIntentStringValue(value: String?, allowEmpty: Boolean = false): String =
    value?.takeIf { allowEmpty || it.isNotEmpty() }
        ?: throw FileTransferException(
            "INVALID_TRANSFER_INTENT",
            false,
            "The transfer request is incomplete."
        )
