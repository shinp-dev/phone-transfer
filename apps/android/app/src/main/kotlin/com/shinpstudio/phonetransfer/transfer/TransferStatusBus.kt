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

object TransferStatusBus {
    private val mutableState = MutableStateFlow<TransferServiceState>(TransferServiceState.Idle)
    val state = mutableState.asStateFlow()

    fun begin(operationId: String, kind: TransferKind) {
        if (mutableState.value is TransferServiceState.RecoveryBlocked) return
        mutableState.value = TransferServiceState.Running(operationId, kind, 0, 0)
    }

    fun recoveryBlocked(code: String) {
        mutableState.value = TransferServiceState.RecoveryBlocked(code)
    }

    fun progress(operationId: String, kind: TransferKind, transferred: Long, total: Long) {
        val current = mutableState.value
        if (current is TransferServiceState.Running && current.operationId == operationId) {
            mutableState.value =
                TransferServiceState.Running(operationId, kind, transferred, total)
        }
    }

    fun resumable(
        operationId: String,
        kind: TransferKind,
        transferred: Long,
        total: Long,
        reason: String,
        canResume: Boolean = true
    ) {
        val current = mutableState.value
        if (current is TransferServiceState.RecoveryBlocked) return
        if (
            current is TransferServiceState.Running && current.operationId != operationId ||
            current is TransferServiceState.Resumable && current.operationId != operationId
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
                canResume
            )
    }

    fun complete(operationId: String, kind: TransferKind, fileName: String) {
        val current = mutableState.value
        if (
            current is TransferServiceState.Running && current.operationId == operationId ||
            current is TransferServiceState.Resumable && current.operationId == operationId
        ) {
            mutableState.value = TransferServiceState.Completed(operationId, kind, fileName)
        }
    }

    fun fail(operationId: String, kind: TransferKind, code: String) {
        val current = mutableState.value
        if (
            current is TransferServiceState.Running && current.operationId == operationId ||
            current is TransferServiceState.Resumable && current.operationId == operationId
        ) {
            mutableState.value = TransferServiceState.Failed(operationId, kind, code)
        }
    }

    fun cancel(operationId: String, kind: TransferKind) {
        val current = mutableState.value
        if (
            current is TransferServiceState.Running && current.operationId == operationId ||
            current is TransferServiceState.Resumable && current.operationId == operationId
        ) {
            mutableState.value = TransferServiceState.Cancelled(operationId, kind)
        }
    }
}
