package com.secondscreen.client.decoder

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

class VideoDecoder(private val surface: Surface) : AutoCloseable {

    companion object {
        private const val TAG = "VideoDecoder"
        private const val MIME_TYPE = "video/avc"
    }

    private var codec: MediaCodec? = null
    var isConfigured = false
        private set
    private val bufferInfo = MediaCodec.BufferInfo()
    private var lastWidth = 0
    private var lastHeight = 0
    private var lastSpsPps: ByteArray? = null

    fun configure(width: Int, height: Int, spsPpsData: ByteArray) {
        if (isConfigured && codec != null && lastWidth == width && lastHeight == height && lastSpsPps != null && lastSpsPps.contentEquals(spsPpsData)) {
            Log.d(TAG, "Decoder already configured with matching parameters, skipping recreate")
            return
        }

        close()
        lastWidth = width
        lastHeight = height
        lastSpsPps = spsPpsData.clone()

        try {
            val decoder = MediaCodec.createDecoderByType(MIME_TYPE)
            try {
                val format = MediaFormat.createVideoFormat(MIME_TYPE, width, height)
                format.setByteBuffer("csd-0", ByteBuffer.wrap(spsPpsData))

                // Low-Latency Flags (same vendor set Moonlight uses against Sunshine)
                format.setInteger(MediaFormat.KEY_PRIORITY, 0) // Realtime priority
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                }
                format.setInteger("vendor.qti-ext-dec-picture-order.enable", 1) // Qualcomm: output in decode order, no reorder buffering
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
                decoder.configure(basicFormat, surface, null, 0)
            }

            decoder.start()
            codec = decoder
            isConfigured = true
            Log.i(TAG, "MediaCodec AVC low-latency decoder started: ${width}x${height}")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize MediaCodec decoder", e)
            close()
            throw e
        }
    }

    fun decodeFrame(nalData: ByteArray, ptsUs: Long, isKeyframe: Boolean): Boolean {
        val decoder = codec ?: return false
        if (!isConfigured) return false

        try {
            // Drain any pending outputs first to keep queue empty
            var outIndex = decoder.dequeueOutputBuffer(bufferInfo, 0)
            while (outIndex >= 0) {
                decoder.releaseOutputBuffer(outIndex, true)
                outIndex = decoder.dequeueOutputBuffer(bufferInfo, 0)
            }

            var inIndex = decoder.dequeueInputBuffer(2_000)
            var retries = 0
            while (inIndex < 0 && retries < 4) {
                outIndex = decoder.dequeueOutputBuffer(bufferInfo, 0)
                while (outIndex >= 0) {
                    decoder.releaseOutputBuffer(outIndex, true)
                    outIndex = decoder.dequeueOutputBuffer(bufferInfo, 0)
                }
                inIndex = decoder.dequeueInputBuffer(2_000)
                retries++
            }

            if (inIndex >= 0) {
                val inputBuffer = decoder.getInputBuffer(inIndex)
                if (inputBuffer != null) {
                    inputBuffer.clear()
                    inputBuffer.put(nalData)
                    val flags = if (isKeyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
                    decoder.queueInputBuffer(inIndex, 0, nalData.size, ptsUs, flags)
                }
            } else {
                Log.w(TAG, "MediaCodec input buffer unavailable")
                return false
            }

            // Wait up to 2.5ms for immediate hardware decode output so the current frame is rendered right away
            outIndex = decoder.dequeueOutputBuffer(bufferInfo, 2_500)
            while (outIndex >= 0) {
                decoder.releaseOutputBuffer(outIndex, true)
                outIndex = decoder.dequeueOutputBuffer(bufferInfo, 0)
            }
            return true
        } catch (e: Exception) {
            Log.w(TAG, "Decode frame error", e)
            return false
        }
    }

    override fun close() {
        isConfigured = false
        lastSpsPps = null
        try {
            codec?.stop()
        } catch (_: Exception) {}
        try {
            codec?.release()
        } catch (_: Exception) {}
        codec = null
    }
}

