from pathlib import Path


def replace_once(path_str: str, old: str, new: str) -> None:
    path = Path(path_str)
    text = path.read_text()
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"expected one match in {path_str}, found {count}")
    path.write_text(text.replace(old, new, 1))


home = "apps/android/app/src/main/kotlin/com/shinpstudio/phonetransfer/ui/HomeViewModel.kt"
replace_once(
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
replace_once(
    home,
    "        TransferStatusBus.resumable(\n            pending.operationId,",
    "        TransferStatusBus.restoreResumable(\n            pending.operationId,",
)

bus = "apps/android/app/src/main/kotlin/com/shinpstudio/phonetransfer/transfer/TransferStatusBus.kt"
replace_once(
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
replace_once(
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

service = "apps/android/app/src/main/kotlin/com/shinpstudio/phonetransfer/transfer/FileTransferService.kt"
replace_once(
    service,
    """    private suspend fun finishCancellation(operation: PersistedTransferOperation): Boolean {
        if (operation.kind == DurableTransferKind.Upload) {
            val pc = SavedPcStore.get(this).read().firstOrNull { it.deviceId == operation.deviceId }
            if (pc != null) {
                val repository = DurableUploadRepository(this)
                var transferId = operation.serverTransferId""",
    """    private suspend fun finishCancellation(operation: PersistedTransferOperation): Boolean {
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
            var transferId = operation.serverTransferId""",
)
replace_once(
    service,
    """                } catch (error: FileTransferException) {
                    if (error.retryable) return false
                    return removeOperation(operation)
                } catch (_: IOException) {
                    return false
                }""",
    """                } catch (_: FileTransferException) {
                    return false
                } catch (_: IOException) {
                    return false
                }""",
)
replace_once(
    service,
    """            }
        }
        return removeOperation(operation)
    }

    private fun completeDuringCancellation(""",
    """        }
        return removeOperation(operation)
    }

    private fun completeDuringCancellation(""",
)

store = "apps/android/app/src/main/kotlin/com/shinpstudio/phonetransfer/data/TransferOperationStore.kt"
replace_once(
    store,
    '        check(operation.committedOffset in 0..size) { "INVALID_LOCAL_OFFSET" }\n    }',
    """        check(operation.committedOffset in 0..size) { "INVALID_LOCAL_OFFSET" }
        check(operation.committedOffset == 0L || operation.serverTransferId != null) {
            "UPLOAD_TRANSFER_ID_REQUIRED"
        }
    }""",
)

persistence_test = "apps/android/app/src/test/kotlin/com/shinpstudio/phonetransfer/data/TransferOperationPersistenceTest.kt"
replace_once(
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

bus_test = "apps/android/app/src/test/kotlin/com/shinpstudio/phonetransfer/transfer/TransferStatusBusTest.kt"
replace_once(
    bus_test,
    """class TransferStatusBusTest {
    @Test
    fun interruptedDownloadNeverAdvertisesUnsafeResume() {""",
    """class TransferStatusBusTest {
    @Test
    fun restartRestoreDoesNotDowngradeALiveRunningTransfer() {
        val operationId = UUID.randomUUID().toString()
        TransferStatusBus.begin(operationId, TransferKind.Upload)

        TransferStatusBus.restoreResumable(
            operationId,
            TransferKind.Upload,
            transferred = 4,
            total = 8,
            reason = "PROCESS_INTERRUPTED"
        )

        assertTrue(TransferStatusBus.state.value is TransferServiceState.Running)
    }

    @Test
    fun aDifferentPendingOperationCannotBeHiddenByBegin() {
        val pending = UUID.randomUUID().toString()
        TransferStatusBus.resumable(
            pending,
            TransferKind.Upload,
            transferred = 4,
            total = 8,
            reason = "PROCESS_INTERRUPTED"
        )

        TransferStatusBus.begin(UUID.randomUUID().toString(), TransferKind.Upload)

        val current = TransferStatusBus.state.value as TransferServiceState.Resumable
        assertTrue(current.operationId == pending)
    }

    @Test
    fun interruptedDownloadNeverAdvertisesUnsafeResume() {""",
)
