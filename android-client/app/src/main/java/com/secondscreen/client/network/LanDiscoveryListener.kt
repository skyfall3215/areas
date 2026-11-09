package com.secondscreen.client.network

import com.secondscreen.client.protocol.ProtocolConstants
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap

class LanDiscoveryListener(
    private val port: Int = ProtocolConstants.DISCOVERY_PORT
) {
    private val _discoveredHosts = MutableStateFlow<List<DiscoveredHost>>(emptyList())
    val discoveredHosts: StateFlow<List<DiscoveredHost>> = _discoveredHosts.asStateFlow()

    private val hostsMap = ConcurrentHashMap<String, DiscoveredHost>()
    private var listenJob: Job? = null
    private var cleanupJob: Job? = null
    private var socket: DatagramSocket? = null

    fun start(scope: CoroutineScope) {
        if (listenJob != null) return

        listenJob = scope.launch(Dispatchers.IO) {
            try {
                val s = DatagramSocket(null).apply {
                    reuseAddress = true
                    broadcast = true
                    bind(InetSocketAddress(port))
                }
                socket = s

                val buffer = ByteArray(2048)
                val packet = DatagramPacket(buffer, buffer.size)

                while (isActive && !s.isClosed) {
                    try {
                        packet.length = buffer.size
                        s.receive(packet)
                        val senderIp = packet.address?.hostAddress ?: ""
                        val data = packet.data.copyOfRange(packet.offset, packet.offset + packet.length)

                        val host = DiscoveryBeaconParser.parse(data, senderIp)
                        if (host != null && host.ipAddress.isNotEmpty()) {
                            hostsMap[host.ipAddress] = host.copy(lastSeenMs = System.currentTimeMillis())
                            _discoveredHosts.value = hostsMap.values.sortedBy { it.hostname }
                        }
                    } catch (e: Exception) {
                        if (!isActive || s.isClosed) break
                    }
                }
            } catch (_: Exception) {
                // Port might be in use or network unavailable
            }
        }

        // Cleanup expired hosts (older than 4 seconds)
        cleanupJob = scope.launch(Dispatchers.Default) {
            while (isActive) {
                delay(2000)
                val now = System.currentTimeMillis()
                val removed = hostsMap.entries.removeIf { now - it.value.lastSeenMs > 4000 }
                if (removed) {
                    _discoveredHosts.value = hostsMap.values.sortedBy { it.hostname }
                }
            }
        }
    }

    fun stop() {
        listenJob?.cancel()
        listenJob = null
        cleanupJob?.cancel()
        cleanupJob = null
        try { socket?.close() } catch (_: Exception) {}
        socket = null
        hostsMap.clear()
        _discoveredHosts.value = emptyList()
    }
}

