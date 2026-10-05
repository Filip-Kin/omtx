package com.filipkin.omtx.camera

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecInfo.CodecCapabilities
import android.media.MediaCodecInfo.CodecProfileLevel
import android.media.MediaCodecInfo.EncoderCapabilities
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.view.Surface
import com.filipkin.omtx.core.AnnexB
import com.filipkin.omtx.core.VideoCodec

/**
 * MediaCodec encoder fed through its input Surface, run with async callbacks.
 *
 * Output rules (PROTOCOL-OMTX.md §1): the BUFFER_FLAG_CODEC_CONFIG buffer (SPS/PPS, plus VPS
 * for HEVC) is kept and put in front of every keyframe that does not already carry it, start
 * codes are 4 bytes, one access unit per callback.
 */
class VideoEncoder(
    private val codec: VideoCodec,
    private val width: Int,
    private val height: Int,
    private val fps: Int,
    initialBitrate: Int,
    private val sink: (au: ByteArray, keyframe: Boolean, ptsUs: Long) -> Unit,
    private val onFailure: (String) -> Unit,
) {
    private val thread = HandlerThread("encoder").apply { start() }
    private val handler = Handler(thread.looper)
    private var mc: MediaCodec? = null
    private var config: ByteArray? = null
    @Volatile private var running = false
    @Volatile var bitrate: Int = initialBitrate
        private set
    var encoderName: String = ""
        private set
    var iFrameInterval = 0
        private set

    val mime: String get() = mimeOf(codec)

    /** Configure and return the input Surface for the camera. */
    fun prepare(): Surface {
        val info = pickEncoder(codec, width, height)
        val caps = info?.getCapabilitiesForType(mime)
        val m = if (info != null) MediaCodec.createByCodecName(info.name) else MediaCodec.createEncoderByType(mime)
        encoderName = m.name

        // Keyframes only on demand (new subscriber, drop, OMTKeyframeRequest). Ask for no
        // periodic sync frames: -1 means "only the first frame" (API 25+); encoders that reject
        // it get one hour. Intra refresh stays off: joins wait for an IDR anyway.
        val attempts = listOf(
            -1 to false, NO_PERIODIC_FALLBACK_S to false, NO_PERIODIC_FALLBACK_S to true,
        )
        var configured = false
        for ((interval, minimal) in attempts) {
            try {
                m.configure(buildFormat(caps, interval, minimal), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                iFrameInterval = interval
                configured = true
                break
            } catch (e: Exception) {
                Log.w(TAG, "configure interval=$interval minimal=$minimal failed: $e")
                m.reset()
            }
        }
        if (!configured) {
            m.release()
            throw IllegalStateException("encoder configure failed")
        }
        m.setCallback(callback, handler)
        val surface = m.createInputSurface()
        mc = m
        Log.i(TAG, "encoder $encoderName ${width}x$height@$fps $codec iFrameInterval=$iFrameInterval")
        return surface
    }

    fun start() {
        running = true
        mc?.start()
    }

    fun requestKeyframe() = handler.post {
        if (!running) return@post
        try {
            mc?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
        } catch (e: Exception) { Log.w(TAG, "sync frame: $e") }
    }

    fun setBitrate(bps: Int) = handler.post {
        if (!running || bps == bitrate) return@post
        try {
            mc?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, bps) })
            bitrate = bps
        } catch (e: Exception) { Log.w(TAG, "bitrate: $e") }
    }

    fun release() {
        running = false
        handler.post {
            try { mc?.stop() } catch (_: Exception) {}
            try { mc?.release() } catch (_: Exception) {}
            mc = null
        }
        thread.quitSafely()
        thread.join(1000)
    }

    private fun buildFormat(caps: CodecCapabilities?, iFrameIntervalS: Int, minimal: Boolean): MediaFormat {
        val f = MediaFormat.createVideoFormat(mime, width, height)
        f.setInteger(MediaFormat.KEY_COLOR_FORMAT, CodecCapabilities.COLOR_FormatSurface)
        f.setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
        f.setInteger(MediaFormat.KEY_FRAME_RATE, fps)
        val enc = caps?.encoderCapabilities
        val cbr = enc?.isBitrateModeSupported(EncoderCapabilities.BITRATE_MODE_CBR) == true
        f.setInteger(
            MediaFormat.KEY_BITRATE_MODE,
            if (cbr) EncoderCapabilities.BITRATE_MODE_CBR else EncoderCapabilities.BITRATE_MODE_VBR,
        )
        f.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, iFrameIntervalS)
        if (minimal) return f

        f.setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
        f.setInteger(MediaFormat.KEY_PRIORITY, 0)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) f.setInteger(MediaFormat.KEY_LATENCY, 1)
        f.setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT709)
        f.setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED)
        f.setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_SDR_VIDEO)
        val profile = when (codec) {
            VideoCodec.H264 -> CodecProfileLevel.AVCProfileHigh
            VideoCodec.HEVC -> CodecProfileLevel.HEVCProfileMain
        }
        if (caps?.profileLevels?.any { it.profile == profile } == true) {
            f.setInteger(MediaFormat.KEY_PROFILE, profile)
        }
        return f
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) { /* Surface input */ }

        override fun onOutputBufferAvailable(c: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            try {
                if (info.size > 0) {
                    val buf = c.getOutputBuffer(index)
                    if (buf != null) {
                        buf.position(info.offset)
                        buf.limit(info.offset + info.size)
                        val bytes = ByteArray(info.size)
                        buf.get(bytes)
                        handleOutput(bytes, info)
                    }
                }
                c.releaseOutputBuffer(index, false)
            } catch (e: IllegalStateException) {
                // Codec stopped under us during shutdown.
            }
        }

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
            Log.e(TAG, "codec error", e)
            if (!e.isTransient) onFailure("encoder")
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {
            Log.i(TAG, "output format $format")
            // Some encoders deliver parameter sets here (csd-0/csd-1) instead of a config buffer.
            val csd = listOf("csd-0", "csd-1", "csd-2").mapNotNull { format.getByteBuffer(it) }
            if (csd.isNotEmpty() && config == null) {
                val all = csd.fold(ByteArray(0)) { acc, b ->
                    val a = ByteArray(b.remaining()); b.duplicate().get(a); acc + a
                }
                config = AnnexB.extractParameterSets(all, this@VideoEncoder.codec).takeIf { it.isNotEmpty() }
            }
        }
    }

    private fun handleOutput(bytes: ByteArray, info: MediaCodec.BufferInfo) {
        if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
            config = AnnexB.extractParameterSets(bytes, codec)
            // Rare: config and picture in one buffer. Otherwise this buffer has no picture.
            if (AnnexB.split(bytes, codec).none { codec.isVcl(it.type) }) return
        }
        // Keyframe = the AU holds an IDR (H.264 5) / IRAP (HEVC 16..21). BUFFER_FLAG_KEY_FRAME
        // alone is not trusted: some encoders set it on recovery-point frames that have no
        // parameter sets and are not a valid join point.
        val (au, idr) = AnnexB.makeSelfContained(bytes, codec, config)
        sink(au, idr, info.presentationTimeUs)
    }

    companion object {
        private const val TAG = "omtx.encoder"
        private const val NO_PERIODIC_FALLBACK_S = 3600

        fun mimeOf(codec: VideoCodec) = when (codec) {
            VideoCodec.H264 -> MediaFormat.MIMETYPE_VIDEO_AVC
            VideoCodec.HEVC -> MediaFormat.MIMETYPE_VIDEO_HEVC
        }

        /** Hardware encoder first, then any encoder. */
        fun pickEncoder(codec: VideoCodec, width: Int = 1280, height: Int = 720): MediaCodecInfo? {
            val mime = mimeOf(codec)
            val all = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
                info.isEncoder && info.supportedTypes.any { it.equals(mime, ignoreCase = true) } &&
                    runCatching {
                        info.getCapabilitiesForType(mime).videoCapabilities.isSizeSupported(width, height)
                    }.getOrDefault(false)
            }
            return all.firstOrNull { it.isHardwareAccelerated && !it.isAlias } ?: all.firstOrNull()
        }

        fun hasEncoder(codec: VideoCodec, width: Int, height: Int): Boolean = pickEncoder(codec, width, height) != null
    }
}
