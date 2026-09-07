package com.shinpstudio.phonetransfer.security

import android.annotation.SuppressLint
import java.net.Proxy
import java.net.Socket
import java.security.Principal
import java.security.PrivateKey
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.X509TrustManager
import okhttp3.ConnectionSpec
import okhttp3.OkHttpClient

// ADR013: QR SPKI is the trust anchor; no system/public CA fallback is allowed.
@SuppressLint("CustomX509TrustManager")
class PinnedTrustManager(private val pin: String) : X509TrustManager {
    init {
        require(pin.matches(Regex("[a-f0-9]{64}")))
    }

    override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
        val leaf = chain.firstOrNull() ?: throw CertificateException("MISSING_CERTIFICATE")
        leaf.checkValidity()
        if (leaf.basicConstraints != -1 ||
            leaf.keyUsage?.getOrNull(0) != true ||
            leaf.extendedKeyUsage?.contains("1.3.6.1.5.5.7.3.1") != true ||
            PairingProof.sha256(leaf.publicKey.encoded) != pin
        ) {
            throw CertificateException("SERVER_IDENTITY_MISMATCH")
        }
    }

    override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String): Unit =
        throw CertificateException("CLIENT_TRUST_UNSUPPORTED")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}

object PinnedTls {
    fun client(endpoint: String, pin: String, identity: ClientIdentity? = null): OkHttpClient {
        val target = PairingInvitation.lanEndpoint(endpoint)
        val trust = PinnedTrustManager(pin)
        val keys = identity?.let { arrayOf(ClientKeyManager(it)) }
        val context = SSLContext.getInstance("TLS").apply { init(keys, arrayOf(trust), null) }
        return OkHttpClient.Builder()
            .sslSocketFactory(context.socketFactory, trust)
            // Identity is the QR SPKI, not an IP SAN. Still constrain the routing host.
            .hostnameVerifier { host, session ->
                host == target.host &&
                    runCatching {
                        trust.checkServerTrusted(
                            session.peerCertificates.map {
                                it as X509Certificate
                            }.toTypedArray(),
                            "EC"
                        )
                    }.isSuccess
            }
            .proxy(Proxy.NO_PROXY)
            .followRedirects(false)
            .followSslRedirects(false)
            .retryOnConnectionFailure(false)
            .connectionSpecs(listOf(ConnectionSpec.MODERN_TLS))
            .callTimeout(15, TimeUnit.SECONDS)
            .addInterceptor { chain ->
                val url = chain.request().url
                check(url.isHttps && url.host == target.host && url.port == target.port) {
                    "ENDPOINT_MISMATCH"
                }
                chain.proceed(chain.request())
            }.build()
    }
}

private class ClientKeyManager(private val identity: ClientIdentity) : X509ExtendedKeyManager() {
    override fun getClientAliases(
        keyType: String?,
        issuers: Array<out Principal>?
    ): Array<String>? = if (keyType == "EC") arrayOf("client") else null
    override fun chooseClientAlias(
        keyType: Array<out String>?,
        issuers: Array<out Principal>?,
        socket: Socket?
    ): String? = if (keyType?.contains("EC") == true) "client" else null
    override fun getCertificateChain(alias: String?): Array<X509Certificate>? =
        if (alias == "client") arrayOf(identity.certificate) else null
    override fun getPrivateKey(alias: String?): PrivateKey? = if (alias ==
        "client"
    ) {
        identity.key
    } else {
        null
    }
    override fun getServerAliases(
        keyType: String?,
        issuers: Array<out Principal>?
    ): Array<String>? = null
    override fun chooseServerAlias(
        keyType: String?,
        issuers: Array<out Principal>?,
        socket: Socket?
    ): String? = null
}
