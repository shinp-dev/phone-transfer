package com.shinpstudio.phonetransfer.domain

internal object TransferWireRules {
    const val CHUNK_BYTES = 1024 * 1024
    const val MAX_FILE_BYTES = 1_099_511_627_776L
    private val sha256 = Regex("[a-f0-9]{64}")

    fun expectedOffset(current: Long, count: Int, total: Long): Long {
        require(current >= 0 && count >= 0 && total >= 0) { "INVALID_UPLOAD_OFFSET" }
        val next = Math.addExact(current, count.toLong())
        require(next <= total) { "INVALID_UPLOAD_OFFSET" }
        return next
    }

    fun strongSha256Etag(value: String?): String {
        require(
            value != null &&
                value.length == 66 &&
                value.first() == '"' &&
                value.last() == '"'
        ) {
            "INVALID_ETAG"
        }
        val digest = value.substring(1, value.length - 1)
        require(sha256.matches(digest)) { "INVALID_ETAG" }
        return digest
    }
}
