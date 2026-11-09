package com.secondscreen.client.transport

import com.secondscreen.client.protocol.FrameHeader
import com.secondscreen.client.protocol.PacketParser
import com.secondscreen.client.protocol.PacketSerializer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.net.InetSocketAddress
import java.net.Socket

class SocketTransport(
    private val host: String = "127.0.0.1",
    private val port: Int = 27183,
    private val connectTimeoutMs: Int = 3000
) : ITransport {

    private var socket: Socket? = null
    private var inputStream: BufferedInputStream? = null
    private var outputStream: BufferedOutputStream? = null

    override val isConnected: Boolean
        get() = socket?.isConnected == true && socket?.isClosed == false

    suspend fun connect() = withContext(Dispatchers.IO) {
        val s = Socket()
        s.tcpNoDelay = true
        s.sendBufferSize = 64 * 1024
        s.receiveBufferSize = 64 * 1024
        s.connect(InetSocketAddress(host, port), connectTimeoutMs)
        socket = s
        inputStream = BufferedInputStream(s.getInputStream(), 64 * 1024)
        outputStream = BufferedOutputStream(s.getOutputStream(), 64 * 1024)
    }

    override suspend fun sendPacket(header: FrameHeader, payload: ByteArray) = withContext(Dispatchers.IO) {
        val os = outputStream ?: throw IllegalStateException("Transport not connected")
        PacketSerializer.writePacket(os, header, payload)
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
}

