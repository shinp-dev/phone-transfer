package com.shinpstudio.phonetransfer.domain

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UploadRecoveryRulesTest {
    @Test
    fun serverAheadOfLocalCheckpointIsAdoptedAfterResponseLoss() {
        val decision = UploadRecoveryRules.reconcile(
            local(offset = 4),
            server(offset = 8, state = "paused")
        )

        assertEquals(UploadRecoveryDecision.Resume(8), decision)
    }

    @Test
    fun serverBehindLocalCheckpointFailsClosed() {
        val decision = UploadRecoveryRules.reconcile(
            local(offset = 8),
            server(offset = 4, state = "paused")
        )

        assertEquals(
            UploadRecoveryDecision.Terminal("SERVER_OFFSET_BEHIND_LOCAL"),
            decision
        )
    }

    @Test
    fun completedServerAfterProcessKillConvergesWithoutMorePatch() {
        val decision = UploadRecoveryRules.reconcile(
            local(offset = 8),
            server(offset = 12, state = "completed")
        )

        assertEquals(UploadRecoveryDecision.Completed, decision)
    }

    @Test
    fun cancelledAndFailedServerStatesNeverResume() {
        assertEquals(
            UploadRecoveryDecision.Terminal("SERVER_TRANSFER_CANCELLED"),
            UploadRecoveryRules.reconcile(local(4), server(4, "cancelled"))
        )
        assertEquals(
            UploadRecoveryDecision.Terminal("SERVER_TRANSFER_FAILED"),
            UploadRecoveryRules.reconcile(local(4), server(4, "failed"))
        )
    }

    @Test
    fun durableCancelIntentWinsOverOtherwiseResumableServerState() {
        val decision = UploadRecoveryRules.reconcile(
            local(offset = 4).copy(cancelRequested = true),
            server(offset = 8, state = "paused")
        )

        assertEquals(UploadRecoveryDecision.Terminal("CANCEL_REQUESTED"), decision)
    }

    @Test
    fun metadataMismatchNeverUsesServerOffset() {
        val decision = UploadRecoveryRules.reconcile(
            local(offset = 4),
            server(offset = 8, state = "paused").copy(fileName = "other.bin")
        )

        assertEquals(
            UploadRecoveryDecision.Terminal("SERVER_TRANSFER_MISMATCH"),
            decision
        )
    }

    @Test
    fun verifyingRequiresAllBytesButCanRetryComplete() {
        assertEquals(
            UploadRecoveryDecision.Resume(12),
            UploadRecoveryRules.reconcile(local(12), server(12, "verifying"))
        )
        assertEquals(
            UploadRecoveryDecision.Terminal("SERVER_TRANSFER_MISMATCH"),
            UploadRecoveryRules.reconcile(local(4), server(4, "verifying"))
        )
    }

    @Test
    fun repeatedRestartDecisionIsStable() {
        val first = UploadRecoveryRules.reconcile(local(4), server(8, "paused"))
        assertEquals(UploadRecoveryDecision.Resume(8), first)
        val second = UploadRecoveryRules.reconcile(local(8), server(8, "paused"))
        assertEquals(UploadRecoveryDecision.Resume(8), second)
    }

    @Test
    fun sourceNameSizeAndHashAllRemainPartOfResumeIdentity() {
        val expected = local(4)
        assertTrue(UploadRecoveryRules.sourceMatches(expected, "resume.bin", 12, HASH))
        assertFalse(UploadRecoveryRules.sourceMatches(expected, "changed.bin", 12, HASH))
        assertFalse(UploadRecoveryRules.sourceMatches(expected, "resume.bin", 13, HASH))
        assertFalse(
            UploadRecoveryRules.sourceMatches(
                expected,
                "resume.bin",
                12,
                "1123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            )
        )
    }

    private fun local(offset: Long) = LocalUploadCheckpoint(
        fileName = "resume.bin",
        totalSize = 12,
        sha256 = HASH,
        committedOffset = offset,
        cancelRequested = false
    )

    private fun server(offset: Long, state: String) = ServerUploadStatus(
        fileName = "resume.bin",
        totalSize = 12,
        sha256 = HASH,
        committedOffset = offset,
        state = state
    )

    companion object {
        private const val HASH = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    }
}
