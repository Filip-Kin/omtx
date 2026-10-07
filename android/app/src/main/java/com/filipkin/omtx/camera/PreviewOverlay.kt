package com.filipkin.omtx.camera

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.PointF
import android.util.AttributeSet
import android.view.View

/**
 * Drawn over the preview, never sent: full-screen tally tint, rule-of-thirds grid, focus marker.
 * `focusPoint` is 0..1 across this view.
 */
class PreviewOverlay @JvmOverloads constructor(
    context: Context, attrs: AttributeSet? = null,
) : View(context, attrs) {
    private val dp = resources.displayMetrics.density

    var tint: Int = Color.TRANSPARENT
        set(value) { if (field != value) { field = value; invalidate() } }
    var grid: Boolean = false
        set(value) { if (field != value) { field = value; invalidate() } }
    var focusPoint: PointF? = null
        set(value) { field = value; invalidate() }
    var focusLocked: Boolean = false
        set(value) { if (field != value) { field = value; invalidate() } }

    private val gridPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.argb(0x80, 0xFF, 0xFF, 0xFF)
        strokeWidth = 1f * dp
    }
    private val focusPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 2f * dp
    }

    /** Taps reach this view through a touch listener (focus point, pinch zoom). */
    override fun performClick(): Boolean = super.performClick()

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        if (Color.alpha(tint) != 0) canvas.drawColor(tint)
        if (grid) {
            for (i in 1..2) {
                canvas.drawLine(w * i / 3f, 0f, w * i / 3f, h, gridPaint)
                canvas.drawLine(0f, h * i / 3f, w, h * i / 3f, gridPaint)
            }
        }
        focusPoint?.let { p ->
            focusPaint.color = if (focusLocked) LOCK_COLOR else Color.WHITE
            val r = 36f * dp
            val cx = p.x * w
            val cy = p.y * h
            canvas.drawRect(cx - r, cy - r, cx + r, cy + r, focusPaint)
        }
    }

    companion object {
        val LOCK_COLOR = Color.rgb(0xFF, 0xC1, 0x07)
    }
}
