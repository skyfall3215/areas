package com.secondscreen.client.transport

import com.secondscreen.client.protocol.FrameHeader

interface ITransport : AutoCloseable {
    val isConnected: Boolean
    suspend fun sendPacket(header: FrameHeader, payload: ByteArray)
    suspend fun receivePacket(): Pair<FrameHeader, ByteArray>
    override fun close()
}

