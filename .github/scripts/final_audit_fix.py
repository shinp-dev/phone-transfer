from pathlib import Path


def replace_once_or_done(path_str: str, old: str, new: str) -> None:
    path = Path(path_str)
    text = path.read_text()
    if new in text:
        return
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"expected one match in {path_str}, found {count}")
    path.write_text(text.replace(old, new, 1))


home = "apps/android/app/src/main/kotlin/com/shinpstudio/phonetransfer/ui/HomeViewModel.kt"
replace_once_or_done(
    home,
    """        runOperation {
            mutableState.update { it.copy(pcs = repository.saved()) }
            restoreInterruptedTransfer()
        }""",
    """        runOperation {
            restoreInterruptedTransfer()
            mutableState.update { it.copy(pcs = repository.saved()) }
        }""",
)
replace_once_or_done(
    home,
    "        TransferStatusBus.resumable(\n            pending.operationId,",
    "        TransferStatusBus.restoreResumable(\n            pending.operationId,",
)

bus = "apps/android/app/src/main/kotlin/com/shinpstudio/phonetransfer/transfer/TransferStatusBus.kt"
replace_once_or_done(
    bus,
    """    fun begin(operationId: String, kind: TransferKind) {
        if (mutableState.value is TransferServiceState.RecoveryBlocked) return
        mutableState.value = TransferServiceState.Running(operationId, kind, 0, 0)
    }""",
    """    fun begin(operationId: String, kind: TransferKind) {
        val current = mutableState.value
        if (current is TransferServiceState.RecoveryBlocked) return
        if (
            current is TransferServiceState.Running && current.operationId != operationId ||
            current is TransferServiceState.Resumable && current.operationId != operationId
        ) {
            return
        }
        mutableState.value = TransferServiceState.Running(operationId, kind, 0, 0)
    }""",
)
replace_once_or_done(
    bus,
    """    fun resumable(
        operationId: String,
        kind: TransferKind,
        transferred: Long,
        total: Long,
        reason: String,
        canResume: Boolean = true
    ) {""",
    """    fun restoreResumable(
        operationId: String,
        kind: TransferKind,
        transferred: Long,
        total: Long,
        reason: String,
        canResume: Boolean = true
    ) {
        if (mutableState.value is TransferServiceState.Running) return
        resumable(operationId, kind, transferred, total, reason, canResume)
    }

    fun resumable(
        operationId: String,
        kind: TransferKind,
        transferred: Long,
        total: Long,
        reason: String,
        canResume: Boolean = true
    ) {""",
)

service = Path(
    "apps/android/app/src/main/kotlin/com/shinpstudio/phonetransfer/transfer/FileTransferService.kt"
)
service_text = service.read_text()
if "val serverTransferImpossible =" not in service_text:
    start_marker = "    private suspend fun finishCancellation(operation: PersistedTransferOperation): Boolean {\n"
    end_marker = "    private fun completeDuringCancellation(\n"
    start = service_text.index(start_marker)
    end = service_text.index(end_marker, start)
    replacement = """    private suspend fun finishCancellation(operation: PersistedTransferOperation): Boolean {
        if (operation.kind == DurableTransferKind.Upload) {
            val serverTransferImpossible =
                operation.serverTransferId == null &&
                    operation.sourceName == null &&
                    operation.totalSize == null &&
                    operation.sha256 == null
            if (serverTransferImpossible) return removeOperation(operation)

            val pc =
                try {
                    SavedPcStore.get(this).read().firstOrNull { it.deviceId == operation.deviceId }
                } catch (_: Exception) {
                    null
                } ?: return false
            val repository = DurableUploadRepository(this)
            var transferId = operation.serverTransferId
            if (
                transferId == null &&
                operation.sourceName != null &&
                operation.totalSize != null &&
                operation.sha256 != null
            ) {
                try {
                    val created = repository.createOrRecover(
                        pc,
                        operation.shareId,
                        operation.remotePath,
                        DurableUploadSource(
                            operation.sourceName,
                            operation.totalSize,
                            operation.sha256
                        ),
                        checkNotNull(operation.idempotencyKey)
                    )
                    transferId = created.transferId
                    if (created.status == "completed") {
                        return completeDuringCancellation(operation, created.fileName)
                    }
                } catch (_: FileTransferException) {
                    return false
                } catch (_: IOException) {
                    return false
                }
            }
            if (transferId != null) {
                try {
                    val before = repository.status(pc, transferId)
                    if (before.status == "completed") {
                        return completeDuringCancellation(operation, before.fileName)
                    }
                    if (before.status == "cancelled" || before.status == "failed") {
                        return removeOperation(operation)
                    }
                    repository.cancel(pc, transferId)
                } catch (error: FileTransferException) {
                    val after = try {
                        repository.status(pc, transferId)
                    } catch (_: Exception) {
                        null
                    }
                    if (after?.status == "completed") {
                        return completeDuringCancellation(operation, after.fileName)
                    }
                    if (after?.status == "cancelled" || after?.status == "failed") {
                        return removeOperation(operation)
                    }
                    if (error.retryable) return false
                    return false
                } catch (_: IOException) {
                    return false
                }
            }
        }
        return removeOperation(operation)
    }

"""
    service.write_text(service_text[:start] + replacement + service_text[end:])

store = "apps/android/app/src/main/kotlin/com/shinpstudio/phonetransfer/data/TransferOperationStore.kt"
replace_once_or_done(
    store,
    '        check(operation.committedOffset in 0..size) { "INVALID_LOCAL_OFFSET" }\n    }',
    """        check(operation.committedOffset in 0..size) { "INVALID_LOCAL_OFFSET" }
        check(operation.committedOffset == 0L || operation.serverTransferId != null) {
            "UPLOAD_TRANSFER_ID_REQUIRED"
        }
    }""",
)

persistence_test = "apps/android/app/src/test/kotlin/com/shinpstudio/phonetransfer/data/TransferOperationPersistenceTest.kt"
replace_once_or_done(
    persistence_test,
    """        assertThrows(IllegalStateException::class.java) {
            TransferOperationPersistence.encode(listOf(impossible))
        }
    }

    @Test
    fun nonContentUriAndMissingPersistedGrantRemainExplicit()""",
    """        assertThrows(IllegalStateException::class.java) {
            TransferOperationPersistence.encode(listOf(impossible))
        }

        val offsetWithoutServerIdentity = upload()
            .withSource("resume.bin", 12, HASH, 2)
            .copy(committedOffset = 4)
        assertThrows(IllegalStateException::class.java) {
            TransferOperationPersistence.encode(listOf(offsetWithoutServerIdentity))
        }
    }

    @Test
    fun nonContentUriAndMissingPersistedGrantRemainExplicit()""",
)

bus_test = Path(
    "apps/android/app/src/test/kotlin/com/shinpstudio/phonetransfer/transfer/TransferStatusBusTest.kt"
)
bus_test_text = bus_test.read_text()
if "restartRestoreDoesNotDowngradeALiveRunningTransfer" not in bus_test_text:
    bus_test.write_text(
        """package com.shinpstudio.phonetransfer.transfer

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
"""
    )
