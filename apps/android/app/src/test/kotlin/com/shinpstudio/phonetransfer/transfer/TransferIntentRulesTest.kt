package com.shinpstudio.phonetransfer.transfer

import com.shinpstudio.phonetransfer.data.FileTransferException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class TransferIntentRulesTest {
    @Test
    fun acceptsEmptyShareRootWhenExplicitlyAllowed() {
        assertEquals("", requireIntentStringValue("", allowEmpty = true))
    }

    @Test
    fun rejectsMissingAndUnexpectedEmptyValues() {
        listOf(null, "").forEach { value ->
            val error = assertThrows(FileTransferException::class.java) {
                requireIntentStringValue(value)
            }
            assertEquals("INVALID_TRANSFER_INTENT", error.code)
        }
    }
}
