package com.secondscreen.client.ui

import android.animation.ObjectAnimator
import android.animation.ValueAnimator
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
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
import android.view.animation.DecelerateInterpolator
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.TextView
import androidx.annotation.ColorRes
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.secondscreen.client.R
import com.secondscreen.client.databinding.ActivityMainBinding
import com.secondscreen.client.databinding.ItemDiscoveredHostBinding
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
import com.secondscreen.client.decoder.DecodeResult
import com.secondscreen.client.transport.ClientIsolationException
import com.secondscreen.client.transport.SocketTransport
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.nio.ByteBuffer
import java.util.Locale
import kotlin.math.roundToInt

private sealed interface DecodeItem {
    class Config(val spsPps: ByteArray) : DecodeItem
    class Frame(
        val nal: ByteArray,
        val ptsUs: Long,
        val isKeyframe: Boolean,
        val frameNumber: Long,
        val captureUs: Int,
        val encodeUs: Int,
        val wireBytes: Int,
        val enqueuedAtNs: Long = System.nanoTime()
    ) : DecodeItem
}

private class AuthFailedException(message: String) : Exception(message)

private enum class ConnectionUiState {
    IDLE,
    CONNECTING,
    CONNECTED,
    ERROR
}

class MainActivity : AppCompatActivity(), SurfaceHolder.Callback {

    private lateinit var binding: ActivityMainBinding
    @Volatile private var videoDecoder: VideoDecoder? = null
    @Volatile private var latestSpsPps: ByteArray? = null
    @Volatile private var activeSurfaceResetHandler: (() -> Unit)? = null
    private var sessionJob: Job? = null
    private var isSurfaceAvailable = false
    @Volatile private var lastStreamWidth = 1920
    @Volatile private var lastStreamHeight = 1080
    private val telemetry = TelemetryTracker()
    private var isStatsMenuOpen = false
    private var activeTransport: SocketTransport? = null
    private var wifiLock: WifiManager.WifiLock? = null

    private val discoveryListener = LanDiscoveryListener()
    private var userCancelled = false
    private var isStreamingActive = false
    private var areConnectTriggersEnabled = true
    private var renderedHostKeys: List<Triple<String, String, Int>> = emptyList()
    private var radarPulseAnimator: ObjectAnimator? = null
    private val dockStateMachine = DockAutoHideStateMachine()
    private var dockAutoHideJob: Job? = null
    private var isTouchGestureRoutedToVideo = false
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

        // Populate dynamic brand version badge & tablet native display spec pill
        binding.txtVersionBadge.text = "v${resolveAppVersionName()}"
        populateDisplaySpecBadge()
        updateConnectionStatus(getString(R.string.status_disconnected), ConnectionUiState.IDLE)

        // Load preferences
        val prefs = getSharedPreferences("second_screen_prefs", Context.MODE_PRIVATE)
        val lastIp = prefs.getString("last_lan_ip", "") ?: ""
        if (lastIp.isNotEmpty()) {
            binding.inputIpAddress.setText(lastIp)
        }
        val lastPin = prefs.getString("last_pin", "") ?: ""
        if (lastPin.isNotEmpty()) {
            binding.inputPin.setText(lastPin)
        }

        // Bitrate pill chip selector (default 15 Mbps)
        val bitrateChipMap = listOf(
            R.id.chipBitrate10 to 10,
            R.id.chipBitrate15 to 15,
            R.id.chipBitrate20 to 20,
            R.id.chipBitrate30 to 30,
            R.id.chipBitrate40 to 40
        )
        val savedBitrate = prefs.getInt("preferred_bitrate_mbps", 15)
        val initialChipId = bitrateChipMap.firstOrNull { it.second == savedBitrate }?.first ?: R.id.chipBitrate15
        binding.chipGroupBitrate.check(initialChipId)
        binding.chipGroupBitrate.setOnCheckedStateChangeListener { _, checkedIds ->
            val checkedId = checkedIds.firstOrNull() ?: return@setOnCheckedStateChangeListener
            val chosen = bitrateChipMap.firstOrNull { it.first == checkedId }?.second ?: 15
            prefs.edit().putInt("preferred_bitrate_mbps", chosen).apply()
        }

