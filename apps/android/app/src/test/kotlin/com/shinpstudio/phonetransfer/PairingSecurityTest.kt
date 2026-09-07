package com.shinpstudio.phonetransfer

import com.shinpstudio.phonetransfer.security.PairingProof
import com.shinpstudio.phonetransfer.security.PinnedTrustManager
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import java.util.Date
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.asn1.x509.BasicConstraints
import org.bouncycastle.asn1.x509.ExtendedKeyUsage
import org.bouncycastle.asn1.x509.Extension
import org.bouncycastle.asn1.x509.KeyPurposeId
import org.bouncycastle.asn1.x509.KeyUsage
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingSecurityTest {
    private val pair = KeyPairGenerator.getInstance("EC").apply {
        initialize(ECGenParameterSpec("secp256r1"))
    }.generateKeyPair()

    private fun certificate(server: Boolean = true, expired: Boolean = false, ca: Boolean = false): X509Certificate {
        val name = X500Name("CN=test")
        val now = System.currentTimeMillis()
        val bytes = JcaX509v3CertificateBuilder(name, BigInteger.ONE, Date(now - 60_000),
            Date(now + if (expired) -1000 else 60_000), name, pair.public)
            .addExtension(Extension.basicConstraints, true, BasicConstraints(ca))
            .addExtension(Extension.keyUsage, true, KeyUsage(KeyUsage.digitalSignature))
            .addExtension(Extension.extendedKeyUsage, true,
                ExtendedKeyUsage(if (server) KeyPurposeId.id_kp_serverAuth else KeyPurposeId.id_kp_clientAuth))
            .build(JcaContentSignerBuilder("SHA256withECDSA").build(pair.private)).encoded
        return CertificateFactory.getInstance("X.509").generateCertificate(bytes.inputStream()) as X509Certificate
    }

    @Test
    fun proofBindsAllFieldsAndUsesDerEcdsa() {
        val transcript = PairingProof.transcript("id", "phone", "token", byteArrayOf(1, 2, 3))
        assertEquals("phone-transfer/pairing/v1\nid\nphone\ntoken\n039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", transcript)
        val signed = Base64.getDecoder().decode(PairingProof.sign(pair.private, transcript))
        val verifier = Signature.getInstance("SHA256withECDSA")
        verifier.initVerify(pair.public)
        verifier.update(transcript.toByteArray())
        assertTrue(verifier.verify(signed))
        verifier.update((transcript + "x").toByteArray())
        assertFalse(verifier.verify(signed))
        assertEquals(6, PairingProof.comparisonCode(transcript).length)
    }

    @Test
    fun pinDoesNotBypassValidityOrServerUsage() {
        val trust = PinnedTrustManager(PairingProof.sha256(pair.public.encoded))
        trust.checkServerTrusted(arrayOf(certificate()), "EC")
        for (bad in listOf(certificate(server = false), certificate(expired = true), certificate(ca = true))) {
            assertThrows(CertificateException::class.java) { trust.checkServerTrusted(arrayOf(bad), "EC") }
        }
        assertThrows(CertificateException::class.java) {
            PinnedTrustManager("0".repeat(64)).checkServerTrusted(arrayOf(certificate()), "EC")
        }
        assertThrows(CertificateException::class.java) { trust.checkServerTrusted(emptyArray(), "EC") }
    }
}
