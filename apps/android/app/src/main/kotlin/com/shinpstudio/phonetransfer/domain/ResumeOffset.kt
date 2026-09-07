package com.shinpstudio.phonetransfer.domain

object ResumeOffset {
    const val MAXIMUM_CHUNK_BYTES: Int = 4 * 1024 * 1024

    fun next(total: Long, committed: Long, requestOffset: Long, chunkSize: Int): Long {
        require(total >= 0 && committed >= 0 && committed <= total)
        require(requestOffset == committed)
        require(chunkSize in 1..MAXIMUM_CHUNK_BYTES && chunkSize <= total - committed)
        return committed + chunkSize
    }
}
