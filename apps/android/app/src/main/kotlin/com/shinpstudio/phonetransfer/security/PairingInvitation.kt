package com.shinpstudio.phonetransfer.security

import com.shinpstudio.phonetransfer.protocol.PairingQr
import java.net.URI
import java.text.Normalizer
import java.time.Instant
import java.util.Base64
import java.util.UUID
import kotlinx.serialization.json.Json

/** Validated routing information. Tokens must never be included in diagnostics. */
class PairingInvitation private constructor(
    val deviceId: String,
    val displayName: String,
    val endpoint: URI,
    val apiEndpoint: URI,
    val serverSpkiSha256: String,
    val token: String,
    val expiresAt: Instant
) {
    override fun toString(): String = "PairingInvitation(redacted)"

    companion object {
        private val json = Json { ignoreUnknownKeys = false }

        fun parse(payload: String, now: Instant): PairingInvitation {
            require(payload.length <= 4096) { "INVALID_PAIRING_QR" }
            val qr = json.decodeFromString<PairingQr>(payload)
            require(qr.protocolVersion == 1L) { "UNSUPPORTED_PROTOCOL" }
            val id = UUID.fromString(qr.deviceId)
            require(id.toString() == qr.deviceId && id != UUID(0, 0)) { "INVALID_PAIRING_QR" }
            require(
                qr.displayName.isNotBlank() &&
                    qr.displayName.length <= 128 &&
                    qr.displayName.none { Character.isISOControl(it) } &&
                    Normalizer.isNormalized(qr.displayName, Normalizer.Form.NFC)
            ) { "INVALID_PAIRING_QR" }
            require(qr.serverSpkiSha256.matches(Regex("[a-f0-9]{64}"))) { "INVALID_PAIRING_QR" }
            require(qr.token.matches(Regex("[A-Za-z0-9_-]{43}"))) { "INVALID_PAIRING_QR" }
            val tokenBytes = Base64.getUrlDecoder().decode(qr.token)
            require(
                tokenBytes.size == 32 &&
                    Base64.getUrlEncoder().withoutPadding().encodeToString(tokenBytes) == qr.token
            ) { "INVALID_PAIRING_QR" }
            val expires = Instant.parse(qr.expiresAt)
            require(expires > now && expires <= now.plusSeconds(150)) { "PAIRING_QR_EXPIRED" }
            val bootstrap = lanEndpoint(qr.endpoint)
            val api = lanEndpoint(qr.apiEndpoint)
            require(bootstrap.host == api.host && bootstrap.port != api.port) {
                "INVALID_PAIRING_QR"
            }
            return PairingInvitation(
                qr.deviceId,
                qr.displayName,
                bootstrap,
                api,
                qr.serverSpkiSha256,
                qr.token,
                expires
            )
        }

        private fun lanEndpoint(value: String): URI {
            // Numeric hosts avoid DNS rebinding and accidental Internet/proxy destinations.
            require(value.matches(Regex("https://[0-9.]+:[0-9]{1,5}"))) { "INVALID_PAIRING_QR" }
            val uri = URI(value)
            require(uri.port in 1..65535) { "INVALID_PAIRING_QR" }
            val octets = (uri.host ?: "").split('.')
            require(octets.size == 4) { "INVALID_PAIRING_QR" }
            val bytes = octets.map {
                val number = it.toIntOrNull()
                require(number != null && number in 0..255 && number.toString() == it) {
                    "INVALID_PAIRING_QR"
                }
                number
            }
            require(
                bytes[0] == 10 ||
                    (bytes[0] == 172 && bytes[1] in 16..31) ||
                    (bytes[0] == 192 && bytes[1] == 168) ||
                    (bytes[0] == 169 && bytes[1] == 254)
            ) { "INVALID_PAIRING_QR" }
            return uri
        }
    }
}
