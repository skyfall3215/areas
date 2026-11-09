package com.secondscreen.client.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

data class FrameHeader(
    val magic0: Byte,
    val magic1: Byte,
    val version: Byte,
    val msgType: MessageType,
    val flags: Byte,
    val reserved: Byte,
    val sequence: Int, // uint16 stored as Int (0..65535)
    val timestampUs: Long, // uint32 stored as Long
    val payloadLength: Long // uint32 stored as Long
) {
    companion object {
        fun create(
            msgType: MessageType,
            payloadLength: Long,
            sequence: Int = 0,
            timestampUs: Long = 0L,
            flags: Byte = 0
        ): FrameHeader {
            return FrameHeader(
                magic0 = ProtocolConstants.MAGIC_0,
                magic1 = ProtocolConstants.MAGIC_1,
                version = ProtocolConstants.VERSION_1,
                msgType = msgType,
                flags = flags,
                reserved = 0,
                sequence = sequence and 0xFFFF,
                timestampUs = timestampUs and 0xFFFFFFFFL,
                payloadLength = payloadLength and 0xFFFFFFFFL
            )
        }

        fun readFrom(buffer: ByteBuffer): FrameHeader {
            buffer.order(ByteOrder.BIG_ENDIAN)
            val m0 = buffer.get()
            val m1 = buffer.get()
            val ver = buffer.get()
            val typeCode = buffer.get()
            val msgType = MessageType.fromCode(typeCode)
                ?: throw IllegalArgumentException("Unknown MsgType code: 0x%02X".format(typeCode))
            val flags = buffer.get()
            val reserved = buffer.get()
            val seq = buffer.short.toInt() and 0xFFFF
            val ts = buffer.int.toLong() and 0xFFFFFFFFL
            val len = buffer.int.toLong() and 0xFFFFFFFFL

            return FrameHeader(m0, m1, ver, msgType, flags, reserved, seq, ts, len)
        }
    }

    fun writeTo(buffer: ByteBuffer) {
        buffer.order(ByteOrder.BIG_ENDIAN)
        buffer.put(magic0)
        buffer.put(magic1)
        buffer.put(version)
        buffer.put(msgType.code)
        buffer.put(flags)
        buffer.put(reserved)
        buffer.putShort((sequence and 0xFFFF).toShort())
        buffer.putInt((timestampUs and 0xFFFFFFFFL).toInt())
        buffer.putInt((payloadLength and 0xFFFFFFFFL).toInt())
    }

    fun validate(): String? {
        if (magic0 != ProtocolConstants.MAGIC_0 || magic1 != ProtocolConstants.MAGIC_1) {
            return "Invalid magic bytes: 0x%02X 0x%02X, expected 'SS'".format(magic0, magic1)
        }
        if (version != ProtocolConstants.VERSION_1) {
            return "Unsupported protocol version: $version, expected ${ProtocolConstants.VERSION_1}"
        }
        if (payloadLength > ProtocolConstants.MAX_PAYLOAD_LENGTH) {
            return "Payload length exceeds maximum limit: $payloadLength > ${ProtocolConstants.MAX_PAYLOAD_LENGTH}"
        }
        return null
    }
}

