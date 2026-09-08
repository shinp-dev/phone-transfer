package com.shinpstudio.phonetransfer

import com.shinpstudio.phonetransfer.data.SavedPc
import com.shinpstudio.phonetransfer.data.SavedPcRules
import java.util.UUID
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class SavedPcRulesTest {
    private fun pc(
        id: String = UUID.randomUUID().toString(),
        endpoint: String = "https://192.168.1.10:58443"
    ) = SavedPc(id, "PC", endpoint, "a".repeat(64))

    @Test
    fun persistedEndpointFieldRemainsBackwardCompatible() {
        val json = Json { ignoreUnknownKeys = false }
        val value = pc()
        val encoded = json.encodeToString(value)
        assertTrue(encoded.contains("\"endpoint\""))
        assertFalse(encoded.contains("lastKnownEndpoint"))
        assertEquals(value, json.decodeFromString<SavedPc>(encoded))
    }

    @Test
    fun addRejectsDuplicateWithoutReplacingExistingIdentity() {
        val existing = pc()
        val replacement = existing.copy(displayName = "Other")
        val error = assertThrows(IllegalStateException::class.java) {
            SavedPcRules.add(listOf(existing), replacement)
        }
        assertEquals("PC_ALREADY_SAVED", error.message)
    }

    @Test
    fun addPreservesExistingEntriesAndValidatesRouting() {
        val first = pc()
        val second = pc()
        assertEquals(listOf(first, second), SavedPcRules.add(listOf(first), second))
        assertThrows(IllegalArgumentException::class.java) {
            SavedPcRules.add(emptyList(), pc(endpoint = "https://example.com:58443"))
        }
    }

    @Test
    fun addEnforcesLimitAtCommitTime() {
        val full = (0 until SavedPcRules.LIMIT).map { pc() }
        val error = assertThrows(IllegalStateException::class.java) {
            SavedPcRules.add(full, pc())
        }
        assertEquals("PC_LIMIT", error.message)
    }

    @Test
    fun endpointUpdatePreservesStoredIdentityAndDoesNotResurrectRemovedPc() {
        val existing = pc()
        val updated = SavedPcRules.updateEndpoint(
            listOf(existing),
            existing.deviceId,
            "https://192.168.1.99:58443"
        )
        assertEquals(
            existing.copy(lastKnownEndpoint = "https://192.168.1.99:58443"),
            updated.single()
        )
        assertEquals(
            emptyList<SavedPc>(),
            SavedPcRules.updateEndpoint(emptyList(), existing.deviceId, existing.lastKnownEndpoint)
        )
    }
}
