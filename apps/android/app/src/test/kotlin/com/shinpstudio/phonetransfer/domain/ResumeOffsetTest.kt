package com.shinpstudio.phonetransfer.domain

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class ResumeOffsetTest {
    @Test
    fun supportsOffsetsBeyondTwoGigabytes() {
        assertEquals(3_000_000_100L, ResumeOffset.next(4_000_000_000L, 3_000_000_000L, 3_000_000_000L, 100))
    }

    @Test
    fun rejectsStaleClientOffset() {
        assertThrows(IllegalArgumentException::class.java) { ResumeOffset.next(100, 20, 10, 10) }
    }

    @Test
    fun rejectsOversizedFinalChunk() {
        assertThrows(IllegalArgumentException::class.java) { ResumeOffset.next(100, 90, 90, 11) }
    }
}
