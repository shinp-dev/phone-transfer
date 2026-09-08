package com.shinpstudio.phonetransfer

import com.shinpstudio.phonetransfer.data.DiscoveryRecordRules
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DiscoveryRecordRulesTest {
    private val id = UUID.randomUUID().toString()

    private fun attributes(version: String = "1", deviceId: String = id) = mapOf(
        "version" to version.toByteArray(),
        "deviceId" to deviceId.toByteArray()
    )

    @Test
    fun acceptsPrivateIpv4WithCanonicalIdentity() {
        val result = DiscoveryRecordRules.parse(attributes(), "192.168.10.8", 58443)
        assertEquals(id, result?.deviceId)
        assertEquals("https://192.168.10.8:58443", result?.endpoint)
    }

    @Test
    fun rejectsUntrustedRoutingAndWrongProtocol() {
        assertNull(DiscoveryRecordRules.parse(attributes(), "8.8.8.8", 58443))
        assertNull(DiscoveryRecordRules.parse(attributes(), "127.0.0.1", 58443))
        assertNull(DiscoveryRecordRules.parse(attributes(), "192.168.1.8", 443))
        assertNull(DiscoveryRecordRules.parse(attributes(version = "2"), "192.168.1.8", 58443))
    }

    @Test
    fun rejectsMalformedOrEmptyDeviceIdentity() {
        assertNull(DiscoveryRecordRules.parse(attributes(deviceId = "not-a-uuid"), "10.0.0.2", 58443))
        assertNull(
            DiscoveryRecordRules.parse(
                attributes(deviceId = "00000000-0000-0000-0000-000000000000"),
                "10.0.0.2",
                58443
            )
        )
    }

    @Test
    fun serviceTypeAllowsOnlyDnsSdTrailingDotVariation() {
        assertEquals(true, DiscoveryRecordRules.matchesServiceType("_phone-transfer._tcp"))
        assertEquals(true, DiscoveryRecordRules.matchesServiceType("_phone-transfer._tcp."))
        assertEquals(false, DiscoveryRecordRules.matchesServiceType("_http._tcp"))
    }
}