        binding.inputIpAddress.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_GO || actionId == EditorInfo.IME_ACTION_DONE) {
                if (areConnectTriggersEnabled) {
                    binding.btnConnectWifi.performClick()
                }
                true
            } else {
                false
            }
        }

        binding.inputPin.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_DONE) {
                hideKeyboard()
                binding.inputPin.clearFocus()
                true
            } else {
                false
            }
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
                updateConnectionStatus(getString(R.string.status_invalid_ip), ConnectionUiState.ERROR)
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

        binding.btnDockCollapse.setOnClickListener {
            val nextMode = dockStateMachine.onToggleCollapseClicked()
            if (nextMode != DockMode.EXPANDED) {
                setStatsMenuVisible(false)
            }
            applyDockMode(nextMode, animate = true)
            scheduleDockAutoHide()
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
        startRadarPulseIfNeeded()

        // Enable multi-touch capture on the video surface
        setupTouchHandling()
    }

    override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
        if (isStreamingActive && lastStreamWidth > 0 && lastStreamHeight > 0) {
            val action = ev.actionMasked
            // Lock routing at ACTION_DOWN so waking a hidden dock on touch cannot hijack MOVE/UP of the same gesture.
            val routeToVideo = when (action) {
                MotionEvent.ACTION_DOWN -> {
                    // When collapsed, hit-test only the toggle button so fading content buttons cannot eat video taps.
                    val dockHitTarget = when (dockStateMachine.mode) {
                        DockMode.EXPANDED -> binding.floatingControlDock
                        DockMode.COLLAPSED -> binding.btnDockCollapse
                        DockMode.HIDDEN -> null
                    }
                    val hitsHud = isStatsMenuOpen && isPointInsideVisibleView(binding.hudOverlay, ev)
                    val hitsOverlay = hitsHud ||
                        (dockHitTarget != null && isPointInsideVisibleView(dockHitTarget, ev))
                    isTouchGestureRoutedToVideo = !hitsOverlay
                    !hitsOverlay
                }
                else -> isTouchGestureRoutedToVideo
            }

            if (action == MotionEvent.ACTION_DOWN) {
                cancelDockAutoHideTimer()
                val prevMode = dockStateMachine.mode
                val nextMode = dockStateMachine.onUserTouch()
                if (nextMode != prevMode) {
                    applyDockMode(nextMode, animate = true)
                }
            } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
                isTouchGestureRoutedToVideo = false
                scheduleDockAutoHide()
            }

            if (!routeToVideo) {
                return super.dispatchTouchEvent(ev)
            }
            if (processMotionEvent(ev)) {
                return true
            }
        }
        return super.dispatchTouchEvent(ev)
    }

    private fun isPointInsideVisibleView(view: View, ev: MotionEvent): Boolean {
        if (view.visibility != View.VISIBLE) return false
        val rect = android.graphics.Rect()
        view.getGlobalVisibleRect(rect)
        return rect.contains(ev.rawX.toInt(), ev.rawY.toInt())
    }

    private fun toggleStatsMenu() {
        setStatsMenuVisible(!isStatsMenuOpen)
    }

    private fun setStatsMenuVisible(visible: Boolean, animate: Boolean = true) {
        isStatsMenuOpen = visible
        val hud = binding.hudOverlay
        hud.animate().cancel()

        if (visible) {
            updateHudTelemetry(telemetry.getSnapshot())
            hud.visibility = View.VISIBLE
            if (animate) {
                val slideOffsetPx = 16f * resources.displayMetrics.density
                hud.alpha = 0f
                hud.translationY = slideOffsetPx
                hud.animate()
                    .alpha(1f)
                    .translationY(0f)
                    .setDuration(HUD_ANIM_IN_MS)
                    .setInterpolator(DecelerateInterpolator())
                    .start()
            } else {
                hud.alpha = 1f
                hud.translationY = 0f
            }
        } else {
            if (animate && hud.visibility == View.VISIBLE) {
                val slideOffsetPx = 16f * resources.displayMetrics.density
                hud.animate()
                    .alpha(0f)
                    .translationY(slideOffsetPx)
                    .setDuration(HUD_ANIM_OUT_MS)
                    .withEndAction {
                        if (!isStatsMenuOpen) {
                            hud.visibility = View.GONE
                            hud.alpha = 1f
                            hud.translationY = 0f
                        }
                    }
                    .start()
            } else {
                hud.visibility = View.GONE
                hud.alpha = 1f
                hud.translationY = 0f
            }
        }

        if (visible) {
            binding.btnToggleStats.backgroundTintList =
                ColorStateList.valueOf(ContextCompat.getColor(this, R.color.ripple_violet))
            val activeViolet = ContextCompat.getColor(this, R.color.brand_violet_soft)
            binding.btnToggleStats.setTextColor(activeViolet)
            binding.btnToggleStats.iconTint = ColorStateList.valueOf(activeViolet)
        } else {
            binding.btnToggleStats.backgroundTintList = ColorStateList.valueOf(Color.TRANSPARENT)
            binding.btnToggleStats.setTextColor(ContextCompat.getColor(this, R.color.text_primary))
            binding.btnToggleStats.iconTint = ColorStateList.valueOf(ContextCompat.getColor(this, R.color.text_secondary))
        }

        dockStateMachine.onStatsMenuVisibilityChanged(visible)
        if (visible) {
            cancelDockAutoHideTimer()
        } else {
            scheduleDockAutoHide()
        }
    }

    private fun applyDockMode(mode: DockMode, animate: Boolean) {
        val dock = binding.floatingControlDock
        val content = binding.dockContentGroup
        dock.animate().cancel()
        content.animate().cancel()

        when (mode) {
            DockMode.EXPANDED -> {
                binding.btnDockCollapse.setImageResource(R.drawable.ic_chevron_right)
                binding.btnDockCollapse.contentDescription = getString(R.string.cd_dock_collapse)
                val wasHidden = dock.visibility != View.VISIBLE
                val wasContentHidden = content.visibility != View.VISIBLE
                dock.visibility = View.VISIBLE
                content.visibility = View.VISIBLE
                if (animate) {
                    if (wasHidden) dock.alpha = 0f
                    if (wasContentHidden) content.alpha = 0f
                    dock.animate().alpha(1f).setDuration(DOCK_FADE_IN_MS).start()
                    content.animate().alpha(1f).setDuration(DOCK_FADE_IN_MS).start()
                } else {
                    dock.alpha = 1f
                    content.alpha = 1f
                }
            }
            DockMode.COLLAPSED -> {
                binding.btnDockCollapse.setImageResource(R.drawable.ic_chevron_left)
                binding.btnDockCollapse.contentDescription = getString(R.string.cd_dock_expand)
                val wasHidden = dock.visibility != View.VISIBLE
                dock.visibility = View.VISIBLE
                if (animate) {
                    if (wasHidden) {
                        dock.alpha = 0f
                        content.visibility = View.GONE
                        content.alpha = 1f
                    } else if (content.visibility == View.VISIBLE) {
                        content.animate()
                            .alpha(0f)
                            .setDuration(DOCK_CONTENT_FADE_OUT_MS)
                            .withEndAction {
                                if (dockStateMachine.mode == DockMode.COLLAPSED) {
                                    content.visibility = View.GONE
                                    content.alpha = 1f
                                }
                            }
                            .start()
                    }
                    dock.animate().alpha(DOCK_COLLAPSED_ALPHA).setDuration(DOCK_FADE_IN_MS).start()
                } else {
                    content.visibility = View.GONE
                    content.alpha = 1f
                    dock.alpha = DOCK_COLLAPSED_ALPHA
                }
            }
            DockMode.HIDDEN -> {
                if (animate && dock.visibility == View.VISIBLE) {
                    dock.animate()
                        .alpha(0f)
                        .setDuration(DOCK_FADE_OUT_MS)
                        .withEndAction {
                            if (dockStateMachine.mode == DockMode.HIDDEN) {
                                dock.visibility = View.GONE
                                content.visibility = View.GONE
                                dock.alpha = 1f
                                content.alpha = 1f
                            }
                        }
                        .start()
                } else {
                    dock.visibility = View.GONE
                    content.visibility = View.GONE
                    dock.alpha = 1f
                    content.alpha = 1f
                    binding.btnDockCollapse.setImageResource(R.drawable.ic_chevron_right)
                    binding.btnDockCollapse.contentDescription = getString(R.string.cd_dock_collapse)
                }
            }
        }
    }

    private fun scheduleDockAutoHide() {
        dockAutoHideJob?.cancel()
        val initialDelayMs = dockStateMachine.nextTimeoutDelayMs() ?: return
        dockAutoHideJob = lifecycleScope.launch {
            var nextDelayMs: Long? = initialDelayMs
            while (isActive && nextDelayMs != null) {
                delay(nextDelayMs)
                val prevMode = dockStateMachine.mode
                val nextMode = dockStateMachine.onInactivityTimeout()
                if (nextMode != prevMode) {
                    applyDockMode(nextMode, animate = true)
                }
                nextDelayMs = dockStateMachine.nextTimeoutDelayMs()
            }
        }
    }

    private fun cancelDockAutoHideTimer() {
        dockAutoHideJob?.cancel()
        dockAutoHideJob = null
    }

    private fun resetDockForDisconnectedState() {
        cancelDockAutoHideTimer()
        isTouchGestureRoutedToVideo = false
        dockStateMachine.onStreamStopped()
        setStatsMenuVisible(false, animate = false)
        applyDockMode(DockMode.HIDDEN, animate = false)
        updateKeyboardButtonState()
    }

    private fun updateKeyboardButtonState() {
        if (isKeyboardVisible) {
            binding.btnToggleKeyboard.setBackgroundResource(R.drawable.bg_dock_btn_active)
            binding.btnToggleKeyboard.imageTintList =
                ColorStateList.valueOf(ContextCompat.getColor(this, R.color.brand_cyan))
        } else {
            binding.btnToggleKeyboard.setBackgroundResource(R.drawable.bg_dock_btn)
            binding.btnToggleKeyboard.imageTintList =
                ColorStateList.valueOf(ContextCompat.getColor(this, R.color.text_primary))
        }
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
        updateKeyboardButtonState()
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

    private fun resolveAppVersionName(): String {
        return try {
            val info = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                packageManager.getPackageInfo(packageName, PackageManager.PackageInfoFlags.of(0))
            } else {
                @Suppress("DEPRECATION")
                packageManager.getPackageInfo(packageName, 0)
            }
            info.versionName?.takeIf { it.isNotBlank() } ?: "1.0.6"
        } catch (_: Exception) {
            "1.0.6"
        }
    }

    private fun queryNativeDisplaySpec(): Triple<Int, Int, Int> {
        val metrics = DisplayMetrics()
        @Suppress("DEPRECATION")
        windowManager.defaultDisplay.getRealMetrics(metrics)
        @Suppress("DEPRECATION")
        val refreshRate = windowManager.defaultDisplay.refreshRate.roundToInt().coerceAtLeast(60)
        return Triple(metrics.widthPixels, metrics.heightPixels, refreshRate)
    }

    private fun populateDisplaySpecBadge() {
        try {
            val (width, height, refreshRate) = queryNativeDisplaySpec()
            binding.txtDisplaySpec.text = "$width × $height • ${refreshRate} Hz"
        } catch (_: Exception) {
            binding.displaySpecPill.visibility = View.GONE
        }
    }

    private fun showControlOverlay() {
        binding.controlOverlay.visibility = View.VISIBLE
        startRadarPulseIfNeeded()
    }

    private fun hideControlOverlay() {
        stopRadarPulse()
        binding.controlOverlay.visibility = View.GONE
    }

    private fun startRadarPulseIfNeeded() {
        if (binding.controlOverlay.visibility != View.VISIBLE || renderedHostKeys.isNotEmpty()) {
            stopRadarPulse()
            return
        }
        if (radarPulseAnimator?.isRunning == true) return
        radarPulseAnimator = ObjectAnimator.ofFloat(binding.imgDiscoveryRadar, View.ALPHA, 1f, 0.32f).apply {
            duration = 1350L
            repeatCount = ValueAnimator.INFINITE
            repeatMode = ValueAnimator.REVERSE
            start()
        }
    }

    private fun stopRadarPulse() {
        radarPulseAnimator?.cancel()
        radarPulseAnimator = null
        binding.imgDiscoveryRadar.alpha = 1f
    }

    private fun setConnectTriggersEnabled(enabled: Boolean) {
        areConnectTriggersEnabled = enabled
        val targetAlpha = if (enabled) 1.0f else 0.48f
        listOf(binding.btnConnectUsb, binding.btnConnectHotspot, binding.btnConnectWifi).forEach { trigger ->
            trigger.isEnabled = enabled
            trigger.alpha = targetAlpha
        }
        for (i in 0 until binding.discoveredContainer.childCount) {
            val hostCard = binding.discoveredContainer.getChildAt(i)
            hostCard.isEnabled = enabled
            hostCard.alpha = targetAlpha
        }
    }

    private fun applyPillBackground(
        view: View,
        @ColorRes fillColorRes: Int,
        @ColorRes strokeColorRes: Int,
        cornerRadiusDp: Float = 14f
    ) {
        val density = resources.displayMetrics.density
        val bg = GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            cornerRadius = cornerRadiusDp * density
            setColor(ContextCompat.getColor(this@MainActivity, fillColorRes))
            setStroke((1f * density).toInt().coerceAtLeast(1), ContextCompat.getColor(this@MainActivity, strokeColorRes))
        }
        view.background = bg
    }

    private fun updateConnectionStatus(message: String, state: ConnectionUiState) {
        binding.txtStatus.text = message
        val isConnecting = state == ConnectionUiState.CONNECTING
        binding.progressConnecting.visibility = if (isConnecting) View.VISIBLE else View.GONE
        binding.statusIndicatorDot.visibility = if (isConnecting) View.GONE else View.VISIBLE

        when (state) {
            ConnectionUiState.IDLE -> {
                binding.statusIndicatorDot.backgroundTintList =
                    ColorStateList.valueOf(ContextCompat.getColor(this, R.color.status_idle_dot))
                binding.txtStatus.setTextColor(ContextCompat.getColor(this, R.color.status_idle_text))
                applyPillBackground(binding.statusPillContainer, R.color.status_idle_bg, R.color.glass_stroke)
            }
            ConnectionUiState.CONNECTING -> {
                binding.txtStatus.setTextColor(ContextCompat.getColor(this, R.color.text_cyan_bright))
                applyPillBackground(binding.statusPillContainer, R.color.status_cyan_bg, R.color.status_cyan_stroke)
            }
            ConnectionUiState.CONNECTED -> {
                binding.statusIndicatorDot.backgroundTintList =
                    ColorStateList.valueOf(ContextCompat.getColor(this, R.color.status_emerald))
                binding.txtStatus.setTextColor(ContextCompat.getColor(this, R.color.status_emerald_text))
                applyPillBackground(binding.statusPillContainer, R.color.status_emerald_bg, R.color.status_emerald)
            }
            ConnectionUiState.ERROR -> {
                binding.statusIndicatorDot.backgroundTintList =
                    ColorStateList.valueOf(ContextCompat.getColor(this, R.color.status_rose))
                binding.txtStatus.setTextColor(ContextCompat.getColor(this, R.color.status_rose_text))
                applyPillBackground(binding.statusPillContainer, R.color.status_rose_bg, R.color.status_rose_stroke)
            }
        }
    }

    private fun showWarningBanner(message: String) {
        binding.txtWarning.text = message
        binding.warningBanner.visibility = View.VISIBLE
    }

    private fun hideWarningBanner() {
        binding.txtWarning.text = ""
        binding.warningBanner.visibility = View.GONE
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
        val newKeys = hosts.map { Triple(it.hostname, it.ipAddress, it.port) }
        if (hosts.isEmpty()) {
            if (renderedHostKeys.isNotEmpty() || binding.discoveredContainer.childCount > 0) {
                binding.discoveredContainer.removeAllViews()
            }
            renderedHostKeys = emptyList()
            binding.discoveredContainer.visibility = View.GONE
            binding.discoveredEmptyState.visibility = View.VISIBLE
            binding.txtDiscoveryBadge.text = getString(R.string.discovery_scanning_badge)
            binding.txtDiscoveryBadge.setTextColor(ContextCompat.getColor(this, R.color.brand_cyan))
            startRadarPulseIfNeeded()
            return
        }

        binding.discoveredEmptyState.visibility = View.GONE
        binding.discoveredContainer.visibility = View.VISIBLE
        binding.txtDiscoveryBadge.text = if (hosts.size == 1) {
            getString(R.string.discovery_found_one)
        } else {
            getString(R.string.discovery_found_many, hosts.size)
        }
        binding.txtDiscoveryBadge.setTextColor(ContextCompat.getColor(this, R.color.status_emerald))

        if (newKeys == renderedHostKeys) {
            return
        }
        renderedHostKeys = newKeys
        stopRadarPulse()
        binding.discoveredContainer.removeAllViews()

        for (host in hosts) {
            val cardBinding = ItemDiscoveredHostBinding.inflate(
                layoutInflater,
                binding.discoveredContainer,
                false
            )
            cardBinding.txtHostName.text = host.hostname
            cardBinding.txtHostAddress.text = "${host.ipAddress}:${host.port}"
            cardBinding.root.isEnabled = areConnectTriggersEnabled
            cardBinding.root.alpha = if (areConnectTriggersEnabled) 1.0f else 0.48f
            cardBinding.root.setOnClickListener {
                if (!areConnectTriggersEnabled) return@setOnClickListener
                binding.inputIpAddress.setText(host.ipAddress)
                getSharedPreferences("second_screen_prefs", Context.MODE_PRIVATE)
                    .edit().putString("last_lan_ip", host.ipAddress).apply()
                connectTo(host.ipAddress, host.port)
            }
            binding.discoveredContainer.addView(cardBinding.root)
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
            setConnectTriggersEnabled(false)
            binding.btnCancel.visibility = View.VISIBLE
            hideWarningBanner()

            val backoffs = listOf(1000L, 1500L, 2000L, 3000L, 4000L)
            val maxAttempts = 8
            var attempt = 0

            while (isActive && !userCancelled && attempt < maxAttempts) {
                attempt++
                val isRetry = attempt > 1

                withContext(Dispatchers.Main) {
                    if (isRetry) {
                        // Make sure status & cancel button are visible if stream dropped mid-session
                        showControlOverlay()
                        resetDockForDisconnectedState()
                        updateConnectionStatus(
                            getString(R.string.status_reconnecting_target, attempt, maxAttempts, host, port),
                            ConnectionUiState.CONNECTING
                        )
                    } else {
                        updateConnectionStatus(
                            getString(R.string.status_connecting_target, host, port),
                            ConnectionUiState.CONNECTING
                        )
                    }
                }

                try {
                    runStreamingSession(host, port)
                    // Reset attempt counter after a successful active session if it dropped unexpectedly
                    attempt = 0
                } catch (e: AuthFailedException) {
                    withContext(Dispatchers.Main) {
                        showControlOverlay()
                        updateConnectionStatus(
                            e.message ?: getString(R.string.status_auth_failed),
                            ConnectionUiState.ERROR
                        )
                    }
                    break // Never retry automatically with wrong PIN
                } catch (e: ClientIsolationException) {
                    // Yurt / Okul Wi-Fi Client Isolation detected!
                    withContext(Dispatchers.Main) {
                        showControlOverlay()
                        showWarningBanner(e.message ?: getString(R.string.status_client_isolation))
                        updateConnectionStatus(
                            getString(R.string.status_client_isolation),
                            ConnectionUiState.ERROR
                        )
                    }
                    break // Stop retry loop
                } catch (e: CancellationException) {
                    break
                } catch (e: Exception) {
                    withContext(Dispatchers.Main) {
                        showControlOverlay()
                        updateConnectionStatus(
                            getString(R.string.status_connect_failed, e.message ?: getString(R.string.status_error)),
                            ConnectionUiState.ERROR
                        )
                    }
                }

                if (userCancelled || !isActive) break

                // Exponential backoff before next attempt
                val backoffMs = backoffs[((attempt - 1).coerceAtLeast(0).coerceAtMost(backoffs.size - 1))]
                delay(backoffMs)
            }

            // Cleanup UI after session terminates
            withContext(Dispatchers.Main) {
                setConnectTriggersEnabled(true)
                binding.btnCancel.visibility = View.GONE
                if (binding.controlOverlay.visibility != View.VISIBLE) {
                    showControlOverlay()
                }
                resetDockForDisconnectedState()
                if (isKeyboardVisible) {
                    toggleSoftKeyboard()
                }
            }
        }
    }

    private fun cancelActiveSession() {
        userCancelled = true
        isStreamingActive = false
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
        updateConnectionStatus(getString(R.string.status_stopped), ConnectionUiState.IDLE)
        hideWarningBanner()
        setConnectTriggersEnabled(true)
        binding.btnCancel.visibility = View.GONE
        showControlOverlay()
        resetDockForDisconnectedState()
        if (isKeyboardVisible) {
            toggleSoftKeyboard()
        }
    }

    private suspend fun runStreamingSession(host: String, port: Int) = kotlinx.coroutines.coroutineScope {
        val transport = SocketTransport(host, port, connectTimeoutMs = 2500)
        activeTransport = transport
        latestSpsPps = null
        acquireWifiLock()

        try {
            // 1. Connect socket (throws ClientIsolationException if 2.5s timeout on LAN)
            transport.connect()

            // 2. Query screen metrics
            val (nativeWidth, nativeHeight, refreshRate) = queryNativeDisplaySpec()
            val deviceName = Build.MODEL ?: "Android Tablet"

            val isUsb = host == "127.0.0.1" || host == "localhost"
            val featureFlags = ProtocolConstants.FEATURE_FLAG_TOUCH.toInt() or ProtocolConstants.FEATURE_FLAG_LOW_LATENCY_DECODER.toInt()

            val prefs = getSharedPreferences("second_screen_prefs", Context.MODE_PRIVATE)
            val bitrateMbps = prefs.getInt("preferred_bitrate_mbps", 15)
            val preferredBitrateKbps = bitrateMbps * 1000
            val pinCode = binding.inputPin.text?.toString()?.trim() ?: ""
            if (pinCode.isNotEmpty()) {
                prefs.edit().putString("last_pin", pinCode).apply()
            }

            // 3. Send HELLO
            val hello = HelloMessage(
                nativeWidth = nativeWidth,
                nativeHeight = nativeHeight,
                maxFps = refreshRate,
                preferredBitrateKbps = preferredBitrateKbps,
                featureFlags = featureFlags.toByte(),
                deviceName = deviceName,
                pinCode = pinCode
            )
            val helloPayload = hello.toByteArray()
            val helloHeader = FrameHeader.create(MessageType.HELLO, helloPayload.size.toLong(), sequence = 1)
            transport.sendPacket(helloHeader, helloPayload)

            withContext(Dispatchers.Main) {
                updateConnectionStatus(getString(R.string.status_handshake), ConnectionUiState.CONNECTING)
            }

            // 4. Wait for HELLO_ACK
            val (ackHeader, ackPayload) = transport.receivePacket()
            if (ackHeader.msgType != MessageType.HELLO_ACK) {
                throw IllegalStateException("Expected HELLO_ACK, got ${ackHeader.msgType}")
            }
            val ack = HelloAckMessage.readPayloadFrom(ByteBuffer.wrap(ackPayload))
            if (ack.status == ProtocolConstants.HELLO_STATUS_AUTH_FAILED) {
                throw AuthFailedException(getString(R.string.status_auth_failed))
            }
            lastStreamWidth = ack.streamWidth
            lastStreamHeight = ack.streamHeight

            withContext(Dispatchers.Main) {
                updateConnectionStatus(
                    getString(
                        R.string.status_connected_detail,
                        ack.streamWidth,
                        ack.streamHeight,
                        ack.targetFps,
                        ack.bitrateBps / 1_000_000
                    ),
                    ConnectionUiState.CONNECTED
                )
                hideControlOverlay()
                val modeLabel = when {
                    isUsb -> "USB"
                    host == "192.168.137.1" -> "Hotspot"
                    else -> "Wi-Fi: $host"
                }
                binding.txtBanner.text = "Yayın: ${ack.streamWidth}x${ack.streamHeight} [$modeLabel]"
                // Keep statistics menu hidden by default so it does not cover the screen; user opens it via Stats button
                isStreamingActive = true
                dockStateMachine.onStreamStarted()
                applyDockMode(DockMode.EXPANDED, animate = false)
                setStatsMenuVisible(false, animate = false)
            }

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

            // 5. HUD update loop (every 300 ms, only while HUD is open)
            val hudJob = launch(Dispatchers.Main) {
                while (isActive) {
                    if (isStatsMenuOpen) {
                        updateHudTelemetry(telemetry.getSnapshot())
                    }
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
            val sequence = java.util.concurrent.atomic.AtomicInteger(1000)
            val lastKeyframeReqMs = java.util.concurrent.atomic.AtomicLong(0L)

            // Called from both the reader and the decoder thread.
            val maybeRequestKeyframe: (Long) -> Unit = { lastGoodFrame ->
                val now = System.currentTimeMillis()
                val prev = lastKeyframeReqMs.get()
                // Rate limit: at most 1 request per 500 ms; CAS so two threads never both send.
                if (now - prev >= 500 && lastKeyframeReqMs.compareAndSet(prev, now)) {
                    telemetry.onIdrRequested()
                    launch(Dispatchers.IO) {
                        try {
                            val req = KeyframeReqMessage(lastGoodFrame)
                            val reqPayload = req.toByteArray()
                            val reqHeader = FrameHeader.create(
                                MessageType.KEYFRAME_REQ,
                                reqPayload.size.toLong(),
                                sequence = sequence.getAndIncrement()
                            )
                            transport.sendPacket(reqHeader, reqPayload)
                        } catch (_: Exception) {}
                    }
                }
            }

            activeSurfaceResetHandler = {
                waitingForKeyframe.set(true)
                maybeRequestKeyframe(0L)
            }

            // ---- bounded hand-off between reader and decoder ----
            val decodeQueue = Channel<DecodeItem>(capacity = 4)
            val decodeDispatcher = java.util.concurrent.Executors
                .newSingleThreadExecutor { r -> Thread(r, "AreasDecode") }
                .asCoroutineDispatcher()

            // Reader thread only. Never blocks: if the decoder fell behind, resync instead of waiting.
            val enqueue: (DecodeItem) -> Unit = { item ->
                if (!decodeQueue.trySend(item).isSuccess) {
                    var droppedFrames = if (item is DecodeItem.Frame) 1 else 0
                    var drainedConfig = false
                    while (true) {
                        val drained = decodeQueue.tryReceive().getOrNull() ?: break
                        if (drained is DecodeItem.Config) {
                            drainedConfig = true
                        } else {
                            droppedFrames++
                        }
                    }
                    repeat(droppedFrames) { telemetry.onFrameDropped() }
                    val cachedSpsPps = latestSpsPps
                    if (item is DecodeItem.Frame && item.isKeyframe) {
                        if (cachedSpsPps != null) {
                            decodeQueue.trySend(DecodeItem.Config(cachedSpsPps))
                        }
                        decodeQueue.trySend(item) // fresh IDR resyncs everything
                    } else if (item is DecodeItem.Config) {
                        decodeQueue.trySend(item)
                    } else {
                        if (cachedSpsPps != null && (drainedConfig || videoDecoder?.isConfigured != true)) {
                            decodeQueue.trySend(DecodeItem.Config(cachedSpsPps))
                        }
                        waitingForKeyframe.set(true)
                        maybeRequestKeyframe((item as DecodeItem.Frame).frameNumber)
                    }
                }
            }

            // ---- reader: socket + Ping/Pong only, off the main thread ----
            val readerJob = launch(Dispatchers.IO) {
                while (isActive && transport.isConnected) {
                    val (pktHeader, pktPayload) = transport.receivePacket()
                    when (pktHeader.msgType) {
                        MessageType.VIDEO_CONFIG -> {
                            val config = VideoConfigMessage.readPayloadFrom(pktPayload)
                            latestSpsPps = config.spsPpsData
                            enqueue(DecodeItem.Config(config.spsPpsData))
                        }
                        MessageType.VIDEO_FRAME -> {
                            val frame = VideoFrameMessage.readPayloadFrom(pktPayload, pktHeader.flags)
                            if (frame.isKeyframe) {
                                waitingForKeyframe.set(false)
                            } else if (waitingForKeyframe.get()) {
                                telemetry.onFrameDropped()
                                continue
                            }
                            enqueue(
                                DecodeItem.Frame(
                                    nal = frame.nalData,
                                    ptsUs = pktHeader.timestampUs,
                                    isKeyframe = frame.isKeyframe,
                                    frameNumber = frame.frameNumber,
                                    captureUs = frame.captureDurationUs,
                                    encodeUs = frame.encodeDurationUs,
                                    wireBytes = pktPayload.size
                                )
                            )
                        }
                        MessageType.PING -> {
                            val ping = PingMessage.readPayloadFrom(ByteBuffer.wrap(pktPayload))
                            val pong = telemetry.createPong(ping)
                            val pongPayload = pong.toByteArray()
                            val pongHeader = FrameHeader.create(
                                MessageType.PONG,
                                pongPayload.size.toLong(),
                                sequence = sequence.getAndIncrement()
                            )
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
            }

            // ---- decoder: dedicated thread ----
            val decodeJob = launch(decodeDispatcher) {
                android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_DISPLAY)
                for (item in decodeQueue) {
                    when (item) {
                        is DecodeItem.Config -> {
                            latestSpsPps = item.spsPps
                            try {
                                videoDecoder?.configure(lastStreamWidth, lastStreamHeight, item.spsPps)
                            } catch (e: Exception) {
                                Log.e("StreamDecode", "configure failed", e)
                                waitingForKeyframe.set(true)
                                maybeRequestKeyframe(0L)
                            }
                        }
                        is DecodeItem.Frame -> {
                            val decoder = videoDecoder
                            if (decoder != null && !decoder.isConfigured) {
                                val spsPps = latestSpsPps
                                if (spsPps != null) {
                                    val configuredNow = try {
                                        decoder.configure(lastStreamWidth, lastStreamHeight, spsPps)
                                        decoder.isConfigured
                                    } catch (e: Exception) {
                                        Log.e("StreamDecode", "auto-configure failed", e)
                                        false
                                    }
                                    if (!configuredNow || !item.isKeyframe) {
                                        waitingForKeyframe.set(true)
                                        telemetry.onFrameDropped()
                                        maybeRequestKeyframe(item.frameNumber)
                                        continue
                                    }
                                } else {
                                    waitingForKeyframe.set(true)
                                    telemetry.onFrameDropped()
                                    maybeRequestKeyframe(item.frameNumber)
                                    continue
                                }
                            }

                            if (item.isKeyframe) {
                                waitingForKeyframe.set(false)
                            } else if (waitingForKeyframe.get()) {
                                telemetry.onFrameDropped()
                                continue
                            }

                            val queueWaitNs = (System.nanoTime() - item.enqueuedAtNs).coerceAtLeast(0L)
                            val t0 = System.nanoTime()
                            val result = decoder?.decodeFrame(item.nal, item.ptsUs, item.isKeyframe)
                                ?: DecodeResult.OK // surface not available yet: same as before
                            val decNs = System.nanoTime() - t0

                            if (result == DecodeResult.OK) {
                                val effectiveDecNs = decoder?.lastRenderDecodeNs?.takeIf { it > 0L } ?: decNs
                                telemetry.onFrameDecoded(item.captureUs, item.encodeUs, effectiveDecNs, item.wireBytes, queueWaitNs)
                            } else {
                                if (result == DecodeResult.NO_INPUT) telemetry.onInputUnavailable()
                                val currentDecoder = videoDecoder
                                if (result == DecodeResult.ERROR && currentDecoder != null && !currentDecoder.isConfigured) {
                                    val spsPps = latestSpsPps
                                    if (spsPps != null) {
                                        try {
                                            currentDecoder.configure(lastStreamWidth, lastStreamHeight, spsPps)
                                        } catch (e: Exception) {
                                            Log.e("StreamDecode", "reconfigure after error failed", e)
                                        }
                                    }
                                }
                                waitingForKeyframe.set(true)
                                telemetry.onFrameDropped()
                                maybeRequestKeyframe(item.frameNumber)
                            }
                        }
                    }
                }
            }

            try {
                readerJob.join()
            } finally {
                isStreamingActive = false
                activeSurfaceResetHandler = null
                latestSpsPps = null
                withContext(NonCancellable + Dispatchers.Main.immediate) {
                    resetDockForDisconnectedState()
                }
                readerJob.cancel()
                touchSenderJob.cancel()
                keyboardSenderJob.cancel() // see note below
                while (touchChannel.tryReceive().isSuccess) {}
                hudJob.cancel()
                pingJob.cancel()
                decodeQueue.close()
                withContext(NonCancellable) { decodeJob.cancelAndJoin() }
                decodeDispatcher.close()
            }
        } finally {
            if (activeTransport === transport) {
                activeTransport = null
            }
            releaseWifiLock()
            transport.close()
        }
    }

    private fun updateHudTelemetry(s: com.secondscreen.client.telemetry.TelemetrySnapshot) {
        binding.txtHudFpsValue.text = s.fps.toString()
        binding.txtHudBitrateValue.text = String.format(Locale.US, "%.1f", s.bitrateMbps)
        binding.txtHudTotalValue.text = String.format(Locale.US, "%.1f", s.estimatedPipelineMs)
        binding.txtHudCaptureValue.text = String.format(Locale.US, "%.1f ms", s.captureMs)
        binding.txtHudEncodeValue.text = String.format(Locale.US, "%.1f ms", s.encodeMs)
        binding.txtHudNetworkValue.text = String.format(Locale.US, "%.1f ms", s.networkRttDiv2Ms)
        binding.txtHudQueueValue.text = String.format(Locale.US, "%.1f ms", s.queueWaitMs)
        binding.txtHudDecodeValue.text = String.format(Locale.US, "%.1f ms", s.decodeMs)

        val healthColorRes = when {
            s.fps == 0 && s.estimatedPipelineMs <= 0.0 -> R.color.status_idle_dot
            s.estimatedPipelineMs < 25.0 -> R.color.status_emerald
            s.estimatedPipelineMs < 50.0 -> R.color.brand_cyan
            s.estimatedPipelineMs < 85.0 -> R.color.status_amber
            else -> R.color.status_rose
        }
        val healthColor = ContextCompat.getColor(this, healthColorRes)
        binding.hudHealthDot.backgroundTintList = ColorStateList.valueOf(healthColor)
        binding.txtHudTotalValue.setTextColor(
            if (healthColorRes == R.color.status_idle_dot) {
                ContextCompat.getColor(this, R.color.brand_violet_soft)
            } else {
                healthColor
            }
        )

        updateDiagBadge(binding.txtDiagDrop, getString(R.string.hud_diag_drop, s.droppedFrames), s.droppedFrames)
        updateDiagBadge(binding.txtDiagIdr, getString(R.string.hud_diag_idr, s.idrRequests), s.idrRequests)
        updateDiagBadge(binding.txtDiagNoInput, getString(R.string.hud_diag_no_input, s.inputUnavailable), s.inputUnavailable)
    }

    private fun updateDiagBadge(badge: TextView, label: String, count: Long) {
        if (badge.text.toString() != label) {
            badge.text = label
        }
        val isWarningActive = count > 0L
        if ((badge.tag as? Boolean) != isWarningActive) {
            badge.tag = isWarningActive
            if (isWarningActive) {
                badge.setTextColor(ContextCompat.getColor(this, R.color.status_amber_text))
                applyPillBackground(badge, R.color.status_amber_bg, R.color.status_amber_stroke, cornerRadiusDp = 99f)
            } else {
                badge.setTextColor(ContextCompat.getColor(this, R.color.text_secondary))
                badge.setBackgroundResource(R.drawable.bg_diag_pill)
            }
        }
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        isSurfaceAvailable = true
        val decoder = VideoDecoder(holder.surface)
        val spsPps = latestSpsPps
        if (isStreamingActive && spsPps != null && lastStreamWidth > 0 && lastStreamHeight > 0) {
            try {
                decoder.configure(lastStreamWidth, lastStreamHeight, spsPps)
            } catch (e: Exception) {
                Log.e("StreamDecode", "surfaceCreated configure failed", e)
            }
        }
        videoDecoder = decoder
        if (isStreamingActive) {
            activeSurfaceResetHandler?.invoke()
        }
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        isSurfaceAvailable = false
        videoDecoder?.close()
        videoDecoder = null
    }

    override fun onStart() {
        super.onStart()
        if (isStreamingActive) {
            scheduleDockAutoHide()
        } else {
            startRadarPulseIfNeeded()
        }
    }

    override fun onStop() {
        super.onStop()
        isTouchGestureRoutedToVideo = false
        cancelDockAutoHideTimer()
        stopRadarPulse()
    }

    override fun onDestroy() {
        super.onDestroy()
        cancelDockAutoHideTimer()
        stopRadarPulse()
        binding.hudOverlay.animate().cancel()
        binding.floatingControlDock.animate().cancel()
        binding.dockContentGroup.animate().cancel()
        dockStateMachine.onStreamStopped()
        releaseWifiLock()
        discoveryListener.stop()
        sessionJob?.cancel()
        videoDecoder?.close()
    }

    private companion object {
        private const val HUD_ANIM_IN_MS = 180L
        private const val HUD_ANIM_OUT_MS = 140L
        private const val DOCK_FADE_IN_MS = 180L
        private const val DOCK_CONTENT_FADE_OUT_MS = 140L
        private const val DOCK_FADE_OUT_MS = 250L
        private const val DOCK_COLLAPSED_ALPHA = 0.7f
    }
}
