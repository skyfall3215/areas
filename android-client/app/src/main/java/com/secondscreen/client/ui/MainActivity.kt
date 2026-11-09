package com.secondscreen.client.ui

import android.content.Context
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.util.DisplayMetrics
import android.view.SurfaceHolder
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.LinearLayout
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.secondscreen.client.databinding.ActivityMainBinding
import com.secondscreen.client.decoder.VideoDecoder
import com.secondscreen.client.network.DiscoveredHost
import com.secondscreen.client.network.LanDiscoveryListener
import com.secondscreen.client.protocol.FrameHeader
import com.secondscreen.client.protocol.HelloAckMessage
import com.secondscreen.client.protocol.HelloMessage
import com.secondscreen.client.protocol.MessageType
import com.secondscreen.client.protocol.PingMessage
import com.secondscreen.client.protocol.PongMessage
import com.secondscreen.client.protocol.ProtocolConstants
import com.secondscreen.client.protocol.VideoConfigMessage
import com.secondscreen.client.protocol.VideoFrameMessage
import com.secondscreen.client.telemetry.TelemetryTracker
import com.secondscreen.client.transport.ClientIsolationException
import com.secondscreen.client.transport.SocketTransport
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.nio.ByteBuffer
import java.util.Locale

class MainActivity : AppCompatActivity(), SurfaceHolder.Callback {

    private lateinit var binding: ActivityMainBinding
    private var videoDecoder: VideoDecoder? = null
    private var sessionJob: Job? = null
    private var isSurfaceAvailable = false
    private var lastStreamWidth = 1920
    private var lastStreamHeight = 1080
    private val telemetry = TelemetryTracker()
    private var isHudDetailed = true

    private val discoveryListener = LanDiscoveryListener()
    private var userCancelled = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        enableImmersiveMode()

        binding.videoSurface.holder.addCallback(this)

        // Load last used IP
        val prefs = getSharedPreferences("second_screen_prefs", Context.MODE_PRIVATE)
        val lastIp = prefs.getString("last_lan_ip", "") ?: ""
        if (lastIp.isNotEmpty()) {
            binding.inputIpAddress.setText(lastIp)
        }

        binding.btnConnectUsb.setOnClickListener {
            connectTo("127.0.0.1")
        }

        binding.btnConnectWifi.setOnClickListener {
            val ip = binding.inputIpAddress.text.toString().trim()
            if (ip.isNotEmpty()) {
                prefs.edit().putString("last_lan_ip", ip).apply()
                connectTo(ip)
            } else {
                binding.txtStatus.text = "Lütfen geçerli bir IP adresi girin."
                binding.txtStatus.setTextColor(getColor(android.R.color.holo_red_light))
            }
        }

        binding.btnCancel.setOnClickListener {
            cancelActiveSession()
        }

        binding.hudOverlay.setOnClickListener {
            isHudDetailed = !isHudDetailed
            binding.txtHudHeader.visibility = if (isHudDetailed) View.VISIBLE else View.GONE
            binding.txtHudEncode.visibility = if (isHudDetailed) View.VISIBLE else View.GONE
            binding.txtHudNetwork.visibility = if (isHudDetailed) View.VISIBLE else View.GONE
            binding.txtHudDecode.visibility = if (isHudDetailed) View.VISIBLE else View.GONE
        }

