package com.filipkin.omtx.camera

import android.content.Context
import android.util.AttributeSet
import android.widget.FrameLayout

/**
 * FrameLayout whose content area (inside its padding) keeps a fixed aspect ratio, fitted
 * inside the space it is given. The padding is the tally border.
 */
class AspectFrameLayout @JvmOverloads constructor(
    context: Context, attrs: AttributeSet? = null,
) : FrameLayout(context, attrs) {
    var aspect: Double = 16.0 / 9.0
        set(value) { field = value; requestLayout() }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val maxW = MeasureSpec.getSize(widthMeasureSpec)
        val maxH = MeasureSpec.getSize(heightMeasureSpec)
        val padW = paddingLeft + paddingRight
        val padH = paddingTop + paddingBottom
        var innerW = (maxW - padW).coerceAtLeast(0)
        var innerH = (innerW / aspect).toInt()
        if (innerH > maxH - padH) {
            innerH = (maxH - padH).coerceAtLeast(0)
            innerW = (innerH * aspect).toInt()
        }
        super.onMeasure(
            MeasureSpec.makeMeasureSpec(innerW + padW, MeasureSpec.EXACTLY),
            MeasureSpec.makeMeasureSpec(innerH + padH, MeasureSpec.EXACTLY),
        )
    }
}
