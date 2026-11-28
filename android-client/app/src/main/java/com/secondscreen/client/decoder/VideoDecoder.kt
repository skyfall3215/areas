package com.secondscreen.client.decoder

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.Process
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

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
 * Synchronous hardware H.264 decoder with a dedicated Moonlight-style output-render thread.
 *
 * Input feeding ([decodeFrame]) runs on the caller's dedicated decoder thread ("AreasDecode"),
 * while decoded outputs are drained and rendered immediately on "AreasCodecOut" without
 * blocking input arrival or waiting after queueInputBuffer().
 *
 * Concurrency invariants:
 *  - [generation] is incremented in [closeLocked] and [configureLocked]; "AreasCodecOut" checks
 *    its captured generation on every iteration and exits cleanly before [MediaCodec.stop]/[MediaCodec.release].
 *  - "AreasCodecOut" NEVER acquires [lock], avoiding deadlocks with [closeLocked] / `surfaceDestroyed()`.
 *  - [decodeFrame] holds [lock] only for a short 2 ms [MediaCodec.dequeueInputBuffer] poll and
 *    [MediaCodec.queueInputBuffer] so lifecycle teardown never stalls on the full 30 ms wait budget.
 */
class VideoDecoder(private val surface: Surface) : AutoCloseable {

    companion object {
        private const val TAG = "VideoDecoder"
        private const val MIME_TYPE = "video/avc"
        private const val INPUT_WAIT_NS = 30_000_000L // 30 ms total budget for a free input buffer
        private const val INPUT_POLL_US = 2_000L // 2 ms per dequeueInputBuffer poll
        private const val OUTPUT_WAIT_US = 5_000L // 5 ms dequeueOutputBuffer wait on output thread
        private const val OUTPUT_JOIN_TIMEOUT_MS = 200L
        private const val MAX_INPUT_SIZE = 2 * 1024 * 1024 // 2 MB, comfortably above any IDR at 40 Mbps
        private const val MAX_PTS_MAP_SIZE = 64
    }

    private val lock = Any()
    private val generation = AtomicLong(0L)
    private var codec: MediaCodec? = null
    private var outputThread: Thread? = null
    private val ptsEnqueueMap = ConcurrentHashMap<Long, Long>()

    @Volatile
    var isConfigured = false
        private set

    @Volatile
    var lastRenderDecodeNs: Long = 0L
        private set

    private var lastWidth = 0
    private var lastHeight = 0
    private var lastSpsPps: ByteArray? = null

    fun configure(width: Int, height: Int, spsPpsData: ByteArray) {
        synchronized(lock) { configureLocked(width, height, spsPpsData) }
    }

    private fun configureLocked(width: Int, height: Int, spsPpsData: ByteArray) {
        if (isConfigured && codec != null && outputThread?.isAlive == true &&
            lastWidth == width && lastHeight == height &&
            lastSpsPps != null && lastSpsPps.contentEquals(spsPpsData)
        ) {
            Log.d(TAG, "Decoder already configured with matching parameters, skipping recreate")
            return
        }

        closeLocked()
        if (!surface.isValid) {
            throw IllegalStateException("Surface is not valid")
        }

        lastWidth = width
        lastHeight = height
        lastSpsPps = spsPpsData.clone()

        val myGen = generation.incrementAndGet()

        try {
            val (decoder, configuredTier) = createAndStartDecoder(width, height, spsPpsData)
            codec = decoder
            isConfigured = true

            val thread = Thread({ runOutputLoop(decoder, myGen) }, "AreasCodecOut").apply {
                isDaemon = true
                start()
            }
            outputThread = thread
            Log.i(TAG, "MediaCodec AVC sync decoder started ($configuredTier): ${width}x${height}")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize MediaCodec decoder", e)
            closeLocked()
            throw e
        }
    }

    private fun createAndStartDecoder(
        width: Int,
        height: Int,
        spsPpsData: ByteArray
    ): Pair<MediaCodec, String> {
        val tiers = listOf<Pair<String, () -> MediaFormat>>(
            "tier1 (standard + vendor low-latency)" to {
                createBaseFormat(width, height, spsPpsData).apply {
                    applyStandardLowLatencyKeys(this)
                    applyVendorLowLatencyKeys(this)
                }
            },
            "tier2 (standard low-latency)" to {
                createBaseFormat(width, height, spsPpsData).apply {
                    applyStandardLowLatencyKeys(this)
                }
            },
            "tier3 (basic fallback)" to {
                createBaseFormat(width, height, spsPpsData)
            }
        )

        var lastError: Exception? = null
        for ((tierName, buildFormat) in tiers) {
            var candidate: MediaCodec? = null
            try {
                candidate = MediaCodec.createDecoderByType(MIME_TYPE)
                candidate.configure(buildFormat(), surface, null, 0)
                candidate.start()
                return candidate to tierName
            } catch (e: Exception) {
                lastError = e
                Log.w(TAG, "MediaCodec $tierName failed, releasing and trying next tier", e)
                try {
                    candidate?.release()
                } catch (_: Exception) {
                }
            }
        }
        throw lastError ?: IllegalStateException("Failed to configure MediaCodec decoder")
    }

