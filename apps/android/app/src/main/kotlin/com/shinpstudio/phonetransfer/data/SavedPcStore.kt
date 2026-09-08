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
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.decodeFromJsonElement

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

    fun updateEndpoint(pcs: List<SavedPc>, id: String, endpoint: String): List<SavedPc> {
        validate(pcs)
        val index = pcs.indexOfFirst { it.deviceId == id }
        if (index < 0) return pcs
        val updated = pcs[index].copy(lastKnownEndpoint = endpoint)
        validatePc(updated)
        return pcs.toMutableList().also { it[index] = updated }
    }

    private fun validatePc(pc: SavedPc) {
        check(UUID.fromString(pc.deviceId).toString() == pc.deviceId)
        PairingInvitation.lanEndpoint(pc.lastKnownEndpoint)
        check(pc.pin.matches(Regex("[a-f0-9]{64}")))
    }
}

@Serializable
private data class SavedPcDocument(val version: Int, val pcs: List<SavedPc>)

internal object SavedPcPersistence {
    private const val VERSION = 1
    private val json = Json { ignoreUnknownKeys = false }

    fun decode(text: String): List<SavedPc> {
        val root = json.parseToJsonElement(text)
        val pcs = when (root) {
            is JsonArray -> json.decodeFromJsonElement<List<SavedPc>>(root)
            is JsonObject -> {
                val document = json.decodeFromJsonElement<SavedPcDocument>(root)
                check(document.version == VERSION) { "SAVED_PC_VERSION" }
                document.pcs
            }
            else -> error("SAVED_PC_FORMAT")
        }
        SavedPcRules.validate(pcs)
        return pcs
    }

    fun encode(pcs: List<SavedPc>): String {
        SavedPcRules.validate(pcs)
        return json.encodeToString(SavedPcDocument(VERSION, pcs))
    }
}

internal class SavedPcStore private constructor(context: Context) {
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

    @Synchronized
    fun updateEndpoint(id: String, endpoint: String): List<SavedPc> {
        val current = readUnlocked()
        val next = SavedPcRules.updateEndpoint(current, id, endpoint)
        if (next != current) writeUnlocked(next)
        return next
    }

    private fun readUnlocked(): List<SavedPc> {
        if (!file.baseFile.exists()) return emptyList()
        val text = file.openRead().use { it.readBytes().toString(Charsets.UTF_8) }
        return SavedPcPersistence.decode(text)
    }

    private fun writeUnlocked(pcs: List<SavedPc>) {
        val output = file.startWrite()
        try {
            output.write(SavedPcPersistence.encode(pcs).toByteArray(Charsets.UTF_8))
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
