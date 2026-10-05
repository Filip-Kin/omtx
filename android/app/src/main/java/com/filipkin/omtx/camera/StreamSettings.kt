package com.filipkin.omtx.camera

import android.content.Context
import com.filipkin.omtx.core.VideoCodec

data class StreamSettings(
    val sourceName: String = DEFAULT_SOURCE_NAME,
    val width: Int = 1920,
    val height: Int = 1080,
    val fps: Int = 30,
    val codec: VideoCodec = VideoCodec.H264,
    val maxBitrateMbps: Int = 10,
) {
    val maxBitrateBps: Int get() = maxBitrateMbps * 1_000_000

    fun save(ctx: Context) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString("sourceName", sourceName)
            .putInt("width", width)
            .putInt("height", height)
            .putInt("fps", fps)
            .putString("codec", codec.name)
            .putInt("maxBitrateMbps", maxBitrateMbps)
            .apply()
    }

    companion object {
        const val PREFS = "omtx"
        const val DEFAULT_SOURCE_NAME = "Camera"
        val RESOLUTIONS = listOf(1920 to 1080, 1280 to 720)
        val FRAME_RATES = listOf(30, 60)
        val BITRATES_MBPS = listOf(4, 6, 8, 10, 12, 15, 20, 25, 30)

        fun load(ctx: Context): StreamSettings {
            val p = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            val d = StreamSettings()
            val w = p.getInt("width", d.width)
            val h = p.getInt("height", d.height)
            val res = if (RESOLUTIONS.contains(w to h)) w to h else d.width to d.height
            return StreamSettings(
                sourceName = p.getString("sourceName", null)?.takeIf { it.isNotBlank() } ?: d.sourceName,
                width = res.first,
                height = res.second,
                fps = p.getInt("fps", d.fps).takeIf { it in FRAME_RATES } ?: d.fps,
                codec = runCatching { VideoCodec.valueOf(p.getString("codec", d.codec.name)!!) }.getOrDefault(d.codec),
                maxBitrateMbps = p.getInt("maxBitrateMbps", d.maxBitrateMbps).takeIf { it in BITRATES_MBPS } ?: d.maxBitrateMbps,
            )
        }
    }
}