        // Start UDP LAN Discovery
        startDiscovery()
    }

    private fun startDiscovery() {
        discoveryListener.start(lifecycleScope)
        lifecycleScope.launch {
            discoveryListener.discoveredHosts.collectLatest { hosts ->
                updateDiscoveredUi(hosts)
            }
        }
    }

    private fun updateDiscoveredUi(hosts: List<DiscoveredHost>) {
        if (hosts.isEmpty()) {
            binding.discoveredSection.visibility = View.GONE
            binding.discoveredContainer.removeAllViews()
            return
        }

        binding.discoveredSection.visibility = View.VISIBLE
        binding.discoveredContainer.removeAllViews()

        for (host in hosts) {
            val btn = Button(this).apply {
                text = "${host.hostname} (${host.ipAddress})"
                textSize = 12f
                setTextColor(Color.WHITE)
                setBackgroundColor(0xFF263238.toInt())
                val params = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT
                ).apply {
                    setMargins(0, 4, 0, 4)
                }
                layoutParams = params
                setOnClickListener {
                    binding.inputIpAddress.setText(host.ipAddress)
                    getSharedPreferences("second_screen_prefs", Context.MODE_PRIVATE)
                        .edit().putString("last_lan_ip", host.ipAddress).apply()
                    connectTo(host.ipAddress, host.port)
                }
            }
            binding.discoveredContainer.addView(btn)
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

    private fun hideKeyboard() {
        val imm = getSystemService(Context.INPUT_METHOD_SERVICE) as? InputMethodManager
        currentFocus?.let { view ->
            imm?.hideSoftInputFromWindow(view.windowToken, 0)
        }
    }

    private fun connectTo(host: String, port: Int = ProtocolConstants.DEFAULT_PORT) {
        hideKeyboard()
        userCancelled = false
        sessionJob?.cancel()

        sessionJob = lifecycleScope.launch {
            binding.btnConnectUsb.isEnabled = false
            binding.btnConnectWifi.isEnabled = false
            binding.btnCancel.visibility = View.VISIBLE
            binding.txtWarning.visibility = View.GONE

            val backoffs = listOf(500L, 1000L, 2000L, 3000L)
            var attempt = 0

            while (isActive && !userCancelled) {
                attempt++
                val isRetry = attempt > 1

                withContext(Dispatchers.Main) {
                    if (isRetry) {
                        binding.txtStatus.text = "Yeniden bağlanılıyor ($attempt)... Hedef: $host:$port"
                    } else {
                        binding.txtStatus.text = "Bağlanıyor ($host:$port)..."
                    }
                    binding.txtStatus.setTextColor(getColor(android.R.color.darker_gray))
                }

                var sessionSucceededOnce = false
                try {
                    runStreamingSession(host, port)
                    sessionSucceededOnce = true
                } catch (e: ClientIsolationException) {
                    // Yurt / Okul Wi-Fi Client Isolation detected!
                    withContext(Dispatchers.Main) {
                        binding.txtWarning.text = e.message
                        binding.txtWarning.visibility = View.VISIBLE
                        binding.txtStatus.text = "Bağlantı engellendi (Client Isolation)"
                        binding.txtStatus.setTextColor(getColor(android.R.color.holo_red_light))
                    }
                    break // Stop retry loop
                } catch (e: CancellationException) {
                    break
                } catch (e: Exception) {
                    withContext(Dispatchers.Main) {
                        binding.txtStatus.text = "Bağlantı koptu veya kurulamadı: ${e.message}"
                        binding.txtStatus.setTextColor(getColor(android.R.color.holo_red_light))
                    }
                }

                if (userCancelled || !isActive) break

                // Exponential backoff before next attempt
                val backoffMs = backoffs[((attempt - 1).coerceAtMost(backoffs.size - 1))]
                delay(backoffMs)
            }

            // Cleanup UI after session terminates
            withContext(Dispatchers.Main) {
                binding.btnConnectUsb.isEnabled = true
                binding.btnConnectWifi.isEnabled = true
                binding.btnCancel.visibility = View.GONE
                if (binding.controlOverlay.visibility != View.VISIBLE) {
                    binding.controlOverlay.visibility = View.VISIBLE
                }
                binding.txtBanner.visibility = View.GONE
                binding.hudOverlay.visibility = View.GONE
            }
        }
    }

    private fun cancelActiveSession() {
        userCancelled = true
        sessionJob?.cancel()
        binding.txtStatus.text = "Bağlantı kullanıcı tarafından iptal edildi."
        binding.txtStatus.setTextColor(getColor(android.R.color.darker_gray))
        binding.btnConnectUsb.isEnabled = true
        binding.btnConnectWifi.isEnabled = true
        binding.btnCancel.visibility = View.GONE
        binding.controlOverlay.visibility = View.VISIBLE
        binding.txtBanner.visibility = View.GONE
        binding.hudOverlay.visibility = View.GONE
    }

    private suspend fun runStreamingSession(host: String, port: Int) = kotlinx.coroutines.coroutineScope {
        val transport = SocketTransport(host, port, connectTimeoutMs = 2500)

        try {
            // 1. Connect socket (throws ClientIsolationException if 2.5s timeout on LAN)
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
            val ack = HelloAckMessage.readPayloadFrom(ByteBuffer.wrap(ackPayload))
            lastStreamWidth = ack.streamWidth
            lastStreamHeight = ack.streamHeight

            withContext(Dispatchers.Main) {
                binding.txtStatus.text = "Bağlandı (${ack.streamWidth}x${ack.streamHeight} @ ${ack.targetFps} FPS)"
                binding.controlOverlay.visibility = View.GONE
                binding.txtBanner.visibility = View.VISIBLE
                val modeLabel = if (host == "127.0.0.1") "USB" else "Wi-Fi: $host"
                binding.txtBanner.text = "Yayın Aktif: ${ack.streamWidth}x${ack.streamHeight} [$modeLabel]"
                binding.hudOverlay.visibility = View.VISIBLE
            }

            // 5. HUD update loop (every 300 ms)
            val hudJob = launch(Dispatchers.Main) {
                while (isActive) {
                    val s = telemetry.getSnapshot()
                    binding.txtHudFpsBitrate.text = String.format(Locale.US, "FPS: %d | Bitrate: %.1f Mbps", s.fps, s.bitrateMbps)
                    binding.txtHudEncode.text = String.format(Locale.US, "Encode: %.1f ms (Cap: %.1f ms)", s.encodeMs, s.captureMs)
                    binding.txtHudNetwork.text = String.format(Locale.US, "Ağ (RTT/2): %.1f ms", s.networkRttDiv2Ms)
                    binding.txtHudDecode.text = String.format(Locale.US, "Decode: %.1f ms", s.decodeMs)
                    binding.txtHudTotal.text = String.format(Locale.US, "Tahmini Gecikme: %.1f ms", s.estimatedPipelineMs)
                    delay(300)
                }
            }

            // 6. Ping loop (every 1000 ms)
            val pingJob = launch(Dispatchers.IO) {
                var pingSeq = 100
                while (isActive && transport.isConnected) {
                    delay(1000)
                    val ping = telemetry.createPing()
                    val pingPayload = ping.toByteArray()
                    val pingHeader = FrameHeader.create(MessageType.PING, pingPayload.size.toLong(), sequence = pingSeq++)
                    transport.sendPacket(pingHeader, pingPayload)
                }
            }

            var sequence = 1000

            // 7. Stream receive loop
            try {
                while (isActive && transport.isConnected) {
                    val (pktHeader, pktPayload) = transport.receivePacket()
                    when (pktHeader.msgType) {
                        MessageType.VIDEO_CONFIG -> {
                            val config = VideoConfigMessage.readPayloadFrom(pktPayload)
                            videoDecoder?.configure(lastStreamWidth, lastStreamHeight, config.spsPpsData)
                        }
                        MessageType.VIDEO_FRAME -> {
                            val frame = VideoFrameMessage.readPayloadFrom(pktPayload, pktHeader.flags)
                            val t0 = System.nanoTime()
                            val ok = videoDecoder?.decodeFrame(frame.nalData, pktHeader.timestampUs, frame.isKeyframe) ?: true
                            val decNs = System.nanoTime() - t0
                            telemetry.onFrameDecoded(frame.captureDurationUs, frame.encodeDurationUs, decNs, pktPayload.size)

                            if (!ok) {
                                val req = com.secondscreen.client.protocol.KeyframeReqMessage(frame.frameNumber)
                                val reqPayload = req.toByteArray()
                                val reqHeader = FrameHeader.create(MessageType.KEYFRAME_REQ, reqPayload.size.toLong(), sequence = sequence++)
                                transport.sendPacket(reqHeader, reqPayload)
                            }
                        }
                        MessageType.PING -> {
                            val ping = PingMessage.readPayloadFrom(ByteBuffer.wrap(pktPayload))
                            val pong = telemetry.createPong(ping)
                            val pongPayload = pong.toByteArray()
                            val pongHeader = FrameHeader.create(MessageType.PONG, pongPayload.size.toLong(), sequence = sequence++)
                            transport.sendPacket(pongHeader, pongPayload)
                        }
                        MessageType.PONG -> {
                            val pong = PongMessage.readPayloadFrom(ByteBuffer.wrap(pktPayload))
                            telemetry.onPongReceived(pong)
                        }
                        MessageType.DISCONNECT -> {
                            userCancelled = true
                            break
                        }
                        else -> {
                            // Ignore other packet types
                        }
                    }
                }
            } finally {
                hudJob.cancel()
                pingJob.cancel()
            }
        } finally {
            transport.close()
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
        discoveryListener.stop()
        sessionJob?.cancel()
        videoDecoder?.close()
    }
}
