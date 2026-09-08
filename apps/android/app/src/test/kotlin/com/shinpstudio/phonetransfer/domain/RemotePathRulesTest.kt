package com.shinpstudio.phonetransfer.domain

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class RemotePathRulesTest {
    @Test
    fun joinsRootAndNestedDirectoriesWithoutNormalizingInput() {
        assertEquals("photo.jpg", RemotePathRules.join("", "photo.jpg"))
        assertEquals("旅行/2026/photo.jpg", RemotePathRules.join("旅行/2026", "photo.jpg"))
        assertEquals("旅行/2026", RemotePathRules.parent("旅行/2026/photo.jpg"))
        assertEquals("", RemotePathRules.parent("photo.jpg"))
    }

    @Test
    fun rejectsWindowsAmbiguousNames() {
        listOf(
            "..",
            ".",
            "name.",
            "name ",
            "a/b",
            "a\\b",
            "file.txt:secret",
            "%2e%2e",
            "NUL.txt",
            "COM1.txt",
            "LPT9",
            "cafe\u0301.txt"
        ).forEach { name ->
            assertThrows(IllegalArgumentException::class.java) {
                RemotePathRules.validateName(name)
            }
        }
    }

    @Test
    fun rejectsTraversalAndEmptySegmentsInRemotePaths() {
        listOf("../secret", "a/../secret", "/absolute", "a//b", "a/./b").forEach { path ->
            assertThrows(IllegalArgumentException::class.java) {
                RemotePathRules.validate(path)
            }
        }
        assertEquals("", RemotePathRules.validate("", allowRoot = true))
    }
}
