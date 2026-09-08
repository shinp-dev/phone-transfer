package com.shinpstudio.phonetransfer.transfer

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

enum class TransferKind {
    Upload,
    Download
}

sealed interface TransferServiceState {
    data object Idle : TransferServiceState

    data class Running(
        val operationId: String,
        val kind: TransferKind,
        val transferredBytes: Long,
        val totalBytes: Long
    ) : TransferServiceState

    data class Resumable(
        val operationId: String,
        val kind: TransferKind,
        val transferredBytes: Long,
        val totalBytes: Long,
        val reason: String,
        val canResume: Boolean = true
    ) : TransferServiceState

    data class RecoveryBlocked(val code: String) : TransferServiceState

    data class Completed(val operationId: String, val kind: TransferKind, val fileName: String) :
        TransferServiceState

    data class Failed(val operationId: String, val kind: TransferKind, val code: String) :
        TransferServiceState

    data class Cancelled(val operationId: String, val kind: TransferKind) : TransferServiceState
}

open class TransferStatusTracker {
    private val mutableState = MutableStateFlow<TransferServiceState>(TransferServiceState.Idle)
    val state = mutableState.asStateFlow()

    @Synchronized
    fun begin(operationId: String, kind: TransferKind): Boolean {
        val current = mutableState.value
        if (current is TransferServiceState.RecoveryBlocked) return false
        if (
            current is TransferServiceState.Running &&
            current.operationId != operationId ||
            current is TransferServiceState.Resumable &&
            current.operationId != operationId
        ) {
            return false
        }
        mutableState.value = TransferServiceState.Running(operationId, kind, 0, 0)
        return true
    }

    @Synchronized
    fun recoveryBlocked(code: String) {
        mutableState.value = TransferServiceState.RecoveryBlocked(code)
    }

    @Synchronized
    fun progress(operationId: String, kind: TransferKind, transferred: Long, total: Long) {
        val current = mutableState.value
        if (current is TransferServiceState.Running && current.operationId == operationId) {
            mutableState.value =
                TransferServiceState.Running(operationId, kind, transferred, total)
        }
    }

    @Synchronized
    fun restoreResumable(
        operationId: String,
        kind: TransferKind,
        transferred: Long,
        total: Long,
        reason: String,
        canResume: Boolean = true
    ) {
        val current = mutableState.value
        if (current is TransferServiceState.Running || current.isTerminal(operationId)) return
        resumable(operationId, kind, transferred, total, reason, canResume)
    }

    @Synchronized
    fun resumable(
        operationId: String,
        kind: TransferKind,
        transferred: Long,
        total: Long,
        reason: String,
        canResume: Boolean = true
    ) {
        val current = mutableState.value
        if (current is TransferServiceState.RecoveryBlocked ||
            current.isTerminal(operationId)
        ) {
            return
        }
        if (
            current is TransferServiceState.Running &&
            current.operationId != operationId ||
            current is TransferServiceState.Resumable &&
            current.operationId != operationId
        ) {
            return
        }
        mutableState.value =
            TransferServiceState.Resumable(
                operationId,
                kind,
                transferred,
                total,
                reason,
                canResume && kind == TransferKind.Upload
            )
    }

    @Synchronized
    fun restoreCompleted(operationId: String, kind: TransferKind, fileName: String) {
        val current = mutableState.value
        if (current is TransferServiceState.RecoveryBlocked) return
        if (current is TransferServiceState.Running) return
        if (current is TransferServiceState.Resumable && current.operationId != operationId) return
        if (current.isTerminal(operationId) && current !is TransferServiceState.Completed) return
        mutableState.value = TransferServiceState.Completed(operationId, kind, fileName)
    }

    @Synchronized
    fun complete(operationId: String, kind: TransferKind, fileName: String) {
        val current = mutableState.value
        if (
            current is TransferServiceState.Running &&
            current.operationId == operationId ||
            current is TransferServiceState.Resumable &&
            current.operationId == operationId
        ) {
            mutableState.value = TransferServiceState.Completed(operationId, kind, fileName)
        }
    }

    @Synchronized
    fun fail(operationId: String, kind: TransferKind, code: String) {
        val current = mutableState.value
        if (
            current is TransferServiceState.Running &&
            current.operationId == operationId ||
            current is TransferServiceState.Resumable &&
            current.operationId == operationId
        ) {
            mutableState.value = TransferServiceState.Failed(operationId, kind, code)
        }
    }

    @Synchronized
    fun cancel(operationId: String, kind: TransferKind) {
        val current = mutableState.value
        if (
            current is TransferServiceState.Running &&
            current.operationId == operationId ||
            current is TransferServiceState.Resumable &&
            current.operationId == operationId
        ) {
            mutableState.value = TransferServiceState.Cancelled(operationId, kind)
        }
    }

    private fun TransferServiceState.isTerminal(operationId: String): Boolean = when (this) {
        is TransferServiceState.Completed -> this.operationId == operationId
        is TransferServiceState.Cancelled -> this.operationId == operationId
        is TransferServiceState.Failed -> this.operationId == operationId
        else -> false
    }
}

object TransferStatusBus : TransferStatusTracker()
