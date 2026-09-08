package com.shinpstudio.phonetransfer.data

import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.nio.file.LinkOption
import java.nio.file.NoSuchFileException
import java.nio.file.StandardCopyOption
import java.nio.file.attribute.BasicFileAttributes

/** Checked commit boundary; callers serialize access. A failed commit poisons this instance. */
internal class DurableJournalFile(
    private val base: File,
    private val syncDirectory: (File) -> Unit,
    private val syncFile: (FileOutputStream) -> Unit = { it.fd.sync() },
    private val move: (File, File) -> Unit = { source, target ->
        Files.move(source.toPath(), target.toPath(), StandardCopyOption.ATOMIC_MOVE)
        Unit
    }
) {
    private val backup = File(base.path + ".bak")
    private val pending = File(base.path + ".new")
    private var poisoned = false

    fun read(): ByteArray? {
        check(!poisoned) { "TRANSFER_JOURNAL_UNAVAILABLE" }
        // Android 9 can die after base -> .bak but before opening the next base file.
        // Recover that committed generation before deciding whether the journal exists.
        if (exists(backup)) {
            try {
                move(backup, base)
                syncDirectory(checkNotNull(base.parentFile))
            } catch (error: Exception) {
                poisoned = true
                throw error
            }
        }
        if (!exists(base)) return null
        return Files.newInputStream(base.toPath()).use { input ->
            val buffer = ByteArray(TransferOperationPersistence.MAX_BYTES + 1)
            var size = 0
            while (size < buffer.size) {
                val count = input.read(buffer, size, buffer.size - size)
                if (count < 0) break
                if (count == 0) continue
                size += count
            }
            check(size <= TransferOperationPersistence.MAX_BYTES) { "TRANSFER_JOURNAL_TOO_LARGE" }
            buffer.copyOf(size)
        }
    }

    fun write(bytes: ByteArray) {
        check(!poisoned) { "TRANSFER_JOURNAL_UNAVAILABLE" }
        try {
            FileOutputStream(pending).use { output ->
                output.write(bytes)
                syncFile(output)
            }
            move(pending, base)
            syncDirectory(checkNotNull(base.parentFile))
        } catch (error: Exception) {
            // A rename/directory-sync failure may be ambiguous. No side effects may follow.
            poisoned = true
            throw error
        }
    }

    private fun exists(file: File): Boolean = try {
        check(
            Files.readAttributes(
                file.toPath(),
                BasicFileAttributes::class.java,
                LinkOption.NOFOLLOW_LINKS
            ).isRegularFile
        ) {
            "INVALID_TRANSFER_JOURNAL_FILE"
        }
        true
    } catch (_: NoSuchFileException) {
        false
    }
}
