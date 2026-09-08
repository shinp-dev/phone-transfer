package com.shinpstudio.phonetransfer.domain

import java.text.Normalizer
import java.util.Locale

internal object RemotePathRules {
    const val MAX_PATH_LENGTH = 4096
    private const val MAX_SEGMENT_LENGTH = 255
    private const val FORBIDDEN = "\\/:%<>\"|?*"

    fun validate(path: String, allowRoot: Boolean = false): String {
        if (path.isEmpty() && allowRoot) return path
        require(path.isNotEmpty() && path.length <= MAX_PATH_LENGTH) { "INVALID_REMOTE_PATH" }
        require(Normalizer.isNormalized(path, Normalizer.Form.NFC)) { "INVALID_REMOTE_PATH" }
        path.split('/').forEach(::validateName)
        return path
    }

    fun validateName(name: String): String {
        require(name.isNotEmpty() && name.length <= MAX_SEGMENT_LENGTH) { "INVALID_REMOTE_NAME" }
        require(name != "." && name != "..") { "INVALID_REMOTE_NAME" }
        require(!name.endsWith('.') && !name.endsWith(' ')) { "INVALID_REMOTE_NAME" }
        require(Normalizer.isNormalized(name, Normalizer.Form.NFC)) { "INVALID_REMOTE_NAME" }
        require(name.none { Character.isISOControl(it) || FORBIDDEN.contains(it) }) { "INVALID_REMOTE_NAME" }

        val stem = name.substringBefore('.').uppercase(Locale.ROOT)
        require(stem !in setOf("CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$")) {
            "INVALID_REMOTE_NAME"
        }
        val numberedDevice =
            stem.length == 4 &&
                (stem.startsWith("COM") || stem.startsWith("LPT")) &&
                "123456789¹²³".contains(stem[3])
        require(!numberedDevice) { "INVALID_REMOTE_NAME" }
        return name
    }

    fun join(directory: String, name: String): String {
        validate(directory, allowRoot = true)
        validateName(name)
        val result = if (directory.isEmpty()) name else "$directory/$name"
        require(result.length <= MAX_PATH_LENGTH) { "INVALID_REMOTE_PATH" }
        return result
    }

    fun parent(path: String): String {
        if (path.isEmpty()) return path
        validate(path)
        return path.substringBeforeLast('/', "")
    }

    fun fileName(path: String): String {
        validate(path)
        return path.substringAfterLast('/')
    }
}
