package com.shinpstudio.phonetransfer.security

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import java.io.File
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.Date
import java.util.UUID

class ClientIdentity(val deviceId: String, val key: PrivateKey, val certificate: X509Certificate) {
    companion object {
        @Synchronized
        fun load(context: Context): ClientIdentity {
            val idFile = AtomicFile(File(context.filesDir, "device-id"))
            val id = if (idFile.baseFile.exists()) {
                idFile.openRead().use { it.readBytes().toString(Charsets.UTF_8) }
            } else {
                val newId = UUID.randomUUID().toString()
                val output = idFile.startWrite()
                try {
                    output.write(newId.toByteArray(Charsets.UTF_8))
                    idFile.finishWrite(output)
                } catch (error: Exception) {
                    idFile.failWrite(output)
                    throw error
                }
                newId
            }
            check(UUID.fromString(id).toString() == id)
            val alias = "PhoneTransfer.Client.$id"
            val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
            val file = AtomicFile(File(context.filesDir, "client-certificate.der"))
            if (!store.containsAlias(alias)) {
                // A lost key cannot silently replace a previously registered identity.
                check(!file.baseFile.exists()) { "IDENTITY_KEY_MISSING" }
                KeyPairGenerator.getInstance(
                    KeyProperties.KEY_ALGORITHM_EC,
                    "AndroidKeyStore"
                ).apply {
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
                file.openRead().use {
                    CertificateFactory.getInstance(
                        "X.509"
                    ).generateCertificate(it) as X509Certificate
                }
            } else {
                val now = Instant.now()
                val certificate = IdentityCertificate.create(
                    "CN=PhoneTransfer-$id",
                    publicKey,
                    key,
                    Date.from(now.minus(1, ChronoUnit.DAYS)),
                    Date.from(now.plus(3650, ChronoUnit.DAYS))
                ).encoded
                val output = file.startWrite()
                try {
                    output.write(certificate)
                    file.finishWrite(output)
                } catch (error: Exception) {
                    file.failWrite(output)
                    throw error
                }
                CertificateFactory.getInstance(
                    "X.509"
                ).generateCertificate(certificate.inputStream()) as X509Certificate
            }
            certificate.checkValidity()
            check(certificate.publicKey.encoded.contentEquals(publicKey.encoded)) {
                "IDENTITY_KEY_MISMATCH"
            }
            certificate.verify(publicKey)
            return ClientIdentity(id, key, certificate)
        }
    }
}
