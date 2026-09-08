package com.shinpstudio.phonetransfer.transfer

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TransferStatusBusTest {
    @Test
    fun restartRestoreDoesNotDowngradeALiveRunningTransfer() {
        TransferStatusBus.begin(OPERATION_ID, TransferKind.Upload)

        TransferStatusBus.restoreResumable(
            OPERATION_ID,
            TransferKind.Upload,
            transferred = 4,
            total = 8,
            reason = "PROCESS_INTERRUPTED"
        )

        assertTrue(TransferStatusBus.state.value is TransferServiceState.Running)
    }

    @Test
    fun aDifferentPendingOperationCannotBeHiddenByBegin() {
        TransferStatusBus.resumable(
            OPERATION_ID,
            TransferKind.Upload,
            transferred = 4,
            total = 8,
            reason = "PROCESS_INTERRUPTED"
        )

        TransferStatusBus.begin(OTHER_OPERATION_ID, TransferKind.Upload)

        val current = TransferStatusBus.state.value as TransferServiceState.Resumable
        assertTrue(current.operationId == OPERATION_ID)
    }

    @Test
    fun interruptedDownloadNeverAdvertisesUnsafeResume() {
        TransferStatusBus.resumable(
            OPERATION_ID,
            TransferKind.Download,
            transferred = 0,
            total = 0,
            reason = "PROCESS_INTERRUPTED",
            canResume = true
        )

        val download = TransferStatusBus.state.value as TransferServiceState.Resumable
        assertFalse(download.canResume)

        TransferStatusBus.resumable(
            OPERATION_ID,
            TransferKind.Upload,
            transferred = 4,
            total = 8,
            reason = "PROCESS_INTERRUPTED",
            canResume = true
        )

        val upload = TransferStatusBus.state.value as TransferServiceState.Resumable
        assertTrue(upload.canResume)
    }

    companion object {
        private const val OPERATION_ID = "11111111-1111-1111-1111-111111111111"
        private const val OTHER_OPERATION_ID = "22222222-2222-2222-2222-222222222222"
    }
}
