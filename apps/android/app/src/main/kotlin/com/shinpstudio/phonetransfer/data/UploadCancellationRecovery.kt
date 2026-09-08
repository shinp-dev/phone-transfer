package com.shinpstudio.phonetransfer.data

import com.shinpstudio.phonetransfer.protocol.Transfer
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout

internal interface UploadCancellationRemote {
    suspend fun createOrRecover(
        pc: SavedPc,
        shareId: String,
        directory: String,
        source: DurableUploadSource,
        idempotencyKey: String
    ): Transfer

    suspend fun status(pc: SavedPc, transferId: String): Transfer
    suspend fun cancel(pc: SavedPc, transferId: String): Transfer
}

internal sealed interface CancellationResult {
    data object Pending : CancellationResult
    data object Cancelled : CancellationResult
    data class Completed(val fileName: String) : CancellationResult
}

/** Production cancellation ordering, also exercised with a real disk journal in JVM tests. */
internal class UploadCancellationRecovery(
    private val store: TransferOperationStore,
    private val findPc: suspend (String) -> SavedPc?,
    private val verifyPc: suspend (SavedPc) -> Unit,
    private val remote: UploadCancellationRemote,
    private val budgetMillis: Long = 15_000
) {
    suspend fun cancel(operationId: String): CancellationResult {
        // Never perform a server side effect unless the terminal intent is committed first.
        val operation = try {
            store.requestCancel(operationId, System.currentTimeMillis())
                ?: return CancellationResult.Cancelled
        } catch (_: Exception) {
            return CancellationResult.Pending
        }
        operation.completedFileName?.let { return CancellationResult.Completed(it) }
        return withContext(NonCancellable) {
            try {
                withTimeout(budgetMillis) { reconcile(operation) }
            } catch (_: TimeoutCancellationException) {
                CancellationResult.Pending
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                // Missing PC, transport/identity failure and malformed replies prove no terminal state.
                CancellationResult.Pending
            }
        }
    }

    private suspend fun reconcile(initial: PersistedTransferOperation): CancellationResult {
        if (initial.kind == DurableTransferKind.Download || initial.sourceName == null) {
            // No source metadata means no upload create could have been issued.
            store.remove(initial.operationId)
            return CancellationResult.Cancelled
        }
        val pc = findPc(initial.deviceId) ?: return CancellationResult.Pending
        verifyPc(pc)
        var operation = initial
        val before = if (operation.serverTransferId == null) {
            val created = remote.createOrRecover(
                pc,
                operation.shareId,
                operation.remotePath,
                operation.uploadSource(),
                checkNotNull(operation.idempotencyKey)
            )
            validateUploadResponse(operation, created)
            operation = store.replace(
                operation.withServer(
                    created.transferId,
                    created.transferredBytes,
                    System.currentTimeMillis()
                )
            )
            created
        } else {
            remote.status(pc, checkNotNull(operation.serverTransferId))
        }
        terminal(operation, before)?.let { return it }
        val transferId = checkNotNull(operation.serverTransferId)
        val after = try {
            remote.cancel(pc, transferId)
        } catch (error: FileTransferException) {
            remote.status(pc, transferId)
        } catch (error: IOException) {
            remote.status(pc, transferId)
        }
        return terminal(operation, after) ?: CancellationResult.Pending
    }

    private fun terminal(
        operation: PersistedTransferOperation,
        server: Transfer
    ): CancellationResult? {
        validateUploadResponse(operation, server)
        return when (server.status) {
            "completed" -> {
                store.complete(operation.operationId, server.fileName, System.currentTimeMillis())
                CancellationResult.Completed(server.fileName)
            }
            "cancelled", "failed" -> {
                store.remove(operation.operationId)
                CancellationResult.Cancelled
            }
            else -> null
        }
    }
}

internal fun PersistedTransferOperation.uploadSource() = DurableUploadSource(
    checkNotNull(sourceName),
    checkNotNull(totalSize),
    checkNotNull(sha256)
)

internal fun validateUploadResponse(operation: PersistedTransferOperation, transfer: Transfer) {
    validateUploadTransfer(transfer, operation.uploadSource(), operation.serverTransferId)
    if (transfer.transferredBytes < operation.committedOffset) {
        throw FileTransferException(
            "SERVER_OFFSET_BEHIND_LOCAL",
            false,
            "The server offset is behind the durable checkpoint."
        )
    }
}

internal suspend fun verifyRecoveryPc(connect: suspend () -> Unit) {
    try {
        connect()
    } catch (error: CancellationException) {
        throw error
    } catch (error: IOException) {
        throw FileTransferException("PC_UNAVAILABLE", true, "The saved PC is unavailable.", error)
    } catch (error: Exception) {
        throw FileTransferException(
            "PC_IDENTITY_MISMATCH",
            false,
            "The saved PC identity could not be verified.",
            error
        )
    }
}
