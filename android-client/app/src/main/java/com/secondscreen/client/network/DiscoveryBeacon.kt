package com.secondscreen.client.network

import com.secondscreen.client.protocol.ProtocolConstants

data class DiscoveredHost(
    val hostname: String,
    val port: Int,
    val ipAddress: String,
    val lastSeenMs: Long = System.currentTimeMillis()
)

object DiscoveryBeaconParser {

    private val magicRegex = Regex("\"magic\"\\s*:\\s*\"([^\"]+)\"")
    private val hostnameRegex = Regex("\"hostname\"\\s*:\\s*\"([^\"]+)\"")
    private val portRegex = Regex("\"port\"\\s*:\\s*([0-9]+)")
    private val addressesRegex = Regex("\"addresses\"\\s*:\\s*\\[([^\\]]*)\\]")
    private val ipRegex = Regex("\"([^\"]+)\"")

    fun parse(data: ByteArray, senderIp: String): DiscoveredHost? {
        if (data.isEmpty()) return null
        val text = String(data, Charsets.UTF_8).trim()
        return parse(text, senderIp)
    }

    fun parse(text: String, senderIp: String): DiscoveredHost? {
        if (text.isEmpty()) return null

        // 1. Try JSON format
        if (text.startsWith("{") && text.endsWith("}")) {
            val magic = magicRegex.find(text)?.groupValues?.getOrNull(1)
            if (magic == "SECOND_SCREEN_DISCOVERY") {
                val hostname = hostnameRegex.find(text)?.groupValues?.getOrNull(1) ?: "PC"
                val port = portRegex.find(text)?.groupValues?.getOrNull(1)?.toIntOrNull() ?: ProtocolConstants.DEFAULT_PORT

                val targetIp = if (senderIp.isNotEmpty() && senderIp != "0.0.0.0") {
                    senderIp
                } else {
                    val addressesBlock = addressesRegex.find(text)?.groupValues?.getOrNull(1)
                    if (addressesBlock != null) {
                        ipRegex.find(addressesBlock)?.groupValues?.getOrNull(1) ?: "127.0.0.1"
                    } else {
                        "127.0.0.1"
                    }
                }
                return DiscoveredHost(
                    hostname = hostname,
                    port = port,
                    ipAddress = targetIp
                )
            }
        }

        // 2. Try text format: SECOND_SCREEN_HOST:<port>:<hostname>
        if (text.startsWith("SECOND_SCREEN_HOST:", ignoreCase = true) ||
            text.startsWith("SECOND_SCREEN_DISCOVERY:", ignoreCase = true)
        ) {
            val parts = text.split(":")
            if (parts.size >= 3) {
                val port = parts[1].toIntOrNull() ?: ProtocolConstants.DEFAULT_PORT
                val hostname = parts[2].trim()
                return DiscoveredHost(
                    hostname = hostname,
                    port = port,
                    ipAddress = senderIp
                )
            }
        }

        return null
    }
}
