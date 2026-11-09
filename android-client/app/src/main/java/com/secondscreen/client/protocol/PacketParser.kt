package com.secondscreen.client.protocol

import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer

object PacketParser {

    fun readPacket(inputStream: InputStream): Pair<FrameHeader, ByteArray> {
        val headerBytes = ByteArray(ProtocolConstants.HEADER_SIZE)
        readExactBytes(inputStream, headerBytes, 0, ProtocolConstants.HEADER_SIZE)

        val headerBuf = ByteBuffer.wrap(headerBytes)
        val header = FrameHeader.readFrom(headerBuf)
        val error = header.validate()
        if (error != null) {
            throw IllegalArgumentException("Protocol violation: $error")
        }

        val payload = if (header.payloadLength > 0) {
            val payloadBytes = ByteArray(header.payloadLength.toInt())
            readExactBytes(inputStream, payloadBytes, 0, payloadBytes.size)
            payloadBytes
        } else {
            ByteArray(0)
        }

        return Pair(header, payload)
    }

    fun readExactBytes(inputStream: InputStream, target: ByteArray, offset: Int, length: Int) {
        var totalRead = 0
        while (totalRead < length) {
            val read = inputStream.read(target, offset + totalRead, length - totalRead)
            if (read == -1) {
                throw EOFException("End of stream reached. Expected $length bytes, got $totalRead")
            }
            totalRead += read
        }
    }
}

object PacketSerializer {

    fun serializePacket(header: FrameHeader, payload: ByteArray): ByteArray {
        val totalSize = ProtocolConstants.HEADER_SIZE + payload.size
        val buffer = ByteBuffer.allocate(totalSize)
        header.writeTo(buffer)
        if (payload.isNotEmpty()) {
            buffer.put(payload)
        }
        return buffer.array()
    }

    fun writePacket(outputStream: OutputStream, header: FrameHeader, payload: ByteArray) {
        val packet = serializePacket(header, payload)
        outputStream.write(packet)
        outputStream.flush()
    }
}

