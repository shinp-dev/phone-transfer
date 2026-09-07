package com.shinpstudio.phonetransfer

import com.shinpstudio.phonetransfer.security.IdentityCertificate
import com.shinpstudio.phonetransfer.security.PairingProof
import com.shinpstudio.phonetransfer.security.PinnedTrustManager
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import java.util.Date
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingSecurityTest {
    private val pair = KeyPairGenerator.getInstance("EC").apply {
        initialize(ECGenParameterSpec("secp256r1"))
    }.generateKeyPair()

    private fun certificate(
        server: Boolean = true,
        expired: Boolean = false,
        ca: Boolean = false
    ): X509Certificate {
        val now = System.currentTimeMillis()
        return IdentityCertificate.create(
            "CN=test",
            pair.public,
            pair.private,
            Date(now - 60_000),
            Date(now + if (expired) -1000 else 60_000),
            server,
            ca
        )
    }

    @Test
    fun proofBindsAllFieldsAndUsesDerEcdsa() {
        val transcript = PairingProof.transcript("id", "phone", "token", byteArrayOf(1, 2, 3))
        assertEquals(
            "phone-transfer/pairing/v1\nid\nphone\ntoken\n039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81",
            transcript
        )
        val signed = Base64.getDecoder().decode(PairingProof.sign(pair.private, transcript))
        val verifier = Signature.getInstance("SHA256withECDSA")
        verifier.initVerify(pair.public)
        verifier.update(transcript.toByteArray())
        assertTrue(verifier.verify(signed))
        verifier.update((transcript + "x").toByteArray())
        assertFalse(verifier.verify(signed))
        assertEquals("167709", PairingProof.comparisonCode(transcript))
    }

    @Test
    fun pinDoesNotBypassValidityOrServerUsage() {
        val server = certificate()
        server.verify(pair.public)
        val client = certificate(server = false)
        client.verify(pair.public)
        assertEquals(-1, client.basicConstraints)
        assertTrue(client.keyUsage[0])
        assertTrue(client.extendedKeyUsage.contains("1.3.6.1.5.5.7.3.2"))
        val trust = PinnedTrustManager(PairingProof.sha256(pair.public.encoded))
        trust.checkServerTrusted(arrayOf(server), "EC")
        for (bad in listOf(
            certificate(server = false),
            certificate(expired = true),
            certificate(ca = true)
        )) {
            assertThrows(CertificateException::class.java) {
                trust.checkServerTrusted(arrayOf(bad), "EC")
            }
        }
        assertThrows(CertificateException::class.java) {
            PinnedTrustManager("0".repeat(64)).checkServerTrusted(arrayOf(certificate()), "EC")
        }
        assertThrows(CertificateException::class.java) {
            trust.checkServerTrusted(emptyArray(), "EC")
        }
    }
}
