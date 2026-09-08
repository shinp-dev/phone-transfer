package com.shinpstudio.phonetransfer.data

import java.io.File
import java.io.IOException
import java.nio.channels.FileChannel
import java.nio.file.Files
import java.nio.file.StandardOpenOption
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

internal fun syncTestDirectory(directory: File) {
    FileChannel.open(directory.toPath(), StandardOpenOption.READ).use { it.force(true) }
}

internal fun journalUpload() = PersistedTransferOperation.upload(
    UUID.randomUUID().toString(),
    UUID.randomUUID().toString(),
    UUID.randomUUID().toString(),
    "incoming",
    "content://provider/source",
    UUID.randomUUID().toString(),
    1
).withSource("resume.bin", 12, "a".repeat(64), 2)

class DurableJournalFileTest {
    @get:Rule
    val temporary = TemporaryFolder()

    private fun store(base: File) = TransferOperationStore(
        DurableJournalFile(base, ::syncTestDirectory)
    )

    @Test
    fun androidNineBackupOnlyCrashRestoresPendingInsteadOfAdmittingNewTransfer() {
        val base = File(temporary.root, "journal")
        val pending = journalUpload().requestingCancel(3)
        store(base).insert(pending)
        Files.move(base.toPath(), File(base.path + ".bak").toPath())

        val restarted = store(base)
        assertEquals(pending, restarted.read().single())
        assertThrows(IllegalStateException::class.java) { restarted.insert(journalUpload()) }
        assertEquals(pending, store(base).read().single())
    }

    @Test
    fun legacyBackupWinsOverPartiallyWrittenBase() {
        val base = File(temporary.root, "journal")
        val pending = journalUpload()
        store(base).insert(pending)
        Files.move(base.toPath(), File(base.path + ".bak").toPath())
        base.writeText("{partial")
        assertEquals(pending, store(base).read().single())
    }

    @Test
    fun failedFileSyncNeverAcknowledgesCancelAndPreservesPreviousGeneration() {
        val base = File(temporary.root, "journal")
        val pending = journalUpload()
        store(base).insert(pending)
        val failing = TransferOperationStore(
            DurableJournalFile(base, ::syncTestDirectory, syncFile = { throw IOException("sync") })
        )
        assertThrows(IOException::class.java) { failing.requestCancel(pending.operationId, 3) }
        assertThrows(IllegalStateException::class.java) { failing.read() }
        assertEquals(pending, store(base).read().single())
    }

    @Test
    fun failedRenameNeverAcknowledgesCancel() {
        val base = File(temporary.root, "journal")
        val pending = journalUpload()
        store(base).insert(pending)
        val failing = TransferOperationStore(
            DurableJournalFile(base, ::syncTestDirectory, move = { _, _ ->
                throw IOException("move")
            })
        )
        assertThrows(IOException::class.java) { failing.requestCancel(pending.operationId, 3) }
        assertEquals(pending, store(base).read().single())
    }

    @Test
    fun directorySyncFailurePoisonsInstanceEvenIfRenameAlreadyCommitted() {
        val base = File(temporary.root, "journal")
        val pending = journalUpload()
        store(base).insert(pending)
        val failing = TransferOperationStore(
            DurableJournalFile(base, syncDirectory = { throw IOException("directory sync") })
        )
        assertThrows(IOException::class.java) { failing.requestCancel(pending.operationId, 3) }
        assertThrows(IllegalStateException::class.java) { failing.insert(journalUpload()) }
        assertTrue(store(base).read().single().cancelRequested)
    }

    @Test
    fun corruptOversizedAndNonFileJournalNeverBecomeEmpty() {
        for (content in listOf("{broken", "x".repeat(TransferOperationPersistence.MAX_BYTES + 1))) {
            val base = temporary.newFile()
            base.writeText(content)
            assertThrows(Exception::class.java) { store(base).insert(journalUpload()) }
            assertEquals(content, base.readText())
        }
        val directory = temporary.newFolder()
        assertThrows(IllegalStateException::class.java) { store(directory).read() }
    }

    @Test
    fun completionReceiptSurvivesKillBeforeNotificationAndOnlyNewIntentRetiresIt() {
        val base = File(temporary.root, "journal")
        val pending = journalUpload().withServer(UUID.randomUUID().toString(), 8, 3)
        val first = store(base)
        first.insert(pending)
        first.complete(pending.operationId, "resume.bin", 4)

        val restarted = store(base)
        val completed = restarted.read().single()
        assertEquals("resume.bin", completed.completedFileName)
        assertEquals(12L, completed.committedOffset)
        assertEquals(completed, restarted.requestCancel(pending.operationId, 5))
        assertThrows(IllegalStateException::class.java) { restarted.replace(pending) }
        assertThrows(IllegalStateException::class.java) { restarted.remove(pending.operationId) }

        val next = journalUpload()
        restarted.insert(next)
        assertEquals(next, store(base).read().single())
    }

    @Test
    fun snapshotReadBeforeCompletionCannotPublishAfterReceiptCommit() {
        val base = File(temporary.root, "journal")
        val store = store(base)
        val pending = journalUpload().withServer(UUID.randomUUID().toString(), 8, 3)
        store.insert(pending)
        val snapshot = store.read().single()
        store.complete(pending.operationId, "resume.bin", 4)
        var published = false
        store.ifCurrent(snapshot) { published = true }
        assertFalse(published)
        store.ifCurrent(store.read().single()) { published = true }
        assertTrue(published)
    }

    @Test
    fun versionOnePendingJournalMigratesWithoutChangingIdempotencyKey() {
        val base = temporary.newFile()
        val pending = journalUpload()
        base.writeText(
            TransferOperationPersistence.encode(
                listOf(pending)
            ).replace("\"version\":2", "\"version\":1")
        )
        val store = store(base)
        assertEquals(pending, store.read().single())
        store.requestCancel(pending.operationId, 3)
        assertEquals(pending.idempotencyKey, store(base).read().single().idempotencyKey)
        assertTrue(base.readText().contains("\"version\":2"))
    }
}
