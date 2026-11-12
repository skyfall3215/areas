package com.secondscreen.client.transport

import com.secondscreen.client.protocol.FrameHeader
import com.secondscreen.client.protocol.PacketParser
import com.secondscreen.client.protocol.PacketSerializer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.SecureRandom
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

class ClientIsolationException(
    message: String = "Ağ cihazlar arası iletişimi engelliyor olabilir (Yurt/Okul Wi-Fi). Lütfen USB veya Mobil Etkin Nokta modunu seçin."
) : Exception(message)

class SocketTransport(
    val host: String = "127.0.0.1",
    val port: Int = 27183,
    private val connectTimeoutMs: Int = 2500
) : ITransport {

    private var socket: Socket? = null
    private var inputStream: BufferedInputStream? = null
    private var outputStream: BufferedOutputStream? = null
    private val sendMutex = Mutex()

    override val isConnected: Boolean
        get() = socket?.isConnected == true && socket?.isClosed == false

    suspend fun connect() = withContext(Dispatchers.IO) {
        val rawSocket = Socket()
        rawSocket.tcpNoDelay = true
        rawSocket.sendBufferSize = 512 * 1024
        rawSocket.receiveBufferSize = 512 * 1024

        try {
            rawSocket.connect(InetSocketAddress(host, port), connectTimeoutMs)
        } catch (e: SocketTimeoutException) {
            rawSocket.close()
            if (isLanHost(host)) {
                throw ClientIsolationException()
            }
            throw e
        } catch (e: Exception) {
            rawSocket.close()
            throw e
        }

        // Upgrade connection to TLS 1.2/1.3 (ephemeral LAN cert; session authenticated via 6-digit PIN)
        val tlsSocket: SSLSocket = try {
            val trustAll = arrayOf<TrustManager>(object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
                override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            })
            val sslCtx = SSLContext.getInstance("TLS")
            sslCtx.init(null, trustAll, SecureRandom())
            val ssl = sslCtx.socketFactory.createSocket(rawSocket, host, port, true) as SSLSocket
            ssl.useClientMode = true
            ssl.tcpNoDelay = true
            ssl.startHandshake()
            ssl
        } catch (e: Exception) {
            try { rawSocket.close() } catch (_: Exception) {}
            throw e
        }

        socket = tlsSocket
        inputStream = BufferedInputStream(tlsSocket.getInputStream(), 512 * 1024)
        outputStream = BufferedOutputStream(tlsSocket.getOutputStream(), 512 * 1024)
    }

    override suspend fun sendPacket(header: FrameHeader, payload: ByteArray) = withContext(Dispatchers.IO) {
        val os = outputStream ?: throw IllegalStateException("Transport not connected")
        sendMutex.withLock {
            PacketSerializer.writePacket(os, header, payload)
        }
    }

    override suspend fun receivePacket(): Pair<FrameHeader, ByteArray> = withContext(Dispatchers.IO) {
        val isStream = inputStream ?: throw IllegalStateException("Transport not connected")
        PacketParser.readPacket(isStream)
    }

    override fun close() {
        try { inputStream?.close() } catch (_: Exception) {}
        try { outputStream?.close() } catch (_: Exception) {}
        try { socket?.close() } catch (_: Exception) {}
        socket = null
        inputStream = null
        outputStream = null
    }

    private fun isLanHost(h: String): Boolean {
        return h != "127.0.0.1" && h != "localhost" && h != "::1"
    }
}
