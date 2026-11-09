package com.secondscreen.client.input

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs

class TouchCoordinateMapperTest {

    @Test
    fun testExactAspectRatioMapping() {
        // Tablet: 1920x1200 (16:10), Stream: 1920x1200 (16:10) -> No bars
        val topLeft = TouchCoordinateMapper.mapTouchToNormalized(0f, 0f, 1920, 1200, 1920, 1200)
        assertNotNull(topLeft)
        assertEquals(0, topLeft!!.first)
        assertEquals(0, topLeft.second)

        val bottomRight = TouchCoordinateMapper.mapTouchToNormalized(1920f, 1200f, 1920, 1200, 1920, 1200)
        assertNotNull(bottomRight)
        assertEquals(65535, bottomRight!!.first)
        assertEquals(65535, bottomRight.second)

        val center = TouchCoordinateMapper.mapTouchToNormalized(960f, 600f, 1920, 1200, 1920, 1200)
        assertNotNull(center)
        assertTrue(abs(center!!.first - 32767) <= 1)
        assertTrue(abs(center.second - 32767) <= 1)
    }

    @Test
    fun testLetterboxMapping() {
        // Tablet: 1920x1200 (16:10), Stream: 1920x1080 (16:9)
        // Video height = 1920 / (16/9) = 1080. OffsetY = (1200 - 1080) / 2 = 60px black bar top & bottom.

        // Touch on top black bar (Y = 30) -> Should be ignored (null)
        val touchTopBar = TouchCoordinateMapper.mapTouchToNormalized(960f, 30f, 1920, 1200, 1920, 1080)
        assertNull(touchTopBar)

        // Touch on bottom black bar (Y = 1170) -> Should be ignored (null)
        val touchBottomBar = TouchCoordinateMapper.mapTouchToNormalized(960f, 1170f, 1920, 1200, 1920, 1080)
        assertNull(touchBottomBar)

        // Touch at start of video area (Y = 60) -> NormY = 0
        val touchVideoTop = TouchCoordinateMapper.mapTouchToNormalized(0f, 60f, 1920, 1200, 1920, 1080)
        assertNotNull(touchVideoTop)
        assertEquals(0, touchVideoTop!!.first)
        assertEquals(0, touchVideoTop.second)

        // Touch at end of video area (Y = 1140) -> NormY = 65535
        val touchVideoBottom = TouchCoordinateMapper.mapTouchToNormalized(1920f, 1140f, 1920, 1200, 1920, 1080)
        assertNotNull(touchVideoBottom)
        assertEquals(65535, touchVideoBottom!!.first)
        assertEquals(65535, touchVideoBottom.second)
    }
}

