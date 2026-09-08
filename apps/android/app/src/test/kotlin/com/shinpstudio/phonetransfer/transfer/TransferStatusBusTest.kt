package com.shinpstudio.phonetransfer.transfer

import java.util.UUID
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TransferStatusBusTest {
    @Test
    fun interruptedDownloadNeverAdvertisesUnsafeResume() {
        val operationId = UUID.randomUUID().toString()

        TransferStatusBus.resumable(
            operationId,
            TransferKind.Download,
            transferred = 0,
            total = 0,
            reason = "PROCESS_INTERRUPTED",
            canResume = true
        )

        val download = TransferStatusBus.state.value as TransferServiceState.Resumable
        assertFalse(download.canResume)

        TransferStatusBus.resumable(
            operationId,
            TransferKind.Upload,
            transferred = 4,
            total = 8,
            reason = "PROCESS_INTERRUPTED",
            canResume = true
        )

        val upload = TransferStatusBus.state.value as TransferServiceState.Resumable
        assertTrue(upload.canResume)
    }
}
