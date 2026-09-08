package com.shinpstudio.phonetransfer.data

import com.shinpstudio.phonetransfer.protocol.Transfer
import java.io.File
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class UploadCancellationRecoveryTest {
    @get:Rule
    val temporary = TemporaryFolder()

    private val initial = journalUpload().withServer(UUID.randomUUID().toString(), 4, 3)
    private val pc = SavedPc(initial.deviceId, "PC", "https://192.168.1.2:58443", "b".repeat(64))
    private val base get() = File(temporary.root, "journal")
    private fun store() = TransferOperationStore(DurableJournalFile(base, ::syncTestDirectory))
    private fun response(state: String = "transferring", offset: Long = 4) = Transfer(
        checkNotNull(initial.serverTransferId),
        "resume.bin",
        12,
        offset,
        "a".repeat(64),
        state,
        "2026-09-08T00:00:00Z"
    )

    private inner class Remote : UploadCancellationRemote {
        var current = response()
        val calls = mutableListOf<String>()
        var createError: Exception? = null
        var delete: suspend () -> Transfer = { current.copy(status = "cancelled") }
        var get: suspend () -> Transfer = { current }

        override suspend fun createOrRecover(
            pc: SavedPc,
            shareId: String,
            directory: String,
            source: DurableUploadSource,
            idempotencyKey: String
        ): Transfer {
            assertTrue(store().read().single().cancelRequested)
            assertEquals(initial.idempotencyKey, idempotencyKey)
            assertEquals(initial.shareId, shareId)
            assertEquals(initial.remotePath, directory)
            assertEquals(initial.uploadSource(), source)
            calls += "create"
            createError?.let { throw it }
            return current
        }

        override suspend fun status(pc: SavedPc, transferId: String): Transfer {
            assertTrue(store().read().single().cancelRequested)
            assertEquals(initial.serverTransferId, transferId)
            calls += "get"
            return get()
        }

        override suspend fun cancel(pc: SavedPc, transferId: String): Transfer {
            assertTrue(store().read().single().cancelRequested)
            assertEquals(initial.serverTransferId, transferId)
            calls += "delete"
            return delete()
        }
    }

    private fun recovery(remote: Remote, saved: SavedPc? = pc) = UploadCancellationRecovery(
        store(),
        { saved },
        { assertEquals(pc, it) },
        remote,
        budgetMillis = 100
    )

    @Test
    fun missingPcKeepsCancelIntentAndServerIdentityAcrossRestart() = runTest {
        store().insert(initial)
        val remote = Remote()
        assertEquals(CancellationResult.Pending, recovery(remote, null).cancel(initial.operationId))
        assertTrue(store().read().single().cancelRequested)
        assertEquals(initial.serverTransferId, store().read().single().serverTransferId)
        assertTrue(remote.calls.isEmpty())
    }

    @Test
    fun lostCreateResponseThenNonRetryableFailureNeverDeletesIntent() = runTest {
        store().insert(initial.copy(serverTransferId = null, committedOffset = 0))
        val remote = Remote().apply {
            createError = FileTransferException("TRANSFER_SERVICE_UNAVAILABLE", false, "unavailable")
        }
        assertEquals(CancellationResult.Pending, recovery(remote).cancel(initial.operationId))
        assertTrue(store().read().single().cancelRequested)
        remote.createError = null
        assertEquals(CancellationResult.Cancelled, recovery(remote).cancel(initial.operationId))
        assertEquals(listOf("create", "create", "delete"), remote.calls)
        assertTrue(store().read().isEmpty())
    }

    @Test
    fun cancelledCoroutineCanStillReconcileAndCancelServer() = runTest {
        store().insert(initial)
        val remote = Remote()
        var result: CancellationResult? = null
        val job = launch(start = CoroutineStart.UNDISPATCHED) {
            try {
                awaitCancellation()
            } catch (_: CancellationException) {
                result = recovery(remote).cancel(initial.operationId)
            }
        }
        job.cancelAndJoin()
        assertEquals(CancellationResult.Cancelled, result)
        assertEquals(listOf("get", "delete"), remote.calls)
        assertTrue(store().read().isEmpty())
    }

    @Test
    fun hungCleanupStopsAtBudgetAndKeepsIntent() = runTest {
        store().insert(initial)
        val remote = Remote().apply { get = { awaitCancellation() } }
        assertEquals(CancellationResult.Pending, recovery(remote).cancel(initial.operationId))
        assertTrue(store().read().single().cancelRequested)
        assertEquals(listOf("get"), remote.calls)
    }

    @Test
    fun completionWinningDeleteRaceBecomesDurableReceipt() = runTest {
        store().insert(initial)
        val remote = Remote().apply {
            delete = {
                current = response("completed", 12)
                throw FileTransferException("TRANSFER_STATE_CONFLICT", false, "completed")
            }
        }
        assertEquals(CancellationResult.Completed("resume.bin"), recovery(remote).cancel(initial.operationId))
        assertEquals("resume.bin", store().read().single().completedFileName)
        assertEquals(listOf("get", "delete", "get"), remote.calls)
        assertEquals(CancellationResult.Completed("resume.bin"), recovery(remote).cancel(initial.operationId))
        assertEquals(3, remote.calls.size)
    }

    @Test
    fun deleteResponseLossConvergesFromFreshServerStatus() = runTest {
        store().insert(initial)
        val remote = Remote().apply {
            delete = {
                current = response("cancelled")
                throw IOException("lost delete response")
            }
        }
        assertEquals(CancellationResult.Cancelled, recovery(remote).cancel(initial.operationId))
        assertTrue(store().read().isEmpty())
    }

    @Test
    fun failedServerConvergesWithoutAnotherDelete() = runTest {
        store().insert(initial)
        val remote = Remote().apply { current = response("failed") }
        assertEquals(CancellationResult.Cancelled, recovery(remote).cancel(initial.operationId))
        assertEquals(listOf("get"), remote.calls)
    }

    @Test
    fun invalidCompletedResponsesKeepJournalAndNeverSendDelete() = runTest {
        val bad = listOf(
            response("completed", 12).copy(transferId = UUID.randomUUID().toString()),
            response("completed", 12).copy(fileName = "other.bin"),
            response("completed", 12).copy(sha256 = "c".repeat(64)),
            response("completed", 12).copy(totalSize = 13),
            response("completed", 4),
            response("verifying", 4),
            response("transferring", 13),
            response("transferring", 3),
            response("unknown", 4)
        )
        store().insert(initial)
        for (invalid in bad) {
            val remote = Remote().apply { current = invalid }
            assertEquals(CancellationResult.Pending, recovery(remote).cancel(initial.operationId))
            assertTrue(store().read().single().cancelRequested)
            assertEquals(null, store().read().single().completedFileName)
            assertEquals(listOf("get"), remote.calls)
        }
    }

    @Test
    fun activeOrMismatchedDeleteReplyDoesNotProveCancellation() = runTest {
        store().insert(initial)
        val remote = Remote().apply { delete = { response() } }
        assertEquals(CancellationResult.Pending, recovery(remote).cancel(initial.operationId))
        remote.delete = { response("cancelled").copy(transferId = UUID.randomUUID().toString()) }
        assertEquals(CancellationResult.Pending, recovery(remote).cancel(initial.operationId))
        assertTrue(store().read().single().cancelRequested)
    }

    @Test
    fun cancellationDuringPcVerificationIsNotAnIdentityFailureOrUserCancel() = runTest {
        store().insert(initial)
        var cancellationPropagated = false
        val job = launch(start = CoroutineStart.UNDISPATCHED) {
            try {
                verifyRecoveryPc { awaitCancellation() }
            } catch (_: CancellationException) {
                cancellationPropagated = true
            }
        }
        job.cancelAndJoin()
        assertTrue(cancellationPropagated)
        assertFalse(store().read().single().cancelRequested)
    }

    @Test
    fun failedCancelCommitPreventsEveryRemoteCall() = runTest {
        store().insert(initial)
        val failing = TransferOperationStore(
            DurableJournalFile(base, ::syncTestDirectory, syncFile = { throw IOException("sync") })
        )
        val remote = Remote()
        val recovery = UploadCancellationRecovery(failing, { pc }, {}, remote)
        assertEquals(CancellationResult.Pending, recovery.cancel(initial.operationId))
        assertTrue(remote.calls.isEmpty())
        assertFalse(store().read().single().cancelRequested)
    }
}
