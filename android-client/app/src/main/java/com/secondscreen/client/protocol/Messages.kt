package com.secondscreen.client.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets

data class HelloMessage(
    val nativeWidth: Int,
    val nativeHeight: Int,
    val maxFps: Int,
    val preferredBitrateKbps: Int,
    val featureFlags: Byte,
    val deviceName: String
) {
    companion object {
        const val PAYLOAD_SIZE = 32

        fun readPayloadFrom(buffer: ByteBuffer): HelloMessage {
            buffer.order(ByteOrder.BIG_ENDIAN)
            val width = buffer.short.toInt() and 0xFFFF
            val height = buffer.short.toInt() and 0xFFFF
            val fps = buffer.short.toInt() and 0xFFFF
            val bitrate = buffer.short.toInt() and 0xFFFF
            val features = buffer.get()
            // Skip 7 reserved bytes
            for (i in 0 until 7) buffer.get()

            val nameBytes = ByteArray(16)
            buffer.get(nameBytes)
            var nullIdx = nameBytes.indexOf(0.toByte())
            if (nullIdx < 0) nullIdx = 16
            val deviceName = String(nameBytes, 0, nullIdx, StandardCharsets.UTF_8)

            return HelloMessage(width, height, fps, bitrate, features, deviceName)
        }
    }

    fun writePayloadTo(buffer: ByteBuffer) {
        buffer.order(ByteOrder.BIG_ENDIAN)
        buffer.putShort((nativeWidth and 0xFFFF).toShort())
        buffer.putShort((nativeHeight and 0xFFFF).toShort())
        buffer.putShort((maxFps and 0xFFFF).toShort())
        buffer.putShort((preferredBitrateKbps and 0xFFFF).toShort())
        buffer.put(featureFlags)
        // 7 reserved bytes
        for (i in 0 until 7) buffer.put(0.toByte())

        val nameBytes = deviceName.toByteArray(StandardCharsets.UTF_8)
        val copyLen = minOf(nameBytes.size, 16)
        buffer.put(nameBytes, 0, copyLen)
        for (i in copyLen until 16) buffer.put(0.toByte())
    }

    fun toByteArray(): ByteArray {
        val bytes = ByteArray(PAYLOAD_SIZE)
        val buf = ByteBuffer.wrap(bytes)
        writePayloadTo(buf)
        return bytes
    }
}

data class HelloAckMessage(
    val status: Byte,
    val codecId: Byte,
    val streamWidth: Int,
    val streamHeight: Int,
    val targetFps: Int,
    val bitrateBps: Long,
    val sessionId: Long
) {
    companion object {
        const val PAYLOAD_SIZE = 16

        fun readPayloadFrom(buffer: ByteBuffer): HelloAckMessage {
            buffer.order(ByteOrder.BIG_ENDIAN)
            val status = buffer.get()
            val codecId = buffer.get()
            val width = buffer.short.toInt() and 0xFFFF
            val height = buffer.short.toInt() and 0xFFFF
            val fps = buffer.short.toInt() and 0xFFFF
            val bitrate = buffer.int.toLong() and 0xFFFFFFFFL
            val sessionId = buffer.int.toLong() and 0xFFFFFFFFL

            return HelloAckMessage(status, codecId, width, height, fps, bitrate, sessionId)
        }
    }

    fun writePayloadTo(buffer: ByteBuffer) {
        buffer.order(ByteOrder.BIG_ENDIAN)
        buffer.put(status)
        buffer.put(codecId)
        buffer.putShort((streamWidth and 0xFFFF).toShort())
        buffer.putShort((streamHeight and 0xFFFF).toShort())
        buffer.putShort((targetFps and 0xFFFF).toShort())
        buffer.putInt((bitrateBps and 0xFFFFFFFFL).toInt())
        buffer.putInt((sessionId and 0xFFFFFFFFL).toInt())
    }

    fun toByteArray(): ByteArray {
        val bytes = ByteArray(PAYLOAD_SIZE)
        val buf = ByteBuffer.wrap(bytes)
        writePayloadTo(buf)
        return bytes
    }
}

data class VideoConfigMessage(val spsPpsData: ByteArray) {
    companion object {
        fun readPayloadFrom(bytes: ByteArray): VideoConfigMessage {
            return VideoConfigMessage(bytes)
        }
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (javaClass != other?.javaClass) return false
        other as VideoConfigMessage
        return spsPpsData.contentEquals(other.spsPpsData)
    }

