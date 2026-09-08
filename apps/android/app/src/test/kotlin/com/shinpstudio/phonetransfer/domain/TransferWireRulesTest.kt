package com.shinpstudio.phonetransfer.domain

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class TransferWireRulesTest {
    @Test
    fun acceptsCanonicalStrongSha256Etag() {
        val digest = "a".repeat(64)
        assertEquals(digest, TransferWireRules.strongSha256Etag("\"$digest\""))
    }

    @Test
    fun rejectsWeakMalformedAndUppercaseEtags() {
        listOf(
            null,
            "W/\"${"a".repeat(64)}\"",
            "\"${"A".repeat(64)}\"",
            "\"short\"",
            "${"a".repeat(64)}"
        ).forEach { value ->
            assertThrows(IllegalArgumentException::class.java) {
                TransferWireRules.strongSha256Etag(value)
            }
        }
    }

    @Test
    fun uploadOffsetCannotAdvancePastDeclaredSize() {
        assertEquals(
            1_048_576L,
            TransferWireRules.expectedOffset(0, 1_048_576, 2_000_000)
        )
        assertThrows(IllegalArgumentException::class.java) {
            TransferWireRules.expectedOffset(1_900_000, 200_000, 2_000_000)
        }
    }
}
