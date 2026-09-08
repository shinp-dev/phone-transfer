package com.shinpstudio.phonetransfer.transfer

import org.junit.Assert.assertFalse
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TransferStatusBusTest {
    private val bus = TransferStatusTracker()

    @Test
    fun staleRestoreAndCancelCannotOverwriteCompletion() {
        bus.begin(OPERATION_ID, TransferKind.Upload)
        bus.complete(OPERATION_ID, TransferKind.Upload, "done.bin")
        bus.restoreResumable(OPERATION_ID, TransferKind.Upload, 4, 8, "stale")
        bus.cancel(OPERATION_ID, TransferKind.Upload)
        bus.fail(OPERATION_ID, TransferKind.Upload, "stale")
        assertTrue(bus.state.value is TransferServiceState.Completed)
    }

    @Test
    fun staleCallbacksCannotAffectAnotherOperation() {
        bus.begin(OPERATION_ID, TransferKind.Upload)
        bus.complete(OPERATION_ID, TransferKind.Upload, "done.bin")
        bus.begin(OTHER_OPERATION_ID, TransferKind.Upload)
        bus.progress(OPERATION_ID, TransferKind.Upload, 8, 8)
        bus.complete(OPERATION_ID, TransferKind.Upload, "done.bin")
        bus.cancel(OPERATION_ID, TransferKind.Upload)
        bus.fail(OPERATION_ID, TransferKind.Upload, "stale")
        bus.restoreResumable(OPERATION_ID, TransferKind.Upload, 4, 8, "stale")
        assertEquals(OTHER_OPERATION_ID, (bus.state.value as TransferServiceState.Running).operationId)
    }

    @Test
    fun corruptJournalBlocksAllCallbacksAndNewAdmission() {
        bus.recoveryBlocked("corrupt")
        assertFalse(bus.begin(OPERATION_ID, TransferKind.Upload))
        bus.restoreResumable(OPERATION_ID, TransferKind.Upload, 4, 8, "stale")
        bus.restoreCompleted(OPERATION_ID, TransferKind.Upload, "done.bin")
        bus.progress(OPERATION_ID, TransferKind.Upload, 8, 8)
        assertTrue(bus.state.value is TransferServiceState.RecoveryBlocked)
    }

    @Test
    fun startupReceiptRestoresCompletedWithoutAResumableIntermediateState() {
        bus.restoreCompleted(OPERATION_ID, TransferKind.Upload, "done.bin")
        assertTrue(bus.state.value is TransferServiceState.Completed)
        assertTrue(bus.begin(OTHER_OPERATION_ID, TransferKind.Upload))
    }
    @Test
    fun restartRestoreDoesNotDowngradeALiveRunningTransfer() {
        bus.begin(OPERATION_ID, TransferKind.Upload)

        bus.restoreResumable(
            OPERATION_ID,
            TransferKind.Upload,
            transferred = 4,
            total = 8,
            reason = "PROCESS_INTERRUPTED"
        )

        assertTrue(bus.state.value is TransferServiceState.Running)
    }

    @Test
    fun aDifferentPendingOperationCannotBeHiddenByBegin() {
        bus.resumable(
            OPERATION_ID,
            TransferKind.Upload,
            transferred = 4,
            total = 8,
            reason = "PROCESS_INTERRUPTED"
        )

        bus.begin(OTHER_OPERATION_ID, TransferKind.Upload)

        val current = bus.state.value as TransferServiceState.Resumable
        assertTrue(current.operationId == OPERATION_ID)
    }

    @Test
    fun interruptedDownloadNeverAdvertisesUnsafeResume() {
        bus.resumable(
            OPERATION_ID,
            TransferKind.Download,
            transferred = 0,
            total = 0,
            reason = "PROCESS_INTERRUPTED",
            canResume = true
        )

        val download = bus.state.value as TransferServiceState.Resumable
        assertFalse(download.canResume)

        bus.resumable(
            OPERATION_ID,
            TransferKind.Upload,
            transferred = 4,
            total = 8,
            reason = "PROCESS_INTERRUPTED",
            canResume = true
        )

        val upload = bus.state.value as TransferServiceState.Resumable
        assertTrue(upload.canResume)
    }

    companion object {
        private const val OPERATION_ID = "11111111-1111-1111-1111-111111111111"
        private const val OTHER_OPERATION_ID = "22222222-2222-2222-2222-222222222222"
    }
}
