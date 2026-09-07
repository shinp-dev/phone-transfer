package com.shinpstudio.phonetransfer.security

import java.nio.ByteBuffer
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.Signature
import java.util.Base64
import java.util.Locale

object PairingProof {
    fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256")
        .digest(bytes).joinToString("") { "%02x".format(it) }

    fun transcript(id: String, name: String, token: String, certificate: ByteArray): String =
        "phone-transfer/pairing/v1\n$id\n$name\n$token\n${sha256(certificate)}"

    fun comparisonCode(transcript: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(transcript.toByteArray(Charsets.UTF_8))
        val number = ByteBuffer.wrap(digest).int.toLong() and 0xffffffffL
        return String.format(Locale.ROOT, "%06d", number % 1_000_000)
    }

    fun sign(key: PrivateKey, transcript: String): String {
        val signer = Signature.getInstance("SHA256withECDSA")
        signer.initSign(key)
        signer.update(transcript.toByteArray(Charsets.UTF_8))
        return Base64.getEncoder().encodeToString(signer.sign())
    }
}
