package com.secondscreen.client.telemetry

import com.secondscreen.client.protocol.PingMessage
import com.secondscreen.client.protocol.PongMessage
import java.util.concurrent.atomic.AtomicLong

data class TelemetrySnapshot(
    val fps: Int,
    val bitrateMbps: Double,
    val captureMs: Double,
    val encodeMs: Double,
    val networkRttDiv2Ms: Double,
    val decodeMs: Double,
    val estimatedPipelineMs: Double
)

class TelemetryTracker {

    private val startTimeNs = System.nanoTime()
    private val nextPingId = AtomicLong(1)

    private var frameCount = 0
    private var totalBytesInPeriod = 0L
    private var lastSecondTimestampNs = System.nanoTime()

    private var currentFps = 0
    private var currentBitrateMbps = 0.0

    private var lastCaptureMs = 0.0
    private var lastEncodeMs = 0.0
    private var lastDecodeMs = 0.0
    private var lastNetworkRttDiv2Ms = 0.0

    // Rolling averages for smooth HUD display
    private var avgCaptureMs = 0.0
    private var avgEncodeMs = 0.0
    private var avgDecodeMs = 0.0

    val currentTimeUs: Long
        get() = (System.nanoTime() - startTimeNs) / 1000L

    @Synchronized
    fun onFrameDecoded(captureDurationUs: Int, encodeDurationUs: Int, decodeDurationNs: Long, payloadBytes: Int) {
        frameCount++
        totalBytesInPeriod += payloadBytes

        val capMs = captureDurationUs / 1000.0
        val encMs = encodeDurationUs / 1000.0
        val decMs = decodeDurationNs / 1_000_000.0

        lastCaptureMs = capMs
        lastEncodeMs = encMs
        lastDecodeMs = decMs

        // Exponential moving average (alpha = 0.1)
        avgCaptureMs = if (avgCaptureMs == 0.0) capMs else (avgCaptureMs * 0.9 + capMs * 0.1)
        avgEncodeMs = if (avgEncodeMs == 0.0) encMs else (avgEncodeMs * 0.9 + encMs * 0.1)
        avgDecodeMs = if (avgDecodeMs == 0.0) decMs else (avgDecodeMs * 0.9 + decMs * 0.1)

        val now = System.nanoTime()
        val elapsedNs = now - lastSecondTimestampNs
        if (elapsedNs >= 1_000_000_000L) { // 1 second elapsed
            val elapsedSec = elapsedNs / 1_000_000_000.0
            currentFps = (frameCount / elapsedSec).toInt()
            currentBitrateMbps = (totalBytesInPeriod * 8.0) / (elapsedSec * 1_000_000.0)

            frameCount = 0
            totalBytesInPeriod = 0L
            lastSecondTimestampNs = now
        }
    }

    fun createPing(): PingMessage {
        val id = nextPingId.getAndIncrement()
        return PingMessage(id, currentTimeUs)
    }

    fun createPong(ping: PingMessage): PongMessage {
        return PongMessage(ping.pingId, ping.originTimeUs)
    }

    @Synchronized
    fun onPongReceived(pong: PongMessage) {
        val nowUs = currentTimeUs
        val diffUs = nowUs - pong.originTimeUs
        if (diffUs >= 0) {
            val rttMs = diffUs / 1000.0
            val oneWay = rttMs / 2.0
            lastNetworkRttDiv2Ms = if (lastNetworkRttDiv2Ms == 0.0) oneWay else (lastNetworkRttDiv2Ms * 0.8 + oneWay * 0.2)
        }
    }

    @Synchronized
    fun getSnapshot(): TelemetrySnapshot {
        val estimatedTotal = avgCaptureMs + avgEncodeMs + lastNetworkRttDiv2Ms + avgDecodeMs
        return TelemetrySnapshot(
            fps = currentFps,
            bitrateMbps = currentBitrateMbps,
            captureMs = avgCaptureMs,
            encodeMs = avgEncodeMs,
            networkRttDiv2Ms = lastNetworkRttDiv2Ms,
            decodeMs = avgDecodeMs,
            estimatedPipelineMs = estimatedTotal
        )
    }
}

