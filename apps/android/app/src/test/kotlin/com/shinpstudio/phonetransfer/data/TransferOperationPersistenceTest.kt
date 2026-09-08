package com.shinpstudio.phonetransfer.data

import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class TransferOperationPersistenceTest {
    @Test
    fun operationBeforeSourceHashSurvivesRestartWithStableIdempotencyKey() {
        val operation = upload()

        val restored = restart(operation)

        assertEquals(operation.operationId, restored.operationId)
        assertEquals(operation.idempotencyKey, restored.idempotencyKey)
        assertNull(restored.sourceName)
        assertNull(restored.serverTransferId)
        assertEquals(0L, restored.committedOffset)
    }

    @Test
    fun serverCreateResponseLossRetainsMetadataAndIdempotencyWithoutInventingTransferId() {
        val operation = upload().withSource("resume.bin", 12, HASH, 2)

        val restored = restart(operation)

        assertEquals(operation.idempotencyKey, restored.idempotencyKey)
        assertEquals("resume.bin", restored.sourceName)
        assertEquals(12L, restored.totalSize)
        assertEquals(HASH, restored.sha256)
        assertNull(restored.serverTransferId)
    }

    @Test
    fun acknowledgedServerCheckpointSurvivesRepeatedRestart() {
        val operation = upload()
            .withSource("resume.bin", 12, HASH, 2)
            .withServer(UUID.randomUUID().toString(), 8, 3)

        val first = restart(operation)
        val second = restart(first)

        assertEquals(operation.serverTransferId, second.serverTransferId)
        assertEquals(8L, second.committedOffset)
        assertEquals(operation.idempotencyKey, second.idempotencyKey)
    }

    @Test
    fun durableCancelRequestNeverDisappearsAcrossRestart() {
        val operation = upload()
            .withSource("resume.bin", 12, HASH, 2)
            .requestingCancel(3)

        val restored = restart(operation)

        assertTrue(restored.cancelRequested)
        assertEquals(operation.idempotencyKey, restored.idempotencyKey)
    }

    @Test
    fun downloadRecoveryPersistsOnlyCapabilityAndRestartsFromZero() {
        val operation = PersistedTransferOperation.download(
            UUID.randomUUID().toString(),
            UUID.randomUUID().toString(),
            UUID.randomUUID().toString(),
            "folder/file.bin",
            "content://provider/document/download",
            1
        ).withGrant(true, 2)

        val restored = restart(operation)

        assertEquals(DurableTransferKind.Download, restored.kind)
        assertTrue(restored.persistedGrant)
        assertEquals(0L, restored.committedOffset)
        assertNull(restored.serverTransferId)
        assertNull(restored.idempotencyKey)
    }

    @Test
    fun unknownSchemaAndDuplicateOperationIdsFailClosed() {
        val operation = upload()
        val unknown = "{\"version\":2,\"operations\":[]}".toByteArray()
        assertThrows(IllegalStateException::class.java) {
            TransferOperationPersistence.decode(unknown)
        }
        assertThrows(IllegalStateException::class.java) {
            TransferOperationPersistence.encode(listOf(operation, operation))
        }
    }

    @Test
    fun incompleteUploadMetadataAndLocalOffsetAheadOfSizeFailClosed() {
        val partial = upload().copy(sourceName = "resume.bin")
        assertThrows(IllegalStateException::class.java) {
            TransferOperationPersistence.encode(listOf(partial))
        }

        val impossible = upload()
            .withSource("resume.bin", 12, HASH, 2)
            .copy(committedOffset = 13)
        assertThrows(IllegalStateException::class.java) {
            TransferOperationPersistence.encode(listOf(impossible))
        }
    }

    @Test
    fun nonContentUriAndMissingPersistedGrantRemainExplicit() {
        val noGrant = upload()
        assertFalse(noGrant.persistedGrant)
        assertThrows(IllegalStateException::class.java) {
            TransferOperationPersistence.encode(
                listOf(noGrant.copy(uri = "file:///sdcard/resume.bin"))
            )
        }
    }

    private fun restart(operation: PersistedTransferOperation): PersistedTransferOperation {
        val encoded = TransferOperationPersistence.encode(listOf(operation)).toByteArray()
        return TransferOperationPersistence.decode(encoded).single()
    }

    private fun upload(): PersistedTransferOperation =
        PersistedTransferOperation.upload(
            UUID.randomUUID().toString(),
            UUID.randomUUID().toString(),
            UUID.randomUUID().toString(),
            "incoming",
            "content://provider/document/source",
            UUID.randomUUID().toString(),
            1
        )

    companion object {
        private const val HASH = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    }
}
