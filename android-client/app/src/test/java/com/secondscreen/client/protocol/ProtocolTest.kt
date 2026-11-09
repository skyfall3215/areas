package com.secondscreen.client.protocol

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.io.InputStream
import java.nio.ByteBuffer
import java.util.Random

class ProtocolTest {

    private fun findTestVectorsDir(): File {
        var dir: File? = File(System.getProperty("user.dir") ?: ".")
        while (dir != null) {
            val candidate = File(dir, "protocol/test-vectors")
            if (candidate.exists() && candidate.isDirectory) {
                return candidate
            }
            dir = dir.parentFile
        }
        return File("d:/Areas/protocol/test-vectors")
    }

    @Test
    fun frameHeader_roundTrip_serializesAndDeserializesCorrectly() {
        val header = FrameHeader.create(
            msgType = MessageType.VIDEO_FRAME,
            payloadLength = 1024L,
            sequence = 42,
            timestampUs = 987654L,
            flags = ProtocolConstants.VIDEO_FLAG_KEYFRAME
        )

        val buffer = ByteBuffer.allocate(ProtocolConstants.HEADER_SIZE)
        header.writeTo(buffer)
        buffer.flip()

        val parsed = FrameHeader.readFrom(buffer)
        assertEquals(ProtocolConstants.MAGIC_0, parsed.magic0)
        assertEquals(ProtocolConstants.MAGIC_1, parsed.magic1)
        assertEquals(ProtocolConstants.VERSION_1, parsed.version)
        assertEquals(MessageType.VIDEO_FRAME, parsed.msgType)
        assertEquals(ProtocolConstants.VIDEO_FLAG_KEYFRAME, parsed.flags)
        assertEquals(0.toByte(), parsed.reserved)
        assertEquals(42, parsed.sequence)
        assertEquals(987654L, parsed.timestampUs)
        assertEquals(1024L, parsed.payloadLength)
        assertNull(parsed.validate())
    }

    @Test
    fun helloMessage_roundTrip_matchesValues() {
        val original = HelloMessage(
            nativeWidth = 2560,
            nativeHeight = 1600,
            maxFps = 120,
            preferredBitrateKbps = 20000,
            featureFlags = (ProtocolConstants.FEATURE_FLAG_TOUCH.toInt() or ProtocolConstants.FEATURE_FLAG_LOW_LATENCY_DECODER.toInt()).toByte(),
            deviceName = "Galaxy Tab S9+"
        )

        val bytes = original.toByteArray()
        val parsed = HelloMessage.readPayloadFrom(ByteBuffer.wrap(bytes))

        assertEquals(original.nativeWidth, parsed.nativeWidth)
        assertEquals(original.nativeHeight, parsed.nativeHeight)
        assertEquals(original.maxFps, parsed.maxFps)
        assertEquals(original.preferredBitrateKbps, parsed.preferredBitrateKbps)
        assertEquals(original.featureFlags, parsed.featureFlags)
        assertEquals(original.deviceName, parsed.deviceName)
    }

    @Test
    fun helloAckMessage_roundTrip_matchesValues() {
        val original = HelloAckMessage(
            status = ProtocolConstants.HELLO_STATUS_OK,
            codecId = ProtocolConstants.CODEC_ID_H264,
            streamWidth = 1920,
            streamHeight = 1080,
            targetFps = 60,
            bitrateBps = 15000000L,
            sessionId = 0xDEADBEEFL
        )

        val bytes = original.toByteArray()
        val parsed = HelloAckMessage.readPayloadFrom(ByteBuffer.wrap(bytes))

        assertEquals(original.status, parsed.status)
        assertEquals(original.codecId, parsed.codecId)
        assertEquals(original.streamWidth, parsed.streamWidth)
        assertEquals(original.streamHeight, parsed.streamHeight)
        assertEquals(original.targetFps, parsed.targetFps)
        assertEquals(original.bitrateBps, parsed.bitrateBps)
        assertEquals(original.sessionId, parsed.sessionId)
    }

    @Test
    fun testVector_helloSample_validatesAndParses() {
        val file = File(findTestVectorsDir(), "hello_sample.bin")
        assertTrue("Test vector file not found at: ${file.absolutePath}", file.exists())

        val bytes = file.readBytes()
        assertEquals(48, bytes.size)

        val header = FrameHeader.readFrom(ByteBuffer.wrap(bytes, 0, ProtocolConstants.HEADER_SIZE))
        assertNull(header.validate())
        assertEquals(MessageType.HELLO, header.msgType)
        assertEquals(32L, header.payloadLength)
        assertEquals(1, header.sequence)
        assertEquals(1000000L, header.timestampUs)

        val hello = HelloMessage.readPayloadFrom(ByteBuffer.wrap(bytes, ProtocolConstants.HEADER_SIZE, 32))
        assertEquals(2560, hello.nativeWidth)
        assertEquals(1600, hello.nativeHeight)
        assertEquals(60, hello.maxFps)
        assertEquals(15000, hello.preferredBitrateKbps)
        assertEquals(0x03.toByte(), hello.featureFlags)
        assertEquals("Galaxy Tab S9", hello.deviceName)
    }

