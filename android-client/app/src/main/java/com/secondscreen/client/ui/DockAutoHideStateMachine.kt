package com.secondscreen.client.ui

/**
 * Visibility states for the floating tablet control dock during a stream.
 */
enum class DockMode {
    EXPANDED,
    COLLAPSED,
    HIDDEN
}

/**
 * Tracks two-stage auto-hide transitions (expanded -> collapsed -> hidden) for the
 * floating control dock, keeping state paused whenever the statistics HUD is open.
 */
class DockAutoHideStateMachine(
    val collapseDelayMs: Long = DEFAULT_INACTIVITY_DELAY_MS,
    val hideDelayMs: Long = DEFAULT_INACTIVITY_DELAY_MS
) {
    var mode: DockMode = DockMode.HIDDEN
        private set

    var isStreamingActive: Boolean = false
        private set

    var isStatsMenuOpen: Boolean = false
        private set

    fun onStreamStarted(): DockMode {
        isStreamingActive = true
        isStatsMenuOpen = false
        mode = DockMode.EXPANDED
        return mode
    }

    fun onStreamStopped(): DockMode {
        isStreamingActive = false
        isStatsMenuOpen = false
        mode = DockMode.HIDDEN
        return mode
    }

    fun onUserTouch(): DockMode {
        if (!isStreamingActive) {
            return mode
        }
        // Only reveal the compact toggle handle so regular trackpad touches do not expand the full bar.
        if (mode == DockMode.HIDDEN) {
            mode = DockMode.COLLAPSED
        }
        return mode
    }

    fun onToggleCollapseClicked(): DockMode {
        if (!isStreamingActive) {
            return DockMode.HIDDEN
        }
        mode = if (mode == DockMode.EXPANDED) DockMode.COLLAPSED else DockMode.EXPANDED
        return mode
    }

    fun onStatsMenuVisibilityChanged(open: Boolean): DockMode {
        isStatsMenuOpen = open
        return mode
    }

    fun onInactivityTimeout(): DockMode {
        if (!isStreamingActive || isStatsMenuOpen) {
            return mode
        }
        mode = when (mode) {
            DockMode.EXPANDED -> DockMode.COLLAPSED
            DockMode.COLLAPSED -> DockMode.HIDDEN
            DockMode.HIDDEN -> DockMode.HIDDEN
        }
        return mode
    }

    fun nextTimeoutDelayMs(): Long? {
        if (!isStreamingActive || isStatsMenuOpen) {
            return null
        }
        return when (mode) {
            DockMode.EXPANDED -> collapseDelayMs
            DockMode.COLLAPSED -> hideDelayMs
            DockMode.HIDDEN -> null
        }
    }

    companion object {
        const val DEFAULT_INACTIVITY_DELAY_MS = 7_000L
    }
}

