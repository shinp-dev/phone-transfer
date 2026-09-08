package com.shinpstudio.phonetransfer.data

import android.annotation.SuppressLint
import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import java.util.UUID
import kotlin.coroutines.resume
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine

data class DiscoveredPc(val deviceId: String, val endpoint: String)

internal object DiscoveryRecordRules {
    const val SERVICE_TYPE = "_phone-transfer._tcp"
    const val API_PORT = 58443

    fun parse(attributes: Map<String, ByteArray>, address: String?, port: Int): DiscoveredPc? {
        if (port != API_PORT || address == null || !isPrivateV4(address)) return null
        val version = attribute(attributes, "version") ?: return null
        val idText = attribute(attributes, "deviceId") ?: return null
        if (version != "1" || idText.length > 36) return null
        val id = runCatching { UUID.fromString(idText) }.getOrNull() ?: return null
        if (id == UUID(0, 0) || id.toString() != idText) return null
        return DiscoveredPc(idText, "https://$address:$port")
    }

    fun matchesServiceType(type: String): Boolean = type.trimEnd('.') == SERVICE_TYPE

    private fun attribute(attributes: Map<String, ByteArray>, name: String): String? {
        val value =
            attributes.entries.firstOrNull { it.key.equals(name, ignoreCase = true) }?.value
                ?: return null
        if (value.size > 64) return null
        return value.toString(Charsets.UTF_8)
    }

    private fun isPrivateV4(value: String): Boolean {
        val octets = value.split('.')
        if (octets.size != 4) return false
        val bytes = octets.map {
            val number = it.toIntOrNull() ?: return false
            if (number !in 0..255 || number.toString() != it) return false
            number
        }
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] in 16..31) ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 169 && bytes[1] == 254)
    }
}

class NsdDiscovery(context: Context) {
    private val appContext = context.applicationContext

    fun discover(): Flow<DiscoveredPc> = callbackFlow {
        val manager = appContext.getSystemService(NsdManager::class.java)
        val wifi = appContext.getSystemService(WifiManager::class.java)
        val multicastLock = wifi.createMulticastLock("phone-transfer-nsd").apply {
            setReferenceCounted(false)
            acquire()
        }
        val found = Channel<NsdServiceInfo>(Channel.BUFFERED)
        val resolver = launch(Dispatchers.IO) {
            for (service in found) {
                val resolved = resolve(manager, service) ?: continue
                val candidate = DiscoveryRecordRules.parse(
                    resolved.attributes,
                    resolved.host?.hostAddress,
                    resolved.port
                ) ?: continue
                // Do not deduplicate before authentication. A transient TLS/network failure must be retryable
                // when Android reports the same service again.
                trySend(candidate)
            }
        }
        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) = Unit

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (DiscoveryRecordRules.matchesServiceType(serviceInfo.serviceType)) {
                    found.trySend(serviceInfo)
                }
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) = Unit

            override fun onDiscoveryStopped(serviceType: String) = Unit

            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                close(IllegalStateException("NSD_START_$errorCode"))
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
        }
        try {
            manager.discoverServices(
                DiscoveryRecordRules.SERVICE_TYPE,
                NsdManager.PROTOCOL_DNS_SD,
                listener
            )
        } catch (error: Exception) {
            found.close()
            resolver.cancel()
            if (multicastLock.isHeld) multicastLock.release()
            close(error)
            return@callbackFlow
        }
        awaitClose {
            found.close()
            resolver.cancel()
            try {
                manager.stopServiceDiscovery(listener)
            } catch (_: IllegalArgumentException) {
                // Discovery may already have failed or stopped.
            }
            if (multicastLock.isHeld) multicastLock.release()
        }
    }

    @SuppressLint("Deprecation")
    @Suppress("DEPRECATION")
    private suspend fun resolve(manager: NsdManager, service: NsdServiceInfo): NsdServiceInfo? =
        suspendCancellableCoroutine { continuation ->
            val listener = object : NsdManager.ResolveListener {
                override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                    if (continuation.isActive) continuation.resume(null)
                }

                override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                    if (continuation.isActive) continuation.resume(serviceInfo)
                }
            }
            try {
                manager.resolveService(service, listener)
            } catch (error: Exception) {
                if (error is CancellationException) throw error
                if (continuation.isActive) continuation.resume(null)
            }
        }
}
