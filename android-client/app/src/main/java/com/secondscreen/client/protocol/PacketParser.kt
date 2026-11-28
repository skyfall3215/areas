package com.secondscreen.client.protocol

import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer

object PacketParser {

    private val EMPTY_PAYLOAD = ByteArray(0)

    fun readPacket(inputStream: InputStream): Pair<FrameHeader, ByteArray> {
        return readPacket(inputStream, ByteArray(ProtocolConstants.HEADER_SIZE))
    }

    fun readPacket(inputStream: InputStream, headerScratch: ByteArray): Pair<FrameHeader, ByteArray> {
        require(headerScratch.size >= ProtocolConstants.HEADER_SIZE) {
            "headerScratch must be at least ${ProtocolConstants.HEADER_SIZE} bytes"
        }

        readExactBytes(inputStream, headerScratch, 0, ProtocolConstants.HEADER_SIZE)

        val headerBuf = ByteBuffer.wrap(headerScratch, 0, ProtocolConstants.HEADER_SIZE)
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
            EMPTY_PAYLOAD
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

    fun writePacket(
        outputStream: OutputStream,
        header: FrameHeader,
        payload: ByteArray,
        scratch: ByteArray
    ) {
        val totalSize = ProtocolConstants.HEADER_SIZE + payload.size
        if (totalSize <= scratch.size) {
            val buffer = ByteBuffer.wrap(scratch, 0, totalSize)
            header.writeTo(buffer)
            if (payload.isNotEmpty()) {
                System.arraycopy(payload, 0, scratch, ProtocolConstants.HEADER_SIZE, payload.size)
            }
            outputStream.write(scratch, 0, totalSize)
            outputStream.flush()
        } else {
            writePacket(outputStream, header, payload)
        }
    }
}