    @Test
    fun testVector_helloAckSample_validatesAndParses() {
        val file = File(findTestVectorsDir(), "hello_ack_sample.bin")
        assertTrue(file.exists())

        val bytes = file.readBytes()
        assertEquals(32, bytes.size)

        val header = FrameHeader.readFrom(ByteBuffer.wrap(bytes, 0, ProtocolConstants.HEADER_SIZE))
        assertNull(header.validate())
        assertEquals(MessageType.HELLO_ACK, header.msgType)
        assertEquals(16L, header.payloadLength)

        val ack = HelloAckMessage.readPayloadFrom(ByteBuffer.wrap(bytes, ProtocolConstants.HEADER_SIZE, 16))
        assertEquals(ProtocolConstants.HELLO_STATUS_OK, ack.status)
        assertEquals(ProtocolConstants.CODEC_ID_H264, ack.codecId)
        assertEquals(1920, ack.streamWidth)
        assertEquals(1200, ack.streamHeight)
        assertEquals(60, ack.targetFps)
        assertEquals(15000000L, ack.bitrateBps)
        assertEquals(0x12345678L, ack.sessionId)
    }

    @Test
    fun testVector_videoConfigSample_validatesAndParses() {
        val file = File(findTestVectorsDir(), "video_config_sample.bin")
        assertTrue(file.exists())

        val bytes = file.readBytes()
        assertEquals(37, bytes.size)

        val header = FrameHeader.readFrom(ByteBuffer.wrap(bytes, 0, ProtocolConstants.HEADER_SIZE))
        assertNull(header.validate())
        assertEquals(MessageType.VIDEO_CONFIG, header.msgType)
        assertEquals(21L, header.payloadLength)

        val payload = bytes.copyOfRange(ProtocolConstants.HEADER_SIZE, bytes.size)
        val config = VideoConfigMessage.readPayloadFrom(payload)
        assertEquals(21, config.spsPpsData.size)
        assertEquals(0x00.toByte(), config.spsPpsData[0])
        assertEquals(0x00.toByte(), config.spsPpsData[1])
        assertEquals(0x00.toByte(), config.spsPpsData[2])
        assertEquals(0x01.toByte(), config.spsPpsData[3])
        assertEquals(0x67.toByte(), config.spsPpsData[4]) // SPS NAL
    }

    @Test
    fun testVector_videoFrameSample_validatesAndParses() {
        val file = File(findTestVectorsDir(), "video_frame_sample.bin")
        assertTrue(file.exists())

        val bytes = file.readBytes()
        assertEquals(34, bytes.size)

        val header = FrameHeader.readFrom(ByteBuffer.wrap(bytes, 0, ProtocolConstants.HEADER_SIZE))
        assertNull(header.validate())
        assertEquals(MessageType.VIDEO_FRAME, header.msgType)
        assertEquals(ProtocolConstants.VIDEO_FLAG_KEYFRAME, header.flags)
        assertEquals(18L, header.payloadLength)

        val payload = bytes.copyOfRange(ProtocolConstants.HEADER_SIZE, bytes.size)
        val frame = VideoFrameMessage.readPayloadFrom(payload, header.flags)
        assertEquals(1L, frame.frameNumber)
        assertEquals(2500, frame.captureDurationUs)
        assertEquals(4200, frame.encodeDurationUs)
        assertTrue(frame.isKeyframe)
        assertEquals(10, frame.nalData.size)
    }

