package com.secondscreen.client.ui

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DockAutoHideStateMachineTest {

    @Test
    fun streamStarted_setsExpanded_andSchedulesCollapseTimeout() {
        val sm = DockAutoHideStateMachine()

        val mode = sm.onStreamStarted()

        assertEquals(DockMode.EXPANDED, mode)
        assertEquals(7_000L, sm.nextTimeoutDelayMs())
    }

    @Test
    fun timeoutFromExpanded_collapsesDock_andSchedulesHideTimeout() {
        val sm = DockAutoHideStateMachine()
        sm.onStreamStarted()

        val mode = sm.onInactivityTimeout()

        assertEquals(DockMode.COLLAPSED, mode)
        assertEquals(7_000L, sm.nextTimeoutDelayMs())
    }

    @Test
    fun timeoutFromCollapsed_hidesDock_andClearsTimeout() {
        val sm = DockAutoHideStateMachine()
        sm.onStreamStarted()
        sm.onInactivityTimeout()

        val mode = sm.onInactivityTimeout()

        assertEquals(DockMode.HIDDEN, mode)
        assertNull(sm.nextTimeoutDelayMs())
    }

    @Test
    fun touchWhileHidden_wakesToCollapsed_notExpanded() {
        val sm = DockAutoHideStateMachine()
        sm.onStreamStarted()
        sm.onInactivityTimeout()
        sm.onInactivityTimeout()
        assertEquals(DockMode.HIDDEN, sm.mode)

        val mode = sm.onUserTouch()

        assertEquals(DockMode.COLLAPSED, mode)
        assertEquals(7_000L, sm.nextTimeoutDelayMs())
    }

    @Test
    fun touchWhileExpanded_orCollapsed_keepsCurrentMode() {
        val sm = DockAutoHideStateMachine()
        sm.onStreamStarted()

        assertEquals(DockMode.EXPANDED, sm.onUserTouch())
        assertEquals(7_000L, sm.nextTimeoutDelayMs())

        sm.onInactivityTimeout()
        assertEquals(DockMode.COLLAPSED, sm.onUserTouch())
        assertEquals(7_000L, sm.nextTimeoutDelayMs())
    }

    @Test
    fun statsMenuOpen_blocksTimeouts_untilClosed() {
        val sm = DockAutoHideStateMachine()
        sm.onStreamStarted()

        sm.onStatsMenuVisibilityChanged(true)
        assertNull(sm.nextTimeoutDelayMs())
        assertEquals(DockMode.EXPANDED, sm.onInactivityTimeout())

        sm.onStatsMenuVisibilityChanged(false)
        assertEquals(7_000L, sm.nextTimeoutDelayMs())
        assertEquals(DockMode.COLLAPSED, sm.onInactivityTimeout())
    }

    @Test
    fun statsMenuOpen_alsoBlocksHideTimeout_whenCollapsed() {
        val sm = DockAutoHideStateMachine()
        sm.onStreamStarted()
        sm.onToggleCollapseClicked()
        assertEquals(DockMode.COLLAPSED, sm.mode)

        sm.onStatsMenuVisibilityChanged(true)
        assertNull(sm.nextTimeoutDelayMs())
        assertEquals(DockMode.COLLAPSED, sm.onInactivityTimeout())

        sm.onStatsMenuVisibilityChanged(false)
        assertEquals(7_000L, sm.nextTimeoutDelayMs())
        assertEquals(DockMode.HIDDEN, sm.onInactivityTimeout())
    }

    @Test
    fun manualCollapse_movesToCollapsed_andManualExpand_movesToExpanded() {
        val sm = DockAutoHideStateMachine()
        sm.onStreamStarted()

        assertEquals(DockMode.COLLAPSED, sm.onToggleCollapseClicked())
        assertEquals(7_000L, sm.nextTimeoutDelayMs())

        assertEquals(DockMode.EXPANDED, sm.onToggleCollapseClicked())
        assertEquals(7_000L, sm.nextTimeoutDelayMs())
    }

    @Test
    fun streamStopped_resetsToHidden_andDisablesTimeout_andIgnoresTouches() {
        val sm = DockAutoHideStateMachine()
        sm.onStreamStarted()
        sm.onStatsMenuVisibilityChanged(true)

        val stoppedMode = sm.onStreamStopped()

        assertEquals(DockMode.HIDDEN, stoppedMode)
        assertNull(sm.nextTimeoutDelayMs())
        assertEquals(DockMode.HIDDEN, sm.onUserTouch())
        assertEquals(DockMode.HIDDEN, sm.onInactivityTimeout())
        assertEquals(DockMode.EXPANDED, sm.onStreamStarted())
        assertEquals(7_000L, sm.nextTimeoutDelayMs())
    }

    @Test
    fun initialState_isHidden_andIgnoresInputsBeforeStreamStarts() {
        val sm = DockAutoHideStateMachine()

        assertEquals(DockMode.HIDDEN, sm.mode)
        assertNull(sm.nextTimeoutDelayMs())
        assertEquals(DockMode.HIDDEN, sm.onUserTouch())
        assertEquals(DockMode.HIDDEN, sm.onInactivityTimeout())
        assertEquals(DockMode.HIDDEN, sm.onToggleCollapseClicked())
    }

    @Test
    fun timeoutWhileHidden_remainsHidden() {
        val sm = DockAutoHideStateMachine()
        sm.onStreamStarted()
        sm.onInactivityTimeout()
        sm.onInactivityTimeout()
        assertEquals(DockMode.HIDDEN, sm.mode)

        assertEquals(DockMode.HIDDEN, sm.onInactivityTimeout())
        assertNull(sm.nextTimeoutDelayMs())
    }

    @Test
    fun customDelays_areReturnedPerStage() {
        val sm = DockAutoHideStateMachine(collapseDelayMs = 3_000L, hideDelayMs = 5_000L)
        sm.onStreamStarted()

        assertEquals(3_000L, sm.nextTimeoutDelayMs())
        sm.onInactivityTimeout()
        assertEquals(5_000L, sm.nextTimeoutDelayMs())
    }

    @Test
    fun multipleWakeAndHideCycles_workConsistently() {
        val sm = DockAutoHideStateMachine()
        sm.onStreamStarted()

        sm.onInactivityTimeout()
        sm.onInactivityTimeout()
        assertEquals(DockMode.HIDDEN, sm.mode)

        assertEquals(DockMode.COLLAPSED, sm.onUserTouch())
        assertEquals(7_000L, sm.nextTimeoutDelayMs())
        assertEquals(DockMode.HIDDEN, sm.onInactivityTimeout())
        assertNull(sm.nextTimeoutDelayMs())

        assertEquals(DockMode.COLLAPSED, sm.onUserTouch())
        assertEquals(DockMode.EXPANDED, sm.onToggleCollapseClicked())
        assertEquals(7_000L, sm.nextTimeoutDelayMs())
    }
}

