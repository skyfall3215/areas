package com.secondscreen.client.ui

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Color
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.text.Editable
import android.text.TextWatcher
import android.util.DisplayMetrics
import android.util.Log
import android.view.HapticFeedbackConstants
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.LinearLayout
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.secondscreen.client.databinding.ActivityMainBinding
import com.secondscreen.client.decoder.VideoDecoder
import com.secondscreen.client.input.TouchCoordinateMapper
import com.secondscreen.client.network.DiscoveredHost
import com.secondscreen.client.network.LanDiscoveryListener
import com.secondscreen.client.protocol.FrameHeader
import com.secondscreen.client.protocol.HelloAckMessage
import com.secondscreen.client.protocol.HelloMessage
import com.secondscreen.client.protocol.KeyboardEventMessage
import com.secondscreen.client.protocol.KeyboardFlags
import com.secondscreen.client.protocol.KeyframeReqMessage
import com.secondscreen.client.protocol.MessageType
import com.secondscreen.client.protocol.PingMessage
import com.secondscreen.client.protocol.PointerEntry
import com.secondscreen.client.protocol.PongMessage
import com.secondscreen.client.protocol.ProtocolConstants
import com.secondscreen.client.protocol.TouchAction
import com.secondscreen.client.protocol.TouchEventMessage
import com.secondscreen.client.protocol.VideoConfigMessage
import com.secondscreen.client.protocol.VideoFrameMessage
import com.secondscreen.client.telemetry.TelemetryTracker
import com.secondscreen.client.transport.ClientIsolationException
import com.secondscreen.client.transport.SocketTransport
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
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
    private var isStatsMenuOpen = false
    private var activeTransport: SocketTransport? = null
    private var wifiLock: WifiManager.WifiLock? = null

    private val discoveryListener = LanDiscoveryListener()
    private var userCancelled = false
    private var isStreamingActive = false
    private val touchChannel = Channel<TouchEventMessage>(
        capacity = 16,
        onBufferOverflow = BufferOverflow.DROP_OLDEST
    )
    private val keyboardChannel = Channel<KeyboardEventMessage>(
        capacity = 64,
        onBufferOverflow = BufferOverflow.DROP_OLDEST
    )
    private var isKeyboardVisible = false

    // Windows Touch / Gesture Recognition State (Moonlight Trackpad & Multi-touch Style)
    private var touchDownTime = 0L
    private var touchDownX = 0f
    private var touchDownY = 0f
    private var lastTouchX = 0f
    private var lastTouchY = 0f
    private var isDragging = false
    private var isTapCandidate = false
    private var lastTapUpTime = 0L
    private var lastTrackpadMoveTime = 0L
    private var longPressJob: Job? = null
    private var lastTwoFingerX = 0f
    private var lastTwoFingerY = 0f
    private var isTwoFingerScroll = false
    private var twoFingerTapCandidate = false
    private var twoFingerDownTime = 0L
    private var twoFingerDownMidX = 0f
    private var twoFingerDownMidY = 0f
    private var twoFingerMaxMoveSq = 0f

    private fun acquireWifiLock() {
        try {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            if (wm != null) {
                val lockMode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    WifiManager.WIFI_MODE_FULL_LOW_LATENCY
                } else {
                    @Suppress("DEPRECATION")
                    WifiManager.WIFI_MODE_FULL_HIGH_PERF
                }
                wifiLock = wm.createWifiLock(lockMode, "SecondScreen:WifiLowLatencyLock").apply {
                    setReferenceCounted(false)
                    acquire()
                }
            }
        } catch (_: Exception) {}
    }

    private fun releaseWifiLock() {
        try {
            wifiLock?.let {
                if (it.isHeld) it.release()
            }
        } catch (_: Exception) {}
        wifiLock = null
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        enableImmersiveMode()

        binding.videoSurface.holder.addCallback(this)

        // Load preferences
        val prefs = getSharedPreferences("second_screen_prefs", Context.MODE_PRIVATE)
        val lastIp = prefs.getString("last_lan_ip", "") ?: ""
        if (lastIp.isNotEmpty()) {
            binding.inputIpAddress.setText(lastIp)
        }

        // Bitrate selector (default 15 Mbps)
        val bitrateOptions = listOf(10, 15, 20, 30, 40)
        val bitrateLabels = listOf("10 Mbps", "15 Mbps (Varsayılan)", "20 Mbps", "30 Mbps", "40 Mbps")
        val bitrateAdapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, bitrateLabels)
        binding.spinnerBitrate.adapter = bitrateAdapter
        val savedBitrate = prefs.getInt("preferred_bitrate_mbps", 15)
        val defaultIdx = bitrateOptions.indexOf(savedBitrate).takeIf { it >= 0 } ?: 1
        binding.spinnerBitrate.setSelection(defaultIdx)
        binding.spinnerBitrate.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                val chosen = bitrateOptions[position]
                prefs.edit().putInt("preferred_bitrate_mbps", chosen).apply()
            }
            override fun onNothingSelected(parent: AdapterView<*>?) {}
        }

        binding.btnConnectUsb.setOnClickListener {
            connectTo("127.0.0.1")
        }

        binding.btnConnectHotspot.setOnClickListener {
            connectTo("192.168.137.1")
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

        binding.btnToggleStats.setOnClickListener {
            toggleStatsMenu()
        }

        binding.btnCloseStats.setOnClickListener {
            setStatsMenuVisible(false)
        }

        binding.hudOverlay.setOnClickListener {
            setStatsMenuVisible(false)
        }

        binding.btnDisconnectStream.setOnClickListener {
            cancelActiveSession()
        }

        binding.btnToggleKeyboard.setOnClickListener {
            Log.d("SecondScreenTouch", "btnToggleKeyboard clicked by user!")
            toggleSoftKeyboard()
        }

        binding.hiddenInputReceiver.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {
                if (s.isNullOrEmpty()) return
                for (i in start until (start + count)) {
                    val c = s[i]
                    keyboardChannel.trySend(
                        KeyboardEventMessage(
                            keyCode = c.code,
                            flags = (KeyboardFlags.KEY_DOWN.toInt() or KeyboardFlags.IS_UNICODE.toInt()).toByte()
                        )
                    )
                    keyboardChannel.trySend(
                        KeyboardEventMessage(
                            keyCode = c.code,
                            flags = KeyboardFlags.IS_UNICODE
                        )
                    )
                }
            }
            override fun afterTextChanged(s: Editable?) {
                if (!s.isNullOrEmpty()) {
                    s.clear()
                }
            }
        })

        // Start UDP LAN Discovery
        startDiscovery()

        // Enable multi-touch capture on the video surface
        setupTouchHandling()
    }

    override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
        if (isStreamingActive && lastStreamWidth > 0 && lastStreamHeight > 0) {
            // Allow interacting with the Stats/HUD menu if visible
            if (binding.hudOverlay.visibility == View.VISIBLE) {
                val hudRect = android.graphics.Rect()
                binding.hudOverlay.getGlobalVisibleRect(hudRect)
                if (hudRect.contains(ev.rawX.toInt(), ev.rawY.toInt())) {
                    return super.dispatchTouchEvent(ev)
                }
            }
            // Allow clicking the floating control dock (Keyboard, Stats, Exit) if visible
            if (binding.floatingControlDock.visibility == View.VISIBLE) {
                val dockRect = android.graphics.Rect()
                binding.floatingControlDock.getGlobalVisibleRect(dockRect)
                if (dockRect.contains(ev.rawX.toInt(), ev.rawY.toInt())) {
                    return super.dispatchTouchEvent(ev)
                }
            }
            if (processMotionEvent(ev)) {
                return true
            }
        }
        return super.dispatchTouchEvent(ev)
    }

    private fun toggleStatsMenu() {
        setStatsMenuVisible(!isStatsMenuOpen)
    }

    private fun setStatsMenuVisible(visible: Boolean) {
        isStatsMenuOpen = visible
        binding.hudOverlay.visibility = if (visible) View.VISIBLE else View.GONE
        binding.btnToggleStats.setTextColor(
            if (visible) Color.parseColor("#A78BFA") else Color.parseColor("#E2E8F0")
        )
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (isStreamingActive && event.action == KeyEvent.ACTION_DOWN) {
            when (event.keyCode) {
                KeyEvent.KEYCODE_DEL -> { // Backspace
                    keyboardChannel.trySend(KeyboardEventMessage(keyCode = 0x08, flags = KeyboardFlags.KEY_DOWN))
                    keyboardChannel.trySend(KeyboardEventMessage(keyCode = 0x08, flags = 0))
                    return true
                }
                KeyEvent.KEYCODE_ENTER -> { // Enter
                    keyboardChannel.trySend(KeyboardEventMessage(keyCode = 0x0D, flags = KeyboardFlags.KEY_DOWN))
                    keyboardChannel.trySend(KeyboardEventMessage(keyCode = 0x0D, flags = 0))
                    return true
                }
                KeyEvent.KEYCODE_TAB -> {
                    keyboardChannel.trySend(KeyboardEventMessage(keyCode = 0x09, flags = KeyboardFlags.KEY_DOWN))
                    keyboardChannel.trySend(KeyboardEventMessage(keyCode = 0x09, flags = 0))
                    return true
                }
                KeyEvent.KEYCODE_ESCAPE -> {
                    keyboardChannel.trySend(KeyboardEventMessage(keyCode = 0x1B, flags = KeyboardFlags.KEY_DOWN))
                    keyboardChannel.trySend(KeyboardEventMessage(keyCode = 0x1B, flags = 0))
                    return true
                }
                KeyEvent.KEYCODE_SPACE -> {
                    keyboardChannel.trySend(
                        KeyboardEventMessage(
                            keyCode = ' '.code,
                            flags = (KeyboardFlags.KEY_DOWN.toInt() or KeyboardFlags.IS_UNICODE.toInt()).toByte()
                        )
                    )
                    keyboardChannel.trySend(KeyboardEventMessage(keyCode = ' '.code, flags = KeyboardFlags.IS_UNICODE))
                    return true
                }
            }
        }
        return super.dispatchKeyEvent(event)
    }

    private fun toggleSoftKeyboard() {
        val imm = getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager
        Log.d("SecondScreenTouch", "toggleSoftKeyboard called! current isKeyboardVisible=$isKeyboardVisible")
        if (isKeyboardVisible) {
            imm.hideSoftInputFromWindow(binding.hiddenInputReceiver.windowToken, 0)
            binding.hiddenInputReceiver.clearFocus()
            isKeyboardVisible = false
        } else {
            binding.hiddenInputReceiver.requestFocus()
            val shown = imm.showSoftInput(binding.hiddenInputReceiver, InputMethodManager.SHOW_IMPLICIT)
            if (!shown) {
                imm.toggleSoftInput(InputMethodManager.SHOW_FORCED, 0)
            }
            isKeyboardVisible = true
        }
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun setupTouchHandling() {
        binding.videoSurface.isClickable = true
        binding.videoSurface.setOnTouchListener { _, event ->
            processMotionEvent(event)
        }
    }

    private fun processMotionEvent(event: MotionEvent): Boolean {
        if (!isStreamingActive || lastStreamWidth <= 0 || lastStreamHeight <= 0) {
            return false
        }

        val viewWidth = binding.videoSurface.width
        val viewHeight = binding.videoSurface.height
        if (viewWidth <= 0 || viewHeight <= 0) return false

        val actionMasked = event.actionMasked
        val pointerCount = event.pointerCount

        // 1. Üç Parmakla Dokunma -> Sanal Klavyeyi Aç/Kapat (Moonlight Mantığı)
        if (pointerCount == 3) {
            longPressJob?.cancel()
            if (isDragging) {
                isDragging = false
                touchChannel.trySend(
                    TouchEventMessage(1, 0, listOf(PointerEntry(0, TouchAction.UP, 0, 0, 1)))
                )
            }
            if (actionMasked == MotionEvent.ACTION_POINTER_DOWN && event.actionIndex == 2) {
                toggleSoftKeyboard()
                binding.root.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                return true
            }
            return true
        }

        // 2. İki Parmak Hareketleri (Moonlight: 2 Parmakla Çift Tıklama & 2 Boyutlu Kaydırma)
        if (pointerCount == 2) {
            longPressJob?.cancel()
            if (isDragging) {
                isDragging = false
                touchChannel.trySend(
                    TouchEventMessage(1, 0, listOf(PointerEntry(0, TouchAction.UP, 0, 0, 1)))
                )
            }
            isTapCandidate = false

            when (actionMasked) {
                MotionEvent.ACTION_POINTER_DOWN -> {
                    twoFingerDownTime = SystemClock.uptimeMillis()
                    twoFingerTapCandidate = true
                    isTwoFingerScroll = false
                    val midX = (event.getX(0) + event.getX(1)) / 2f
                    val midY = (event.getY(0) + event.getY(1)) / 2f
                    twoFingerDownMidX = midX
                    twoFingerDownMidY = midY
                    lastTwoFingerX = midX
                    lastTwoFingerY = midY
                    twoFingerMaxMoveSq = 0f
                }
                MotionEvent.ACTION_MOVE -> {
                    val currentMidX = (event.getX(0) + event.getX(1)) / 2f
                    val currentMidY = (event.getY(0) + event.getY(1)) / 2f
                    val totalDx = currentMidX - twoFingerDownMidX
                    val totalDy = currentMidY - twoFingerDownMidY
                    val moveSq = totalDx * totalDx + totalDy * totalDy
                    if (moveSq > twoFingerMaxMoveSq) {
                        twoFingerMaxMoveSq = moveSq
                    }

                    val stepDx = currentMidX - lastTwoFingerX
                    val stepDy = currentMidY - lastTwoFingerY

                    // 12 piksel hareket eşiği aşıldığında kaydırma başlar
                    if (twoFingerMaxMoveSq > 144f) {
                        twoFingerTapCandidate = false
                        isTwoFingerScroll = true

                        val scrollX = (stepDx * 2.5f).toInt().coerceIn(-480, 480)
                        val scrollY = (stepDy * 3.5f).toInt().coerceIn(-480, 480)

                        if (scrollX != 0 || scrollY != 0) {
                            touchChannel.trySend(
                                TouchEventMessage(
                                    pointerCount = 1,
                                    changedIndex = 0,
                                    pointers = listOf(
                                        PointerEntry(0, TouchAction.SCROLL_2D, scrollX, scrollY)
                                    )
                                )
                            )
                        }
                        lastTwoFingerX = currentMidX
                        lastTwoFingerY = currentMidY
                    }
                }
                MotionEvent.ACTION_POINTER_UP -> {
                    // İki parmakla ekrana dokunup bırakma (kısa süre ve minimum hareket) = ÇİFT TIKLAMA (Double Click)!
                    val duration = SystemClock.uptimeMillis() - twoFingerDownTime
                    if (twoFingerTapCandidate && duration < 350 && twoFingerMaxMoveSq < 225f) {
                        val coords = TouchCoordinateMapper.mapTouchToNormalized(
                            twoFingerDownMidX, twoFingerDownMidY, viewWidth, viewHeight, lastStreamWidth, lastStreamHeight
                        )
                        coords?.let {
                            val useCurrentCursor = if (SystemClock.uptimeMillis() - lastTrackpadMoveTime < 4000L) 1 else 0
                            touchChannel.trySend(
                                TouchEventMessage(
                                    pointerCount = 1,
                                    changedIndex = 0,
                                    pointers = listOf(
                                        PointerEntry(0, TouchAction.DOUBLE_CLICK, it.first, it.second, useCurrentCursor)
                                    )
                                )
                            )
                            binding.root.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                            Log.d("SecondScreenTouch", "Two-Finger Tap -> Double Click at (${it.first}, ${it.second}) cursorOnly=$useCurrentCursor")
                        }
                    }
                }
            }
            return true
        }

        // 3. Tek Parmak Dokunma (Moonlight Touchpad Modu: Parmak kaydırarak fareyi yönetme, tıklama ve sürükleme)
        if (pointerCount == 1) {
            when (actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    val now = SystemClock.uptimeMillis()
                    touchDownTime = now
                    touchDownX = event.x
                    touchDownY = event.y
                    lastTouchX = event.x
                    lastTouchY = event.y
                    longPressJob?.cancel()

                    // Çift dokunup basılı tutma (Moonlight Drag): Sol tuşu basılı tutarak sürükle
                    if (now - lastTapUpTime < 260L) {
                        isDragging = true
                        isTapCandidate = false
                        touchChannel.trySend(
                            TouchEventMessage(
                                pointerCount = 1,
                                changedIndex = 0,
                                pointers = listOf(PointerEntry(0, TouchAction.DOWN, 0, 0, 1))
                            )
                        )
                        binding.root.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                    } else {
                        isDragging = false
                        isTapCandidate = true
                        val downX = event.x
                        val downY = event.y
                        longPressJob = lifecycleScope.launch {
                            delay(450L)
                            if (isTapCandidate && !isDragging) {
                                isTapCandidate = false
                                val coords = TouchCoordinateMapper.mapTouchToNormalized(
                                    downX, downY, viewWidth, viewHeight, lastStreamWidth, lastStreamHeight
                                )
                                coords?.let {
                                    val useCurrentCursor = if (SystemClock.uptimeMillis() - lastTrackpadMoveTime < 4000L) 1 else 0
                                    touchChannel.trySend(
                                        TouchEventMessage(
                                            pointerCount = 1,
                                            changedIndex = 0,
                                            pointers = listOf(
                                                PointerEntry(0, TouchAction.RIGHT_CLICK, it.first, it.second, useCurrentCursor)
                                            )
                                        )
                                    )
                                    binding.root.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                                    Log.d("SecondScreenTouch", "Long Press -> Right Click at (${it.first}, ${it.second}) cursorOnly=$useCurrentCursor")
                                }
                            }
                        }
                    }
                    return true
                }
                MotionEvent.ACTION_MOVE -> {
                    val dxFromDown = event.x - touchDownX
                    val dyFromDown = event.y - touchDownY
                    val distSq = dxFromDown * dxFromDown + dyFromDown * dyFromDown

                    // 12 piksel deadzone aşıldığında touchpad gibi imleci hareket ettir (sürükleme modunda da çalışır)
                    if (isDragging || distSq > 144f) {
                        isTapCandidate = false
                        longPressJob?.cancel()

                        val stepDx = event.x - lastTouchX
                        val stepDy = event.y - lastTouchY

                        // Tablet piksel hareketini Windows piksel ölçeğine çevir
                        val scaleX = lastStreamWidth.toFloat() / viewWidth.toFloat()
                        val scaleY = lastStreamHeight.toFloat() / viewHeight.toFloat()

                        val deltaX = (stepDx * scaleX).toInt()
                        val deltaY = (stepDy * scaleY).toInt()

                        if (deltaX != 0 || deltaY != 0) {
                            lastTrackpadMoveTime = SystemClock.uptimeMillis()
                            touchChannel.trySend(
                                TouchEventMessage(
                                    pointerCount = 1,
                                    changedIndex = 0,
                                    pointers = listOf(
                                        PointerEntry(0, TouchAction.MOUSE_MOVE_REL, deltaX, deltaY)
                                    )
                                )
                            )
                        }

                        lastTouchX = event.x
                        lastTouchY = event.y
                    }
                    return true
                }
                MotionEvent.ACTION_UP -> {
                    longPressJob?.cancel()
                    val now = SystemClock.uptimeMillis()
                    val duration = now - touchDownTime
                    val dxFromDown = event.x - touchDownX
                    val dyFromDown = event.y - touchDownY
                    val distSq = dxFromDown * dxFromDown + dyFromDown * dyFromDown

                    if (isDragging) {
                        touchChannel.trySend(
                            TouchEventMessage(
                                pointerCount = 1,
                                changedIndex = 0,
                                pointers = listOf(PointerEntry(0, TouchAction.UP, 0, 0, 1))
                            )
                        )
                    } else if (isTapCandidate && duration < 300 && distSq < 144f) {
                        // Tek parmakla hızlı dokunup bırakma (< 300 ms, hareket yok) = SOL TIK
                        val coords = TouchCoordinateMapper.mapTouchToNormalized(
                            event.x, event.y, viewWidth, viewHeight, lastStreamWidth, lastStreamHeight
                        )
                        coords?.let {
                            val useCurrentCursor = if (now - lastTrackpadMoveTime < 4000L) 1 else 0
                            touchChannel.trySend(
                                TouchEventMessage(
                                    pointerCount = 1,
                                    changedIndex = 0,
                                    pointers = listOf(
                                        PointerEntry(0, TouchAction.LEFT_CLICK, it.first, it.second, useCurrentCursor)
                                    )
                                )
                            )
                            lastTapUpTime = now
                            binding.root.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                            Log.d("SecondScreenTouch", "Single Tap -> Left Click at (${it.first}, ${it.second}) cursorOnly=$useCurrentCursor")
                        }
                    }
                    isTapCandidate = false
                    isDragging = false
                    return true
                }
                MotionEvent.ACTION_CANCEL -> {
                    longPressJob?.cancel()
                    if (isDragging) {
                        touchChannel.trySend(
                            TouchEventMessage(
                                pointerCount = 1,
                                changedIndex = 0,
                                pointers = listOf(PointerEntry(0, TouchAction.UP, 0, 0, 1))
                            )
                        )
                    }
                    isTapCandidate = false
                    isDragging = false
                    return true
                }
            }
        }

        return false
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
                text = "🖥️ ${host.hostname} (${host.ipAddress})"
                textSize = 13f
                isAllCaps = false
                setTextColor(Color.WHITE)
                setBackgroundColor(0xFF1E293B.toInt())
                val params = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT
                ).apply {
                    setMargins(0, 6, 0, 6)
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
            binding.btnConnectHotspot.isEnabled = false
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
                binding.btnConnectHotspot.isEnabled = true
                binding.btnConnectWifi.isEnabled = true
                binding.btnCancel.visibility = View.GONE
                if (binding.controlOverlay.visibility != View.VISIBLE) {
                    binding.controlOverlay.visibility = View.VISIBLE
                }
                setStatsMenuVisible(false)
                binding.floatingControlDock.visibility = View.GONE
                if (isKeyboardVisible) {
                    toggleSoftKeyboard()
                }
            }
        }
    }

    private fun cancelActiveSession() {
        userCancelled = true
        val currentTransport = activeTransport
        lifecycleScope.launch(Dispatchers.IO) {
            try {
                if (currentTransport != null && currentTransport.isConnected) {
                    val discHeader = FrameHeader.create(MessageType.DISCONNECT, 0L, sequence = 9999)
                    currentTransport.sendPacket(discHeader, ByteArray(0))
                }
            } catch (_: Exception) {
            } finally {
                try { currentTransport?.close() } catch (_: Exception) {}
            }
        }
        sessionJob?.cancel()
        binding.txtStatus.text = "Bağlantı sonlandırıldı."
        binding.txtStatus.setTextColor(getColor(android.R.color.darker_gray))
        binding.btnConnectUsb.isEnabled = true
        binding.btnConnectHotspot.isEnabled = true
        binding.btnConnectWifi.isEnabled = true
        binding.btnCancel.visibility = View.GONE
        binding.controlOverlay.visibility = View.VISIBLE
        setStatsMenuVisible(false)
        binding.floatingControlDock.visibility = View.GONE
        if (isKeyboardVisible) {
            toggleSoftKeyboard()
        }
    }

    private suspend fun runStreamingSession(host: String, port: Int) = kotlinx.coroutines.coroutineScope {
        val transport = SocketTransport(host, port, connectTimeoutMs = 2500)
        activeTransport = transport
        acquireWifiLock()

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

            val isUsb = host == "127.0.0.1" || host == "localhost"
            val featureFlags = ProtocolConstants.FEATURE_FLAG_TOUCH.toInt() or ProtocolConstants.FEATURE_FLAG_LOW_LATENCY_DECODER.toInt()

            val bitrateMbps = getSharedPreferences("second_screen_prefs", Context.MODE_PRIVATE)
                .getInt("preferred_bitrate_mbps", 15)
            val preferredBitrateKbps = bitrateMbps * 1000

            // 3. Send HELLO
            val hello = HelloMessage(
                nativeWidth = nativeWidth,
                nativeHeight = nativeHeight,
                maxFps = refreshRate,
                preferredBitrateKbps = preferredBitrateKbps,
                featureFlags = featureFlags.toByte(),
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
                binding.txtStatus.text = "Bağlandı (${ack.streamWidth}x${ack.streamHeight} @ ${ack.targetFps} FPS, ${ack.bitrateBps / 1_000_000} Mbps)"
                binding.controlOverlay.visibility = View.GONE
                val modeLabel = when {
                    isUsb -> "USB"
                    host == "192.168.137.1" -> "Hotspot"
                    else -> "Wi-Fi: $host"
                }
                binding.txtBanner.text = "Yayın: ${ack.streamWidth}x${ack.streamHeight} [$modeLabel]"
                // Keep statistics menu hidden by default so it does not cover the screen; user opens it via Stats button
                setStatsMenuVisible(false)
                binding.floatingControlDock.visibility = View.VISIBLE
            }

            isStreamingActive = true

            // Touch Sender loop (ultra-low latency, non-blocking via Channel)
            val touchSenderJob = launch(Dispatchers.IO) {
                var touchSeq = 5000
                for (touchMsg in touchChannel) {
                    if (!isActive || !transport.isConnected) break
                    val payload = touchMsg.toByteArray()
                    val header = FrameHeader.create(
                        MessageType.TOUCH_EVENT,
                        payload.size.toLong(),
                        sequence = touchSeq++
                    )
                    transport.sendPacket(header, payload)
                }
            }

            // Keyboard Sender loop (Moonlight style soft keyboard input)
            val keyboardSenderJob = launch(Dispatchers.IO) {
                var keySeq = 8000
                for (keyMsg in keyboardChannel) {
                    if (!isActive || !transport.isConnected) break
                    val payload = keyMsg.toByteArray()
                    val header = FrameHeader.create(
                        MessageType.KEYBOARD_EVENT,
                        payload.size.toLong(),
                        sequence = keySeq++
                    )
                    transport.sendPacket(header, payload)
                }
            }

            // 5. HUD update loop (every 300 ms)
            val hudJob = launch(Dispatchers.Main) {
                while (isActive) {
                    val s = telemetry.getSnapshot()
                    binding.txtHudFpsBitrate.text = String.format(Locale.US, "FPS: %d | Bitrate: %.1f Mbps", s.fps, s.bitrateMbps)
                    binding.txtHudSentBitrate.visibility = View.GONE
                    binding.txtHudLoss.visibility = View.GONE
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

            telemetry.resetCounters()
            val waitingForKeyframe = java.util.concurrent.atomic.AtomicBoolean(false)
            var sequence = 1000
            var lastKeyframeReqMs = 0L

            val maybeRequestKeyframe: (Long) -> Unit = { lastGoodFrame ->
                val now = System.currentTimeMillis()
                // Rate limit: at most 1 keyframe request per 500 ms (max ~1-2/sec) to avoid IDR storms
                if (now - lastKeyframeReqMs >= 500) {
                    lastKeyframeReqMs = now
                    telemetry.onIdrRequested()
                    launch(Dispatchers.IO) {
                        try {
                            val req = KeyframeReqMessage(lastGoodFrame)
                            val reqPayload = req.toByteArray()
                            val reqHeader = FrameHeader.create(MessageType.KEYFRAME_REQ, reqPayload.size.toLong(), sequence = sequence++)
                            transport.sendPacket(reqHeader, reqPayload)
                        } catch (_: Exception) {}
                    }
                }
            }

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
                            // Handled over TCP when not in Sunshine mode
                            val frame = VideoFrameMessage.readPayloadFrom(pktPayload, pktHeader.flags)
                            if (waitingForKeyframe.get()) {
                                if (!frame.isKeyframe) {
                                    telemetry.onFrameDropped()
                                    continue
                                }
                                waitingForKeyframe.set(false)
                            }

                            val t0 = System.nanoTime()
                            val ok = videoDecoder?.decodeFrame(frame.nalData, pktHeader.timestampUs, frame.isKeyframe) ?: true
                            val decNs = System.nanoTime() - t0
                            telemetry.onFrameDecoded(frame.captureDurationUs, frame.encodeDurationUs, decNs, pktPayload.size)

                            if (!ok) {
                                waitingForKeyframe.set(true)
                                telemetry.onFrameDropped()
                                maybeRequestKeyframe(frame.frameNumber)
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
                isStreamingActive = false
                touchSenderJob.cancel()
                while (touchChannel.tryReceive().isSuccess) {}
                hudJob.cancel()
                pingJob.cancel()
            }
        } finally {
            if (activeTransport === transport) {
                activeTransport = null
            }
            releaseWifiLock()
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
        releaseWifiLock()
        discoveryListener.stop()
        sessionJob?.cancel()
        videoDecoder?.close()
    }
}