    @Test
    fun streamFragmentation_50KbChunked1Kb_reassemblesCorrectly() {
        val dummyNal = ByteArray(50 * 1024)
        Random(42).nextBytes(dummyNal)

        val frame = VideoFrameMessage(
            frameNumber = 777L,
            captureDurationUs = 1200,
            encodeDurationUs = 2300,
            nalData = dummyNal,
            isKeyframe = true
        )
        val payload = frame.toPayloadByteArray()
        val header = FrameHeader.create(
            msgType = MessageType.VIDEO_FRAME,
            payloadLength = payload.size.toLong(),
            sequence = 100,
            timestampUs = 555555L,
            flags = ProtocolConstants.VIDEO_FLAG_KEYFRAME
        )

        val fullPacket = PacketSerializer.serializePacket(header, payload)

        // Chunked InputStream simulator
        val chunkedStream = object : InputStream() {
            var pos = 0
            val chunkSize = 1024

            override fun read(): Int {
                if (pos >= fullPacket.size) return -1
                return fullPacket[pos++].toInt() and 0xFF
            }

            override fun read(b: ByteArray, off: Int, len: Int): Int {
                if (pos >= fullPacket.size) return -1
                val toRead = minOf(minOf(len, chunkSize), fullPacket.size - pos)
                System.arraycopy(fullPacket, pos, b, off, toRead)
                pos += toRead
                return toRead
            }
        }

        val (parsedHeader, parsedPayload) = PacketParser.readPacket(chunkedStream)
        assertEquals(MessageType.VIDEO_FRAME, parsedHeader.msgType)
        assertEquals(payload.size.toLong(), parsedHeader.payloadLength)
        assertEquals(100, parsedHeader.sequence)
        assertEquals(555555L, parsedHeader.timestampUs)

        val parsedFrame = VideoFrameMessage.readPayloadFrom(parsedPayload, parsedHeader.flags)
        assertEquals(777L, parsedFrame.frameNumber)
        assertEquals(1200, parsedFrame.captureDurationUs)
        assertEquals(2300, parsedFrame.encodeDurationUs)
        assertTrue(parsedFrame.isKeyframe)
        assertArrayEquals(dummyNal, parsedFrame.nalData)
    }

    @Test
    fun pingMessage_and_pongMessage_roundTrip() {
        val ping = PingMessage(pingId = 123L, originTimeUs = 987654321L)
        val pingBytes = ping.toByteArray()
        val parsedPing = PingMessage.readPayloadFrom(ByteBuffer.wrap(pingBytes))
        assertEquals(ping.pingId, parsedPing.pingId)
        assertEquals(ping.originTimeUs, parsedPing.originTimeUs)

        val pong = PongMessage(pingId = 123L, originTimeUs = 987654321L)
        val pongBytes = pong.toByteArray()
        val parsedPong = PongMessage.readPayloadFrom(ByteBuffer.wrap(pongBytes))
        assertEquals(pong.pingId, parsedPong.pingId)
        assertEquals(pong.originTimeUs, parsedPong.originTimeUs)
    }

    @Test
    fun telemetryTracker_snapshotCalculations() {
        val tracker = com.secondscreen.client.telemetry.TelemetryTracker()
        val ping = tracker.createPing()
        val pong = tracker.createPong(ping)

        tracker.onPongReceived(pong)
        tracker.onFrameDecoded(
            captureDurationUs = 1500,
            encodeDurationUs = 3200,
            decodeDurationNs = 4_500_000L,
            payloadBytes = 25000
        )

        val snapshot = tracker.getSnapshot()
        assertEquals(1.5, snapshot.captureMs, 0.01)
        assertEquals(3.2, snapshot.encodeMs, 0.01)
        assertEquals(4.5, snapshot.decodeMs, 0.01)
        assertTrue(snapshot.estimatedPipelineMs > 9.0)
    }

    @Test
    fun touchEventMessage_multiPointer_roundTrip() {
        val pointers = listOf(
            PointerEntry(pointerId = 0, action = TouchAction.DOWN, normalizedX = 1000, normalizedY = 2000, contactRadius = 25),
            PointerEntry(pointerId = 1, action = TouchAction.MOVE, normalizedX = 65535, normalizedY = 32768, contactRadius = 40)
        )
        val msg = TouchEventMessage(pointerCount = 2, changedIndex = 1, pointers = pointers)
        val bytes = msg.toByteArray()
        assertEquals(18, bytes.size) // 2 + 2 * 8

        val parsed = TouchEventMessage.readPayloadFrom(ByteBuffer.wrap(bytes))
        assertEquals(2, parsed.pointerCount)
        assertEquals(1, parsed.changedIndex)
        assertEquals(2, parsed.pointers.size)

        assertEquals(0, parsed.pointers[0].pointerId)
        assertEquals(TouchAction.DOWN, parsed.pointers[0].action)
        assertEquals(1000, parsed.pointers[0].normalizedX)
        assertEquals(2000, parsed.pointers[0].normalizedY)
        assertEquals(25, parsed.pointers[0].contactRadius)

        assertEquals(1, parsed.pointers[1].pointerId)
        assertEquals(TouchAction.MOVE, parsed.pointers[1].action)
        assertEquals(65535, parsed.pointers[1].normalizedX)
        assertEquals(32768, parsed.pointers[1].normalizedY)
        assertEquals(40, parsed.pointers[1].contactRadius)
    }
}

