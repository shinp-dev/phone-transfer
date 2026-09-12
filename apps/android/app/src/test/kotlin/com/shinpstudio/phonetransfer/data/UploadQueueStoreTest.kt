package com.shinpstudio.phonetransfer.data

import java.io.File
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class UploadQueueStoreTest {
    @get:Rule
    val temporary = TemporaryFolder()

    private fun store(base: File) = UploadQueueStore(
        DurableJournalFile(base, syncDirectory = {})
    )

    @Test
    fun queueSurvivesRestartAndAdvancesOneItemAtATime() {
        val base = File(temporary.root, "upload-queue")
        val queue = queue(3)
        store(base).create(queue)

        val first = store(base).activateNext(2)!!
        assertEquals(queue.items[0].operationId, first.active?.operationId)
        assertEquals(1, first.items.count { it.state == QueuedUploadState.Active })

        store(base).complete(first.active!!.operationId, "first.bin", 3)
        val second = store(base).activateNext(4)!!
        assertEquals(queue.items[1].operationId, second.active?.operationId)
        assertEquals(QueuedUploadState.Completed, second.items[0].state)
        assertEquals("first.bin", second.items[0].resultFileName)
    }

    @Test
    fun failureIsRetainedWhileTheNextFileCanStart() {
        val base = File(temporary.root, "upload-queue")
        val first = store(base).create(queue(2)).let { store(base).activateNext(2)!! }

        store(base).fail(first.active!!.operationId, "DESTINATION_EXISTS", 3)
        val next = store(base).activateNext(4)!!

        assertEquals(QueuedUploadState.Failed, next.items[0].state)
        assertEquals("DESTINATION_EXISTS", next.items[0].errorCode)
        assertEquals(QueuedUploadState.Active, next.items[1].state)
    }

    @Test
    fun cancellationDurablyStopsWaitingFiles() {
        val base = File(temporary.root, "upload-queue")
        store(base).create(queue(3))
        val active = store(base).activateNext(2)!!.active!!

        val cancelled = store(base).requestCancel(3)!!

        assertTrue(cancelled.cancelRequested)
        assertEquals(2, cancelled.items.count { it.state == QueuedUploadState.Cancelled })
        assertEquals(active.operationId, cancelled.active?.operationId)
        assertFalse(store(base).activateNext(4)!!.hasQueued)

        val finished = store(base).cancelActive(active.operationId, 5)!!
        assertTrue(finished.isFinished)
        assertNull(finished.active)
    }

    @Test
    fun duplicateSourcesInvalidDocumentsAndOversizedBatchesFailClosed() {
        val duplicate = queue(2).let { value ->
            value.copy(
                items = listOf(value.items[0], value.items[1].copy(uri = value.items[0].uri))
            )
        }
        assertThrows(IllegalStateException::class.java) {
            UploadQueuePersistence.encode(duplicate)
        }
        assertThrows(IllegalStateException::class.java) {
            UploadQueuePersistence.decode(
                UploadQueuePersistence.encode(queue(1))
                    .toString(Charsets.UTF_8)
                    .replace("\"version\":1", "\"version\":2")
                    .toByteArray()
            )
        }
        assertThrows(IllegalStateException::class.java) {
            queue(UploadQueuePersistence.MAX_ITEMS + 1)
        }
    }

    @Test
    fun unfinishedQueueCannotBeSilentlyReplacedButCompletedHistoryCan() {
        val base = File(temporary.root, "upload-queue")
        val first = queue(1)
        store(base).create(first)
        assertThrows(IllegalStateException::class.java) { store(base).create(queue(1)) }

        val active = store(base).activateNext(2)!!.active!!
        store(base).complete(active.operationId, "done.bin", 3)
        val replacement = queue(1)
        store(base).create(replacement)
        assertEquals(replacement, store(base).read())
    }

    private fun queue(count: Int): PersistedUploadQueue = PersistedUploadQueue.create(
        UUID.randomUUID().toString(),
        UUID.randomUUID().toString(),
        "incoming",
        (1..count).map { index ->
            "content://provider/document/$index" to "file-$index.bin"
        },
        1
    )
}
