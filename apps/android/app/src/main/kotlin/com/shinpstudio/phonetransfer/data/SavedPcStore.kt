package com.shinpstudio.phonetransfer.data

import android.content.Context
import android.util.AtomicFile
import com.shinpstudio.phonetransfer.security.PairingInvitation
import java.io.File
import java.util.UUID
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

@Serializable
data class SavedPc(
    val deviceId: String,
    val displayName: String,
    @SerialName("endpoint") val lastKnownEndpoint: String,
    val pin: String
)

internal object SavedPcRules {
    const val LIMIT = 100

    fun validate(pcs: List<SavedPc>) {
        check(pcs.size <= LIMIT && pcs.map { it.deviceId }.distinct().size == pcs.size)
        pcs.forEach(::validatePc)
    }

    fun add(pcs: List<SavedPc>, pc: SavedPc): List<SavedPc> {
        validate(pcs)
        validatePc(pc)
        check(pcs.none { it.deviceId == pc.deviceId }) { "PC_ALREADY_SAVED" }
        check(pcs.size < LIMIT) { "PC_LIMIT" }
        return pcs + pc
    }

    fun remove(pcs: List<SavedPc>, id: String): List<SavedPc> {
        validate(pcs)
        return pcs.filterNot { it.deviceId == id }
    }

    private fun validatePc(pc: SavedPc) {
        check(UUID.fromString(pc.deviceId).toString() == pc.deviceId)
        PairingInvitation.lanEndpoint(pc.lastKnownEndpoint)
        check(pc.pin.matches(Regex("[a-f0-9]{64}")))
    }
}

internal class SavedPcStore private constructor(context: Context) {
    private val json = Json { ignoreUnknownKeys = false }
    private val file = AtomicFile(File(context.filesDir, "paired-pcs.json"))

    @Synchronized
    fun read(): List<SavedPc> = readUnlocked()

    @Synchronized
    fun add(pc: SavedPc): List<SavedPc> {
        val next = SavedPcRules.add(readUnlocked(), pc)
        writeUnlocked(next)
        return next
    }

    @Synchronized
    fun remove(id: String): List<SavedPc> {
        val next = SavedPcRules.remove(readUnlocked(), id)
        writeUnlocked(next)
        return next
    }

    private fun readUnlocked(): List<SavedPc> {
        if (!file.baseFile.exists()) return emptyList()
        val text = file.openRead().use { it.readBytes().toString(Charsets.UTF_8) }
        return json.decodeFromString<List<SavedPc>>(text).also(SavedPcRules::validate)
    }

    private fun writeUnlocked(pcs: List<SavedPc>) {
        SavedPcRules.validate(pcs)
        val output = file.startWrite()
        try {
            output.write(json.encodeToString(pcs).toByteArray(Charsets.UTF_8))
            file.finishWrite(output)
        } catch (error: Exception) {
            file.failWrite(output)
            throw error
        }
    }

    companion object {
        @Volatile
        private var instance: SavedPcStore? = null

        fun get(context: Context): SavedPcStore = instance ?: synchronized(this) {
            instance ?: SavedPcStore(context.applicationContext).also { instance = it }
        }
    }
}
