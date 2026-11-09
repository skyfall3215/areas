package com.secondscreen.client.input

object TouchCoordinateMapper {
    const val MAX_NORMALIZED = 65535

    /**
     * Maps physical tablet screen touch coordinates to normalized 0..65535 coordinates,
     * accounting for aspect ratio and letterbox/pillarbox bars.
     * Returns null if touch is within black letterbox/pillarbox borders.
     */
    fun mapTouchToNormalized(
        touchX: Float,
        touchY: Float,
        viewWidth: Int,
        viewHeight: Int,
        streamWidth: Int,
        streamHeight: Int
    ): Pair<Int, Int>? {
        if (viewWidth <= 0 || viewHeight <= 0 || streamWidth <= 0 || streamHeight <= 0) return null

        val viewAspect = viewWidth.toFloat() / viewHeight.toFloat()
        val streamAspect = streamWidth.toFloat() / streamHeight.toFloat()

        val videoWidth: Float
        val videoHeight: Float
        val offsetX: Float
        val offsetY: Float

        if (viewAspect > streamAspect) {
            // Pillarbox: black bars on left and right
            videoHeight = viewHeight.toFloat()
            videoWidth = videoHeight * streamAspect
            offsetX = (viewWidth - videoWidth) / 2f
            offsetY = 0f
        } else {
            // Letterbox: black bars on top and bottom
            videoWidth = viewWidth.toFloat()
            videoHeight = videoWidth / streamAspect
            offsetX = 0f
            offsetY = (viewHeight - videoHeight) / 2f
        }

        if (touchX < offsetX || touchX > offsetX + videoWidth ||
            touchY < offsetY || touchY > offsetY + videoHeight) {
            return null
        }

        val relX = (touchX - offsetX) / videoWidth
        val relY = (touchY - offsetY) / videoHeight

        val normX = (relX * MAX_NORMALIZED).toInt().coerceIn(0, MAX_NORMALIZED)
        val normY = (relY * MAX_NORMALIZED).toInt().coerceIn(0, MAX_NORMALIZED)

        return Pair(normX, normY)
    }
}

