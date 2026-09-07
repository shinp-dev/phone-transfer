package com.shinpstudio.phonetransfer.security

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import java.io.File
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.Date
import java.util.UUID
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.asn1.x509.BasicConstraints
import org.bouncycastle.asn1.x509.ExtendedKeyUsage
import org.bouncycastle.asn1.x509.Extension
import org.bouncycastle.asn1.x509.KeyPurposeId
import org.bouncycastle.asn1.x509.KeyUsage
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder

class ClientIdentity(val deviceId: String, val key: PrivateKey, val certificate: X509Certificate) {
    companion object {
        @Synchronized
        fun load(context: Context): ClientIdentity {
            val preferences = context.getSharedPreferences("identity", Context.MODE_PRIVATE)
            val id = preferences.getString("deviceId", null) ?: UUID.randomUUID().toString().also {
                check(preferences.edit().putString("deviceId", it).commit()) { "IDENTITY_SAVE_FAILED" }
            }
            val alias = "PhoneTransfer.Client.$id"
            val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
            val file = AtomicFile(File(context.filesDir, "client-certificate.der"))
            if (!store.containsAlias(alias)) {
                // A lost key cannot silently replace a previously registered identity.
                check(!file.baseFile.exists()) { "IDENTITY_KEY_MISSING" }
                KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
                    initialize(
                        KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
                            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                            .setDigests(KeyProperties.DIGEST_SHA256)
                            .setUserAuthenticationRequired(false)
                            .build()
                    )
                }.generateKeyPair()
            }
            val key = store.getKey(alias, null) as PrivateKey
            val publicKey = store.getCertificate(alias).publicKey
            val certificate = if (file.baseFile.exists()) {
                file.openRead().use { CertificateFactory.getInstance("X.509").generateCertificate(it) as X509Certificate }
            } else {
                val now = Instant.now()
                val name = X500Name("CN=PhoneTransfer-$id")
                val certificate = JcaX509v3CertificateBuilder(
                    name, BigInteger(128, SecureRandom()).setBit(127),
                    Date.from(now.minus(1, ChronoUnit.DAYS)), Date.from(now.plus(3650, ChronoUnit.DAYS)),
                    name, publicKey
                ).addExtension(Extension.basicConstraints, true, BasicConstraints(false))
                    .addExtension(Extension.keyUsage, true, KeyUsage(KeyUsage.digitalSignature))
                    .addExtension(Extension.extendedKeyUsage, true, ExtendedKeyUsage(KeyPurposeId.id_kp_clientAuth))
                    .build(JcaContentSignerBuilder("SHA256withECDSA").build(key)).encoded
                val output = file.startWrite()
                try {
                    output.write(certificate)
                    file.finishWrite(output)
                } catch (error: Exception) {
                    file.failWrite(output)
                    throw error
                }
                CertificateFactory.getInstance("X.509").generateCertificate(certificate.inputStream()) as X509Certificate
            }
            certificate.checkValidity()
            check(certificate.publicKey.encoded.contentEquals(publicKey.encoded)) { "IDENTITY_KEY_MISMATCH" }
            certificate.verify(publicKey)
            return ClientIdentity(id, key, certificate)
        }
    }
}
