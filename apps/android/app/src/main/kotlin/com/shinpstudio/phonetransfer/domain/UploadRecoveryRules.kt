package com.shinpstudio.phonetransfer.domain

data class LocalUploadCheckpoint(
    val fileName: String,
    val totalSize: Long,
    val sha256: String,
    val committedOffset: Long,
    val cancelRequested: Boolean
)

data class ServerUploadStatus(
    val fileName: String,
    val totalSize: Long,
    val sha256: String,
    val committedOffset: Long,
    val state: String
)

sealed interface UploadRecoveryDecision {
    data object Completed : UploadRecoveryDecision
    data class Resume(val offset: Long) : UploadRecoveryDecision
    data class Terminal(val code: String) : UploadRecoveryDecision
}

object UploadRecoveryRules {
    private val activeStates = setOf("created", "transferring", "paused", "verifying")
    private val terminalStates = setOf("failed", "cancelled")

    fun reconcile(
        local: LocalUploadCheckpoint,
        server: ServerUploadStatus
    ): UploadRecoveryDecision {
        if (local.cancelRequested) return UploadRecoveryDecision.Terminal("CANCEL_REQUESTED")
        if (!validLocal(local) || !validServer(server)) {
            return UploadRecoveryDecision.Terminal("SERVER_TRANSFER_MISMATCH")
        }
        if (
            local.fileName != server.fileName ||
            local.totalSize != server.totalSize ||
            local.sha256 != server.sha256
        ) {
            return UploadRecoveryDecision.Terminal("SERVER_TRANSFER_MISMATCH")
        }

        if (server.state == "completed") {
            return if (server.committedOffset == server.totalSize) {
                UploadRecoveryDecision.Completed
            } else {
                UploadRecoveryDecision.Terminal("SERVER_TRANSFER_MISMATCH")
            }
        }
        if (server.state in terminalStates) {
            return UploadRecoveryDecision.Terminal("SERVER_TRANSFER_${server.state.uppercase()}")
        }
        if (server.state !in activeStates) {
            return UploadRecoveryDecision.Terminal("SERVER_TRANSFER_MISMATCH")
        }
        if (server.committedOffset < local.committedOffset) {
            return UploadRecoveryDecision.Terminal("SERVER_OFFSET_BEHIND_LOCAL")
        }
        if (server.state == "verifying" && server.committedOffset != server.totalSize) {
            return UploadRecoveryDecision.Terminal("SERVER_TRANSFER_MISMATCH")
        }
        return UploadRecoveryDecision.Resume(server.committedOffset)
    }

    fun sourceMatches(
        expected: LocalUploadCheckpoint,
        actualFileName: String,
        actualSize: Long,
        actualSha256: String
    ): Boolean = expected.fileName == actualFileName &&
        expected.totalSize == actualSize &&
        expected.sha256 == actualSha256

    private fun validLocal(local: LocalUploadCheckpoint): Boolean = local.totalSize >= 0 &&
        local.committedOffset in 0..local.totalSize &&
        local.sha256.matches(Regex("[a-f0-9]{64}"))

    private fun validServer(server: ServerUploadStatus): Boolean = server.totalSize >= 0 &&
        server.committedOffset in 0..server.totalSize &&
        server.sha256.matches(Regex("[a-f0-9]{64}"))
}
