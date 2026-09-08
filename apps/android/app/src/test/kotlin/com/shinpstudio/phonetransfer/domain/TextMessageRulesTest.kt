package com.shinpstudio.phonetransfer.domain

import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class TextMessageRulesTest {
    @Test
    fun plainTextAcceptsNonEmptyContentWithinLimit() {
        TextMessageRules.validate(TextMessageRules.PLAIN_TEXT, "hello")
        TextMessageRules.validate(TextMessageRules.PLAIN_TEXT, "x".repeat(TextMessageRules.MAX_CONTENT_LENGTH))
    }

    @Test
    fun emptyAndOversizedContentAreRejected() {
        assertThrows(IllegalArgumentException::class.java) {
            TextMessageRules.validate(TextMessageRules.PLAIN_TEXT, "")
        }
        assertThrows(IllegalArgumentException::class.java) {
            TextMessageRules.validate(
                TextMessageRules.PLAIN_TEXT,
                "x".repeat(TextMessageRules.MAX_CONTENT_LENGTH + 1)
            )
        }
    }

    @Test
    fun urlAcceptsOnlyAbsoluteHttpOrHttpsWithoutCredentials() {
        assertTrue(TextMessageRules.isHttpUrl("https://example.com/path?q=1"))
        assertTrue(TextMessageRules.isHttpUrl("HTTP://example.com"))
        assertFalse(TextMessageRules.isHttpUrl("ftp://example.com/file"))
        assertFalse(TextMessageRules.isHttpUrl("/relative/path"))
        assertFalse(TextMessageRules.isHttpUrl("https://user:pass@example.com/private"))

        TextMessageRules.validate(TextMessageRules.URL, "https://example.com")
        assertThrows(IllegalArgumentException::class.java) {
            TextMessageRules.validate(TextMessageRules.URL, "javascript:alert(1)")
        }
    }

    @Test
    fun unknownKindIsRejected() {
        assertThrows(IllegalArgumentException::class.java) {
            TextMessageRules.validate("other", "hello")
        }
    }
}
