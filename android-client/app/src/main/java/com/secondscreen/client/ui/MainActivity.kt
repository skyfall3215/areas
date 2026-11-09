package com.secondscreen.client.ui

import android.content.res.Configuration
import android.os.Build
import android.os.Bundle
import android.util.DisplayMetrics
import android.view.SurfaceHolder
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.secondscreen.client.databinding.ActivityMainBinding
import com.secondscreen.client.decoder.VideoDecoder
import com.secondscreen.client.protocol.FrameHeader
import com.secondscreen.client.protocol.HelloAckMessage
import com.secondscreen.client.protocol.HelloMessage
import com.secondscreen.client.protocol.MessageType
import com.secondscreen.client.protocol.ProtocolConstants
import com.secondscreen.client.protocol.VideoConfigMessage
import com.secondscreen.client.protocol.VideoFrameMessage
import com.secondscreen.client.transport.SocketTransport
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : AppCompatActivity(), SurfaceHolder.Callback {

    private lateinit var binding: ActivityMainBinding
    private var videoDecoder: VideoDecoder? = null
    private var streamJob: Job? = null
    private var isSurfaceAvailable = false
    private var lastStreamWidth = 1920
    private var lastStreamHeight = 1080

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        enableImmersiveMode()

        binding.videoSurface.holder.addCallback(this)

        binding.btnConnect.setOnClickListener {
            startUsbSession()
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            enableImmersiveMode()
        }
    }

    private fun enableImmersiveMode() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.insetsController?.let {
                it.hide(WindowInsets.Type.statusBars() or WindowInsets.Type.navigationBars())
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = (
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                    or View.SYSTEM_UI_FLAG_FULLSCREEN
                    or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                    or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                    or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                    or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
            )
        }
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    private fun startUsbSession() {
        streamJob?.cancel()
        streamJob = lifecycleScope.launch {
            binding.btnConnect.isEnabled = false
            binding.txtStatus.text = "Bağlanıyor (127.0.0.1:27183)..."
            binding.txtStatus.setTextColor(getColor(android.R.color.darker_gray))

            val transport = SocketTransport("127.0.0.1", ProtocolConstants.DEFAULT_PORT)

            try {
                // 1. Connect socket
                transport.connect()

                // 2. Query screen metrics
                val metrics = DisplayMetrics()
                @Suppress("DEPRECATION")
                windowManager.defaultDisplay.getRealMetrics(metrics)
                val nativeWidth = metrics.widthPixels
                val nativeHeight = metrics.heightPixels
                @Suppress("DEPRECATION")
                val refreshRate = windowManager.defaultDisplay.refreshRate.toInt().coerceAtLeast(60)

                val deviceName = Build.MODEL ?: "Android Tablet"

                // 3. Send HELLO
                val hello = HelloMessage(
                    nativeWidth = nativeWidth,
                    nativeHeight = nativeHeight,
                    maxFps = refreshRate,
                    preferredBitrateKbps = 15000,
                    featureFlags = (ProtocolConstants.FEATURE_FLAG_TOUCH.toInt() or ProtocolConstants.FEATURE_FLAG_LOW_LATENCY_DECODER.toInt()).toByte(),
                    deviceName = deviceName
                )
                val helloPayload = hello.toByteArray()
                val helloHeader = FrameHeader.create(MessageType.HELLO, helloPayload.size.toLong(), sequence = 1)
                transport.sendPacket(helloHeader, helloPayload)

                withContext(Dispatchers.Main) {
                    binding.txtStatus.text = "El sıkışma bekleniyor..."
                }

                // 4. Wait for HELLO_ACK
                val (ackHeader, ackPayload) = transport.receivePacket()
                if (ackHeader.msgType != MessageType.HELLO_ACK) {
                    throw IllegalStateException("Expected HELLO_ACK, got ${ackHeader.msgType}")
                }
                val ack = HelloAckMessage.readPayloadFrom(java.nio.ByteBuffer.wrap(ackPayload))
                lastStreamWidth = ack.streamWidth
                lastStreamHeight = ack.streamHeight

                withContext(Dispatchers.Main) {
                    binding.txtStatus.text = "Bağlandı (${ack.streamWidth}x${ack.streamHeight} @ ${ack.targetFps} FPS)"
                    binding.controlOverlay.visibility = View.GONE
                    binding.txtBanner.visibility = View.VISIBLE
                    binding.txtBanner.text = "Yayın Aktif: ${ack.streamWidth}x${ack.streamHeight}"
                }

                // 5. Stream receive loop
                while (isActive && transport.isConnected) {
                    val (pktHeader, pktPayload) = transport.receivePacket()
                    when (pktHeader.msgType) {
                        MessageType.VIDEO_CONFIG -> {
                            val config = VideoConfigMessage.readPayloadFrom(pktPayload)
                            videoDecoder?.configure(lastStreamWidth, lastStreamHeight, config.spsPpsData)
                        }
                        MessageType.VIDEO_FRAME -> {
                            val frame = VideoFrameMessage.readPayloadFrom(pktPayload, pktHeader.flags)
                            videoDecoder?.decodeFrame(frame.nalData, pktHeader.timestampUs, frame.isKeyframe)
                        }
                        MessageType.DISCONNECT -> {
                            break
                        }
                        else -> {
                            // Ignored or other packets
                        }
                    }
                }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) {
                    binding.txtStatus.text = "Bağlantı koptu. Kabloyu ve USB hata ayıklamayı kontrol edin."
                    binding.txtStatus.setTextColor(getColor(android.R.color.holo_red_light))
                    binding.controlOverlay.visibility = View.VISIBLE
                    binding.txtBanner.visibility = View.GONE
                }
            } finally {
                transport.close()
                withContext(Dispatchers.Main) {
                    binding.btnConnect.isEnabled = true
                    if (binding.controlOverlay.visibility != View.VISIBLE) {
                        binding.controlOverlay.visibility = View.VISIBLE
                    }
                }
            }
        }
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        isSurfaceAvailable = true
        videoDecoder = VideoDecoder(holder.surface)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        isSurfaceAvailable = false
        videoDecoder?.close()
        videoDecoder = null
    }

    override fun onDestroy() {
        super.onDestroy()
        streamJob?.cancel()
        videoDecoder?.close()
    }
}