    override fun hashCode(): Int = spsPpsData.contentHashCode()
}

data class VideoFrameMessage(
    val frameNumber: Long,
    val captureDurationUs: Int,
    val encodeDurationUs: Int,
    val nalData: ByteArray,
    val isKeyframe: Boolean = false,
    val isCursorEmbedded: Boolean = false
) {
    companion object {
        const val HEADER_OVERHEAD = 8

        fun readPayloadFrom(bytes: ByteArray, flags: Byte): VideoFrameMessage {
            val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
            val frameNumber = buffer.int.toLong() and 0xFFFFFFFFL
            val captureDur = buffer.short.toInt() and 0xFFFF
            val encodeDur = buffer.short.toInt() and 0xFFFF
            val nalData = ByteArray(bytes.size - HEADER_OVERHEAD)
            buffer.get(nalData)

            val isKey = (flags.toInt() and ProtocolConstants.VIDEO_FLAG_KEYFRAME.toInt()) != 0
            val isCursor = (flags.toInt() and ProtocolConstants.VIDEO_FLAG_CURSOR_EMBEDDED.toInt()) != 0

            return VideoFrameMessage(frameNumber, captureDur, encodeDur, nalData, isKey, isCursor)
        }
    }

    fun toPayloadByteArray(): ByteArray {
        val bytes = ByteArray(HEADER_OVERHEAD + nalData.size)
        val buf = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
        buf.putInt((frameNumber and 0xFFFFFFFFL).toInt())
        buf.putShort((captureDurationUs and 0xFFFF).toShort())
        buf.putShort((encodeDurationUs and 0xFFFF).toShort())
        buf.put(nalData)
        return bytes
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (javaClass != other?.javaClass) return false
        other as VideoFrameMessage
        if (frameNumber != other.frameNumber) return false
        if (captureDurationUs != other.captureDurationUs) return false
        if (encodeDurationUs != other.encodeDurationUs) return false
        if (!nalData.contentEquals(other.nalData)) return false
        if (isKeyframe != other.isKeyframe) return false
        if (isCursorEmbedded != other.isCursorEmbedded) return false
        return true
    }

    override fun hashCode(): Int = frameNumber.hashCode()
}

data class KeyframeReqMessage(val lastGoodFrameNumber: Long) {
    companion object {
        const val PAYLOAD_SIZE = 4

        fun readPayloadFrom(buffer: ByteBuffer): KeyframeReqMessage {
            buffer.order(ByteOrder.BIG_ENDIAN)
            val frameNum = buffer.int.toLong() and 0xFFFFFFFFL
            return KeyframeReqMessage(frameNum)
        }
    }

    fun toByteArray(): ByteArray {
        val bytes = ByteArray(PAYLOAD_SIZE)
        val buf = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
        buf.putInt((lastGoodFrameNumber and 0xFFFFFFFFL).toInt())
        return bytes
    }
}

data class PingMessage(val pingId: Long, val originTimeUs: Long) {
    companion object {
        const val PAYLOAD_SIZE = 8

        fun readPayloadFrom(buffer: ByteBuffer): PingMessage {
            buffer.order(ByteOrder.BIG_ENDIAN)
            val id = buffer.int.toLong() and 0xFFFFFFFFL
            val time = buffer.int.toLong() and 0xFFFFFFFFL
            return PingMessage(id, time)
        }
    }

    fun toByteArray(): ByteArray {
        val bytes = ByteArray(PAYLOAD_SIZE)
        val buf = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
        buf.putInt((pingId and 0xFFFFFFFFL).toInt())
        buf.putInt((originTimeUs and 0xFFFFFFFFL).toInt())
        return bytes
    }
}

data class PongMessage(
    val pingId: Long,
    val originTimeUs: Long,
    val sentBitrateKbps: Long = 0,
    val sentPacketsCount: Long = 0
) {
    companion object {
        const val PAYLOAD_SIZE = 16

        fun readPayloadFrom(buffer: ByteBuffer): PongMessage {
            buffer.order(ByteOrder.BIG_ENDIAN)
            val id = buffer.int.toLong() and 0xFFFFFFFFL
            val time = buffer.int.toLong() and 0xFFFFFFFFL
            val bitrate = if (buffer.remaining() >= 8) (buffer.int.toLong() and 0xFFFFFFFFL) else 0L
            val packets = if (buffer.remaining() >= 4) (buffer.int.toLong() and 0xFFFFFFFFL) else 0L
            return PongMessage(id, time, bitrate, packets)
        }
    }

    fun toByteArray(): ByteArray {
        val bytes = ByteArray(PAYLOAD_SIZE)
        val buf = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
        buf.putInt((pingId and 0xFFFFFFFFL).toInt())
        buf.putInt((originTimeUs and 0xFFFFFFFFL).toInt())
        buf.putInt((sentBitrateKbps and 0xFFFFFFFFL).toInt())
        buf.putInt((sentPacketsCount and 0xFFFFFFFFL).toInt())
        return bytes
    }
}

