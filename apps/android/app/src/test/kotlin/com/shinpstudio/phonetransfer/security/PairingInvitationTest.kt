package com.shinpstudio.phonetransfer.security

import com.shinpstudio.phonetransfer.protocol.PairingQr
import java.time.Instant
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Test

class PairingInvitationTest {
    private val now = Instant.parse("2026-09-07T12:00:00Z")
    private val qr = PairingQr(
        protocolVersion = 1,
        deviceId = "ec663bc9-8426-4050-a419-6f4f42f8a2be",
        displayName = "My PC",
        endpoint = "https://192.168.1.10:58442",
        serverSpkiSha256 = "a".repeat(64),
        token = "A".repeat(43),
        expiresAt = now.plusSeconds(120).toString(),
        apiEndpoint = "https://192.168.1.10:58443"
    )

    @Test
    fun acceptsWindowsInvitationAndRedactsToken() {
        val invitation = PairingInvitation.parse(Json.encodeToString(qr), now)
        assertEquals(qr.deviceId, invitation.deviceId)
        assertEquals(58443, invitation.apiEndpoint.port)
        assertFalse(invitation.toString().contains(qr.token))
    }

    @Test
    fun rejectsUnsafeDestinationsBeforeAnyNetworkRequest() {
        listOf(
            "http://192.168.1.10:58442",
            "https://example.com:58442",
            "https://127.0.0.1:58442",
            "https://8.8.8.8:58442",
            "https://192.168.001.10:58442",
            "https://192.168.1.10:0",
            "https://192.168.1.10:65536",
            "https://192.168.1.10:58442/path",
            "https://user@192.168.1.10:58442",
            "https://192.168.1.10:58442?secret=x"
        ).forEach { endpoint ->
            assertThrows(IllegalArgumentException::class.java) {
                PairingInvitation.parse(Json.encodeToString(qr.copy(endpoint = endpoint)), now)
            }
        }
    }

    @Test
    fun rejectsExpiredAndMalformedIdentities() {
        listOf(
            qr.copy(expiresAt = now.toString()),
            qr.copy(expiresAt = now.plusSeconds(151).toString()),
            qr.copy(protocolVersion = 2),
            qr.copy(deviceId = qr.deviceId.uppercase()),
            qr.copy(displayName = "PC\nAdmin"),
            qr.copy(serverSpkiSha256 = "A".repeat(64)),
            qr.copy(token = "A".repeat(42) + "B"),
            qr.copy(apiEndpoint = "https://192.168.1.11:58443"),
            qr.copy(apiEndpoint = qr.endpoint)
        ).forEach { invalid ->
            assertThrows(IllegalArgumentException::class.java) {
                PairingInvitation.parse(Json.encodeToString(invalid), now)
            }
        }
    }

    @Test
    fun rejectsUnboundedOrUnknownPayloads() {
        assertThrows(IllegalArgumentException::class.java) {
            PairingInvitation.parse(" ".repeat(4097), now)
        }
        assertThrows(IllegalArgumentException::class.java) {
            PairingInvitation.parse(Json.encodeToString(qr).dropLast(1) + ",\"extra\":true}", now)
        }
    }
}
