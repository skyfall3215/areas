package com.secondscreen.client.protocol

object ProtocolConstants {
    const val MAGIC_0: Byte = 0x53 // 'S'
    const val MAGIC_1: Byte = 0x53 // 'S'
    const val VERSION_1: Byte = 0x01
    const val HEADER_SIZE = 16
    const val DEFAULT_PORT = 27183
    const val DISCOVERY_PORT = 27184
    const val MAX_PAYLOAD_LENGTH = 4L * 1024 * 1024 // 4 MB

    const val TRANSPORT_MODE_TCP: Byte = 0x00

    const val VIDEO_FLAG_KEYFRAME: Byte = 0x01
    const val VIDEO_FLAG_CURSOR_EMBEDDED: Byte = 0x02

    const val FEATURE_FLAG_TOUCH: Byte = 0x01
    const val FEATURE_FLAG_LOW_LATENCY_DECODER: Byte = 0x02

    const val CODEC_ID_H264: Byte = 0x01

    const val HELLO_STATUS_OK: Byte = 0x00
    const val HELLO_STATUS_VERSION_MISMATCH: Byte = 0x01
    const val HELLO_STATUS_ENCODER_INIT_FAILED: Byte = 0x02
}

enum class MessageType(val code: Byte) {
    HELLO(0x01),
    HELLO_ACK(0x02),
    VIDEO_CONFIG(0x10),
    VIDEO_FRAME(0x11),
    KEYFRAME_REQ(0x12),
    TOUCH_EVENT(0x20),
    KEYBOARD_EVENT(0x21),
    PING(0x30),
    PONG(0x31),
    ERROR(0xF0.toByte()),
    DISCONNECT(0xFF.toByte());

    companion object {
        fun fromCode(code: Byte): MessageType? {
            return entries.firstOrNull { it.code == code }
        }
    }
}