object TouchAction {
    const val DOWN: Byte = 0x01
    const val MOVE: Byte = 0x02
    const val UP: Byte = 0x03
    const val CANCEL: Byte = 0x04
    const val SCROLL: Byte = 0x05     // Wheel scroll (NormalizedY contains scroll delta)
    const val RIGHT_CLICK: Byte = 0x06 // Long-press or 2-finger tap right click
    const val LEFT_CLICK: Byte = 0x07  // Single tap left click
}

data class PointerEntry(
    val pointerId: Int,
    val action: Byte,
    val normalizedX: Int,
    val normalizedY: Int,
    val contactRadius: Int = 0
) {
    companion object {
        const val ENTRY_SIZE = 8

        fun readFrom(buffer: ByteBuffer): PointerEntry {
            val id = buffer.get().toInt() and 0xFF
            val act = buffer.get()
            val x = buffer.short.toInt() and 0xFFFF
            val y = buffer.short.toInt() and 0xFFFF
            val r = buffer.short.toInt() and 0xFFFF
            return PointerEntry(id, act, x, y, r)
        }
    }

    fun writeTo(buffer: ByteBuffer) {
        buffer.put(pointerId.toByte())
        buffer.put(action)
        buffer.putShort(normalizedX.toShort())
        buffer.putShort(normalizedY.toShort())
        buffer.putShort(contactRadius.toShort())
    }
}

data class TouchEventMessage(
    val pointerCount: Int,
    val changedIndex: Int,
    val pointers: List<PointerEntry>
) {
    val payloadSize: Int get() = 2 + (pointerCount * PointerEntry.ENTRY_SIZE)

    companion object {
        fun readPayloadFrom(buffer: ByteBuffer): TouchEventMessage {
            buffer.order(ByteOrder.BIG_ENDIAN)
            val count = buffer.get().toInt() and 0xFF
            val changedIdx = buffer.get().toInt() and 0xFF
            val list = ArrayList<PointerEntry>(count)
            for (i in 0 until count) {
                list.add(PointerEntry.readFrom(buffer))
            }
            return TouchEventMessage(count, changedIdx, list)
        }
    }

    fun toByteArray(): ByteArray {
        val bytes = ByteArray(payloadSize)
        val buf = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
        buf.put(pointerCount.toByte())
        buf.put(changedIndex.toByte())
        for (p in pointers) {
            p.writeTo(buf)
        }
        return bytes
    }
}

object KeyboardFlags {
    const val KEY_DOWN: Byte = 0x01
    const val IS_UNICODE: Byte = 0x02
    const val SHIFT: Byte = 0x04
    const val CTRL: Byte = 0x08
    const val ALT: Byte = 0x10
}

data class KeyboardEventMessage(
    val keyCode: Int,
    val flags: Byte
) {
    val isKeyDown: Boolean get() = (flags.toInt() and KeyboardFlags.KEY_DOWN.toInt()) != 0
    val isUnicode: Boolean get() = (flags.toInt() and KeyboardFlags.IS_UNICODE.toInt()) != 0
    val hasShift: Boolean get() = (flags.toInt() and KeyboardFlags.SHIFT.toInt()) != 0
    val hasCtrl: Boolean get() = (flags.toInt() and KeyboardFlags.CTRL.toInt()) != 0
    val hasAlt: Boolean get() = (flags.toInt() and KeyboardFlags.ALT.toInt()) != 0

    companion object {
        const val PAYLOAD_SIZE = 3

        fun readPayloadFrom(buffer: ByteBuffer): KeyboardEventMessage {
            buffer.order(ByteOrder.BIG_ENDIAN)
            val code = buffer.short.toInt() and 0xFFFF
            val flags = buffer.get()
            return KeyboardEventMessage(code, flags)
        }
    }

    fun toByteArray(): ByteArray {
        val bytes = ByteArray(PAYLOAD_SIZE)
        val buf = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
        buf.putShort((keyCode and 0xFFFF).toShort())
        buf.put(flags)
        return bytes
    }
}