    private fun createBaseFormat(width: Int, height: Int, spsPpsData: ByteArray): MediaFormat {
        return MediaFormat.createVideoFormat(MIME_TYPE, width, height).apply {
            setByteBuffer("csd-0", ByteBuffer.wrap(spsPpsData))
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, MAX_INPUT_SIZE)
        }
    }

    private fun applyStandardLowLatencyKeys(format: MediaFormat) {
        format.setInteger(MediaFormat.KEY_PRIORITY, 0) // Realtime priority
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        }
    }

    private fun applyVendorLowLatencyKeys(format: MediaFormat) {
        // Vendor low-latency keys (same vendor set Moonlight uses against Sunshine)
        format.setInteger("vendor.qti-ext-dec-picture-order.enable", 1) // Qualcomm
        format.setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
        format.setInteger("vendor.rtc-ext-dec-low-latency.enable", 1) // Exynos
        format.setInteger("vendor.low-latency.enable", 1) // Amlogic
        format.setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-req", 1) // Kirin
        format.setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-rdy", -1)
    }

    fun decodeFrame(nalData: ByteArray, ptsUs: Long, isKeyframe: Boolean): DecodeResult {
        val deadlineNs = System.nanoTime() + INPUT_WAIT_NS
        var myGen = 0L

        while (true) {
            synchronized(lock) {
                val decoder = codec
                if (!isConfigured || decoder == null) return DecodeResult.ERROR
                if (myGen == 0L) {
                    myGen = generation.get()
                } else if (generation.get() != myGen) {
                    return DecodeResult.ERROR
                }

                val inIndex = try {
                    decoder.dequeueInputBuffer(INPUT_POLL_US)
                } catch (e: MediaCodec.CodecException) {
                    if (!e.isTransient) {
                        isConfigured = false
                    }
                    Log.w(TAG, "dequeueInputBuffer CodecException (transient=${e.isTransient})", e)
                    return DecodeResult.ERROR
                } catch (e: Exception) {
                    isConfigured = false
                    Log.w(TAG, "dequeueInputBuffer error", e)
                    return DecodeResult.ERROR
                }

                if (inIndex >= 0) {
                    return queueFrameLocked(decoder, inIndex, nalData, ptsUs, isKeyframe)
                }
            }

            if (System.nanoTime() >= deadlineNs) {
                Log.w(TAG, "MediaCodec input buffer unavailable")
                return DecodeResult.NO_INPUT
            }
        }
    }

    private fun queueFrameLocked(
        decoder: MediaCodec,
        inIndex: Int,
        nalData: ByteArray,
        ptsUs: Long,
        isKeyframe: Boolean
    ): DecodeResult {
        try {
            val inputBuffer = decoder.getInputBuffer(inIndex)
            if (inputBuffer == null || nalData.size > inputBuffer.capacity()) {
                Log.e(TAG, "Input buffer too small: need ${nalData.size}, have ${inputBuffer?.capacity()}")
                // Hand the acquired buffer back (empty) so the codec does not leak it.
                decoder.queueInputBuffer(inIndex, 0, 0, ptsUs, 0)
                return DecodeResult.ERROR
            }

            inputBuffer.clear()
            inputBuffer.put(nalData)
            if (ptsEnqueueMap.size >= MAX_PTS_MAP_SIZE) {
                ptsEnqueueMap.clear()
            }
            ptsEnqueueMap[ptsUs] = System.nanoTime()
            val flags = if (isKeyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
            decoder.queueInputBuffer(inIndex, 0, nalData.size, ptsUs, flags)
            return DecodeResult.OK
        } catch (e: MediaCodec.CodecException) {
            ptsEnqueueMap.remove(ptsUs)
            if (!e.isTransient) {
                isConfigured = false
            }
            Log.w(TAG, "Queue frame CodecException (transient=${e.isTransient})", e)
            return DecodeResult.ERROR
        } catch (e: Exception) {
            ptsEnqueueMap.remove(ptsUs)
            isConfigured = false
            Log.w(TAG, "Decode frame error", e)
            return DecodeResult.ERROR
        }
    }

    private fun runOutputLoop(decoder: MediaCodec, myGen: Long) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY)
        val info = MediaCodec.BufferInfo()

        while (generation.get() == myGen) {
            val idx = try {
                decoder.dequeueOutputBuffer(info, OUTPUT_WAIT_US)
            } catch (e: MediaCodec.CodecException) {
                if (generation.get() != myGen) break
                Log.e(
                    TAG,
                    "MediaCodec output CodecException (transient=${e.isTransient}, recoverable=${e.isRecoverable})",
                    e
                )
                if (!e.isTransient) {
                    isConfigured = false
                    break
                }
                continue
            } catch (e: IllegalStateException) {
                if (generation.get() != myGen) break
                Log.e(TAG, "MediaCodec output IllegalStateException", e)
                isConfigured = false
                break
            } catch (e: Exception) {
                if (generation.get() != myGen) break
                Log.e(TAG, "MediaCodec output error", e)
                isConfigured = false
                break
            }

            if (idx == MediaCodec.INFO_TRY_AGAIN_LATER) {
                continue
            }
            if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                try {
                    Log.d(TAG, "MediaCodec output format changed: ${decoder.outputFormat}")
                } catch (_: Exception) {
                }
                continue
            }
            if (idx < 0) {
                continue
            }

            // Drain any newer queued outputs without waiting; release older frames without rendering
            // so only the newest renderable frame is presented to the Surface.
            var latestIdx = idx
            var latestRenderable = isRenderable(info)
            var latestPtsUs = info.presentationTimeUs

            while (generation.get() == myGen) {
                val nextIdx = try {
                    decoder.dequeueOutputBuffer(info, 0)
                } catch (_: Exception) {
                    break
                }
                if (nextIdx >= 0) {
                    val nextRenderable = isRenderable(info)
                    if (nextRenderable || !latestRenderable) {
                        try {
                            decoder.releaseOutputBuffer(latestIdx, false)
                        } catch (_: Exception) {
                        }
                        ptsEnqueueMap.remove(latestPtsUs)
                        latestIdx = nextIdx
                        latestRenderable = nextRenderable
                        latestPtsUs = info.presentationTimeUs
                    } else {
                        try {
                            decoder.releaseOutputBuffer(nextIdx, false)
                        } catch (_: Exception) {
                        }
                    }
                } else if (nextIdx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    try {
                        Log.d(TAG, "MediaCodec output format changed: ${decoder.outputFormat}")
                    } catch (_: Exception) {
                    }
                } else {
                    break
                }
            }

            val render = generation.get() == myGen && latestRenderable
            try {
                decoder.releaseOutputBuffer(latestIdx, render)
            } catch (e: IllegalStateException) {
                if (generation.get() != myGen) break
                Log.e(TAG, "MediaCodec releaseOutputBuffer IllegalStateException", e)
                isConfigured = false
                break
            } catch (e: Exception) {
                if (generation.get() != myGen) break
                Log.e(TAG, "MediaCodec releaseOutputBuffer error", e)
                isConfigured = false
                break
            }

            val enqueueNs = ptsEnqueueMap.remove(latestPtsUs)
            if (render && enqueueNs != null) {
                val decodeNs = System.nanoTime() - enqueueNs
                if (decodeNs > 0L) {
                    lastRenderDecodeNs = decodeNs
                }
            }
        }
    }

    private fun isRenderable(info: MediaCodec.BufferInfo): Boolean {
        return info.size > 0 && (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0
    }

    override fun close() {
        synchronized(lock) { closeLocked() }
    }

    private fun closeLocked() {
        generation.incrementAndGet()
        isConfigured = false
        lastSpsPps = null
        lastRenderDecodeNs = 0L

        val thread = outputThread
        outputThread = null
        if (thread != null && thread !== Thread.currentThread()) {
            try {
                thread.join(OUTPUT_JOIN_TIMEOUT_MS)
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
            }
            if (thread.isAlive) {
                thread.interrupt()
            }
        }

        ptsEnqueueMap.clear()

        val mc = codec
        codec = null
        if (mc != null) {
            try {
                mc.stop()
            } catch (_: Exception) {
            }
            try {
                mc.release()
            } catch (_: Exception) {
            }
        }
    }
}
