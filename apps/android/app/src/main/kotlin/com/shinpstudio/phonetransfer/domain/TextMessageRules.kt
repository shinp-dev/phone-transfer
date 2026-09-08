package com.shinpstudio.phonetransfer.domain

import java.net.URI

internal object TextMessageRules {
    const val PLAIN_TEXT = "plainText"
    const val URL = "url"
    const val MAX_CONTENT_LENGTH = 65_536

    fun validate(kind: String, content: String) {
        require(kind == PLAIN_TEXT || kind == URL) { "INVALID_TEXT_KIND" }
        require(content.isNotEmpty() && content.length <= MAX_CONTENT_LENGTH) {
            "INVALID_TEXT_CONTENT"
        }
        if (kind == URL) require(isHttpUrl(content)) { "INVALID_URL" }
    }

    fun isHttpUrl(content: String): Boolean {
        if (content.isEmpty() || content.length > MAX_CONTENT_LENGTH) return false
        return try {
            val uri = URI(content)
            val scheme = uri.scheme?.lowercase()
            (scheme == "http" || scheme == "https") &&
                !uri.rawAuthority.isNullOrBlank() &&
                uri.userInfo == null
        } catch (_: Exception) {
            false
        }
    }
}
