package com.secondscreen.client.decoder

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

/** Result of feeding one access unit (frame) to the decoder. */
enum class DecodeResult {
    /** Frame was queued to the codec. */
    OK,

    /** No codec input buffer became free within the wait budget. The frame was NOT fed, so the reference chain is broken. */
    NO_INPUT,

    /** Decoder not configured, input buffer too small, or the codec threw. */
    ERROR
}

/**
 * Hardware H.264 decoder.
 *
 * Changes vs. the original:
 *  - decodeFrame() is meant to run on a dedicated decoder thread (NOT the main thread).
 *    All public methods are guarded by one lock so surfaceDestroyed()/close() on the main thread is safe.
 *  - Waits up to INPUT_WAIT_NS (30 ms) for a free input buffer instead of ~10 ms, draining outputs while waiting.
 *  - Returns DecodeResult instead of Boolean so the caller can tell "decoder busy" from "real error".
 *  - Pending outputs are collected and only the newest one is rendered (older ones are released without rendering),
 *    so decoding is never skipped (reference chain stays intact) but the screen always shows the freshest frame.
 *  - KEY_MAX_INPUT_SIZE is raised so a large IDR cannot overflow the default input buffer.
 */
class VideoDecoder(private val surface: Surface) : AutoCloseable {

    companion object {
        private const val TAG = "VideoDecoder"
        private const val MIME_TYPE = "video/avc"
        private const val INPUT_WAIT_NS = 30_000_000L // 30 ms total budget for a free input buffer
        private const val MAX_INPUT_SIZE = 2 * 1024 * 1024 // 2 MB, comfortably above any IDR at 40 Mbps
    }

    private val lock = Any()
    private var codec: MediaCodec? = null

    @Volatile
    var isConfigured = false
        private set

    private val bufferInfo = MediaCodec.BufferInfo()
    private var lastWidth = 0
    private var lastHeight = 0
    private var lastSpsPps: ByteArray? = null

    fun configure(width: Int, height: Int, spsPpsData: ByteArray) {
        synchronized(lock) { configureLocked(width, height, spsPpsData) }
    }

    private fun configureLocked(width: Int, height: Int, spsPpsData: ByteArray) {
        if (isConfigured && codec != null && lastWidth == width && lastHeight == height &&
            lastSpsPps != null && lastSpsPps.contentEquals(spsPpsData)
        ) {
            Log.d(TAG, "Decoder already configured with matching parameters, skipping recreate")
            return
        }

        closeLocked()
        lastWidth = width
        lastHeight = height
        lastSpsPps = spsPpsData.clone()

        try {
            val decoder = MediaCodec.createDecoderByType(MIME_TYPE)
            try {
                val format = MediaFormat.createVideoFormat(MIME_TYPE, width, height)
                format.setByteBuffer("csd-0", ByteBuffer.wrap(spsPpsData))
                format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, MAX_INPUT_SIZE)

                // Low-Latency Flags (same vendor set Moonlight uses against Sunshine)
                format.setInteger(MediaFormat.KEY_PRIORITY, 0) // Realtime priority
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                }
                format.setInteger("vendor.qti-ext-dec-picture-order.enable", 1) // Qualcomm
                format.setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
                format.setInteger("vendor.rtc-ext-dec-low-latency.enable", 1) // Exynos
                format.setInteger("vendor.low-latency.enable", 1) // Amlogic
                format.setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-req", 1) // Kirin
                format.setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-rdy", -1)

                decoder.configure(format, surface, null, 0)
            } catch (_: Exception) {
                // Fallback to clean MediaFormat if vendor low-latency keys are rejected
                val basicFormat = MediaFormat.createVideoFormat(MIME_TYPE, width, height)
                basicFormat.setByteBuffer("csd-0", ByteBuffer.wrap(spsPpsData))
                basicFormat.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, MAX_INPUT_SIZE)
                decoder.configure(basicFormat, surface, null, 0)
            }

            decoder.start()
            codec = decoder
            isConfigured = true
            Log.i(TAG, "MediaCodec AVC low-latency decoder started: ${width}x${height}")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize MediaCodec decoder", e)
            closeLocked()
            throw e
        }
    }

    fun decodeFrame(nalData: ByteArray, ptsUs: Long, isKeyframe: Boolean): DecodeResult {
        return synchronized(lock) { decodeFrameLocked(nalData, ptsUs, isKeyframe) }
    }

    private fun decodeFrameLocked(nalData: ByteArray, ptsUs: Long, isKeyframe: Boolean): DecodeResult {
        val decoder = codec ?: return DecodeResult.ERROR
        if (!isConfigured) return DecodeResult.ERROR

        try {
            // Free finished outputs first (render only the newest) so the codec can accept input.
            renderLatest(decoder, 0)

            var inIndex = decoder.dequeueInputBuffer(2_000)
            val deadlineNs = System.nanoTime() + INPUT_WAIT_NS
            while (inIndex < 0 && System.nanoTime() < deadlineNs) {
                renderLatest(decoder, 0)
                inIndex = decoder.dequeueInputBuffer(2_000)
            }

            if (inIndex < 0) {
                Log.w(TAG, "MediaCodec input buffer unavailable")
                return DecodeResult.NO_INPUT
            }

            val inputBuffer = decoder.getInputBuffer(inIndex)
            if (inputBuffer == null || nalData.size > inputBuffer.capacity()) {
                Log.e(TAG, "Input buffer too small: need ${nalData.size}, have ${inputBuffer?.capacity()}")
                // Hand the acquired buffer back (empty) so the codec does not leak it.
                decoder.queueInputBuffer(inIndex, 0, 0, ptsUs, 0)
                return DecodeResult.ERROR
            }

            inputBuffer.clear()
            inputBuffer.put(nalData)
            val flags = if (isKeyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
            decoder.queueInputBuffer(inIndex, 0, nalData.size, ptsUs, flags)

            // Wait briefly for the output of this frame; render only the newest available.
            renderLatest(decoder, 2_500)
            return DecodeResult.OK
        } catch (e: Exception) {
            Log.w(TAG, "Decode frame error", e)
            return DecodeResult.ERROR
        }
    }

    /**
     * Collects every output that is ready. Older ones are released WITHOUT rendering,
     * only the newest is rendered. Decoding itself is never skipped.
     */
    private fun renderLatest(decoder: MediaCodec, firstTimeoutUs: Long) {
        var pending = -1
        var idx = decoder.dequeueOutputBuffer(bufferInfo, firstTimeoutUs)
        while (true) {
            if (idx >= 0) {
                if (pending >= 0) decoder.releaseOutputBuffer(pending, false)
                pending = idx
                idx = decoder.dequeueOutputBuffer(bufferInfo, 0)
            } else if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                idx = decoder.dequeueOutputBuffer(bufferInfo, 0)
            } else {
                break // INFO_TRY_AGAIN_LATER (or anything else): nothing more ready
            }
        }
        if (pending >= 0) decoder.releaseOutputBuffer(pending, true)
    }

    override fun close() {
        synchronized(lock) { closeLocked() }
    }

    private fun closeLocked() {
        isConfigured = false
        lastSpsPps = null
        try {
            codec?.stop()
        } catch (_: Exception) {
        }
        try {
            codec?.release()
        } catch (_: Exception) {
        }
        codec = null
    }
}
