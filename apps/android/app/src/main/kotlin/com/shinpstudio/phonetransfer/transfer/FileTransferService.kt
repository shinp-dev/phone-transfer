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
import com.shinpstudio.phonetransfer.R
import com.shinpstudio.phonetransfer.data.FileTransferException
import com.shinpstudio.phonetransfer.data.FileTransferRepository
import com.shinpstudio.phonetransfer.data.SavedPcStore
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class FileTransferService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var transferJob: Job? = null
    private var operationId: String? = null
    private var operationKind: TransferKind? = null
    private var lastNotificationAt = 0L

    override fun onCreate() {
        super.onCreate()
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
        if (intent?.action == ACTION_CANCEL) {
            transferJob?.cancel(CancellationException("USER_CANCELLED"))
            if (transferJob?.isActive != true) stopSelf()
            return START_NOT_STICKY
        }
        if (intent?.action !in setOf(ACTION_UPLOAD, ACTION_DOWNLOAD)) {
            stopSelf(startId)
            return START_NOT_STICKY
        }
        if (transferJob?.isActive == true) return START_NOT_STICKY

        val currentOperation =
            intent.getStringExtra(EXTRA_OPERATION_ID) ?: UUID.randomUUID().toString()
        val kind =
            if (intent.action == ACTION_UPLOAD) {
                TransferKind.Upload
            } else {
                TransferKind.Download
            }
        operationId = currentOperation
        operationKind = kind
        TransferStatusBus.begin(currentOperation, kind)
        startForegroundCompat(notification(kind, 0, 0, "転送を準備しています"))

        transferJob =
            scope.launch {
                try {
                    runTransfer(intent, currentOperation, kind)
                } catch (error: CancellationException) {
                    TransferStatusBus.cancel(currentOperation, kind)
                } catch (error: FileTransferException) {
                    TransferStatusBus.fail(currentOperation, kind, error.code)
                } catch (_: SecurityException) {
                    TransferStatusBus.fail(currentOperation, kind, "SAF_PERMISSION_DENIED")
                } catch (_: Exception) {
                    TransferStatusBus.fail(currentOperation, kind, "TRANSFER_FAILED")
                } finally {
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    stopSelf()
                }
            }
        return START_NOT_STICKY
    }

    private suspend fun runTransfer(
        intent: Intent,
        currentOperation: String,
        kind: TransferKind
    ) {
        val deviceId = intent.requireString(EXTRA_DEVICE_ID)
        val shareId = intent.requireString(EXTRA_SHARE_ID)
        val pc =
            SavedPcStore.get(this).read().firstOrNull { it.deviceId == deviceId }
                ?: throw FileTransferException(
                    "PC_NOT_FOUND",
                    false,
                    "The paired PC is no longer saved."
                )
        val repository = FileTransferRepository(this)

        if (kind == TransferKind.Upload) {
            val directory = intent.requireString(EXTRA_REMOTE_PATH)
            val uri = Uri.parse(intent.requireString(EXTRA_URI))
            val persisted = takePersistable(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
            try {
                val result =
                    repository.upload(pc, shareId, directory, uri) { done, total ->
                        publishProgress(currentOperation, kind, done, total)
                    }
                TransferStatusBus.complete(currentOperation, kind, result.fileName)
            } finally {
                if (persisted) {
                    releasePersistable(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
                }
            }
        } else {
            val remotePath = intent.requireString(EXTRA_REMOTE_PATH)
            val uri = Uri.parse(intent.requireString(EXTRA_URI))
            val persisted = takePersistable(uri, Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            try {
                val result =
                    repository.download(pc, shareId, remotePath, uri) { done, total ->
                        publishProgress(currentOperation, kind, done, total)
                    }
                TransferStatusBus.complete(currentOperation, kind, result.fileName)
            } finally {
                if (persisted) {
                    releasePersistable(uri, Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
                }
            }
        }
    }

    private fun publishProgress(
        operationId: String,
        kind: TransferKind,
        done: Long,
        total: Long
    ) {
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
        val cancelIntent = Intent(this, FileTransferService::class.java).setAction(ACTION_CANCEL)
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

    private fun takePersistable(uri: Uri, flags: Int): Boolean =
        try {
            contentResolver.takePersistableUriPermission(uri, flags)
            true
        } catch (_: SecurityException) {
            false
        }

    private fun releasePersistable(uri: Uri, flags: Int) {
        try {
            contentResolver.releasePersistableUriPermission(uri, flags)
        } catch (_: SecurityException) {
            // Temporary URI grants remain sufficient while the foreground service is alive.
        }
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        val currentOperation = operationId
        val kind = operationKind
        if (currentOperation != null && kind != null) {
            TransferStatusBus.fail(currentOperation, kind, "FOREGROUND_SERVICE_TIMEOUT")
        }
        transferJob?.cancel(CancellationException("FOREGROUND_SERVICE_TIMEOUT"))
        stopSelf(startId)
    }

    override fun onDestroy() {
        transferJob?.cancel()
        scope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun Intent.requireString(name: String): String =
        getStringExtra(name)?.takeIf { it.isNotEmpty() }
            ?: throw FileTransferException(
                "INVALID_TRANSFER_INTENT",
                false,
                "The transfer request is incomplete."
            )

    companion object {
        private const val CHANNEL_ID = "file-transfer"
        private const val NOTIFICATION_ID = 58443
        private const val NOTIFICATION_INTERVAL_MS = 500L
        private const val ACTION_UPLOAD = "com.shinpstudio.phonetransfer.action.UPLOAD"
        private const val ACTION_DOWNLOAD = "com.shinpstudio.phonetransfer.action.DOWNLOAD"
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
            source: Uri
        ) {
            val intent =
                Intent(context, FileTransferService::class.java)
                    .setAction(ACTION_UPLOAD)
                    .putExtra(EXTRA_OPERATION_ID, UUID.randomUUID().toString())
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

        fun cancel(context: Context) {
            context.startService(
                Intent(context, FileTransferService::class.java).setAction(ACTION_CANCEL)
            )
        }
    }
}
