package com.shinpstudio.phonetransfer.security

import java.math.BigInteger
import java.security.PrivateKey
import java.security.PublicKey
import java.security.SecureRandom
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.Date
import org.bouncycastle.asn1.ASN1Encodable
import org.bouncycastle.asn1.ASN1Integer
import org.bouncycastle.asn1.DERBitString
import org.bouncycastle.asn1.DERSequence
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.asn1.x509.AlgorithmIdentifier
import org.bouncycastle.asn1.x509.BasicConstraints
import org.bouncycastle.asn1.x509.ExtendedKeyUsage
import org.bouncycastle.asn1.x509.Extension
import org.bouncycastle.asn1.x509.ExtensionsGenerator
import org.bouncycastle.asn1.x509.KeyPurposeId
import org.bouncycastle.asn1.x509.KeyUsage
import org.bouncycastle.asn1.x509.SubjectPublicKeyInfo
import org.bouncycastle.asn1.x509.Time
import org.bouncycastle.asn1.x509.V3TBSCertificateGenerator
import org.bouncycastle.asn1.x9.X9ObjectIdentifiers

/** ASN.1 encoding only; all signing stays in the platform JCA/Keystore provider. */
internal object IdentityCertificate {
    fun create(
        name: String,
        publicKey: PublicKey,
        privateKey: PrivateKey,
        notBefore: Date,
        notAfter: Date,
        server: Boolean = false,
        ca: Boolean = false
    ): X509Certificate {
        val algorithm = AlgorithmIdentifier(X9ObjectIdentifiers.ecdsa_with_SHA256)
        val extensions = ExtensionsGenerator().apply {
            addExtension(Extension.basicConstraints, true, BasicConstraints(ca))
            addExtension(Extension.keyUsage, true, KeyUsage(KeyUsage.digitalSignature))
            val purpose = if (server) {
                KeyPurposeId.id_kp_serverAuth
            } else {
                KeyPurposeId.id_kp_clientAuth
            }
            addExtension(Extension.extendedKeyUsage, true, ExtendedKeyUsage(purpose))
        }
        val body = V3TBSCertificateGenerator().apply {
            setSerialNumber(ASN1Integer(BigInteger(128, SecureRandom()).setBit(127)))
            setSignature(algorithm)
            setIssuer(X500Name(name))
            setSubject(X500Name(name))
            setStartDate(Time(notBefore))
            setEndDate(Time(notAfter))
            setSubjectPublicKeyInfo(SubjectPublicKeyInfo.getInstance(publicKey.encoded))
            setExtensions(extensions.generate())
        }.generateTBSCertificate()
        val signer = Signature.getInstance("SHA256withECDSA").apply {
            initSign(privateKey)
            update(body.encoded)
        }
        val encoded = DERSequence(
            arrayOf<ASN1Encodable>(body, algorithm, DERBitString(signer.sign()))
        ).encoded
        return CertificateFactory.getInstance("X.509")
            .generateCertificate(encoded.inputStream()) as X509Certificate
    }
}
