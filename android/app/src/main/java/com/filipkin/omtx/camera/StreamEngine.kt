package com.filipkin.omtx.camera

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import android.system.Os
import android.system.OsConstants
import android.util.Log
import android.view.Surface
import com.filipkin.omtx.core.OmtxSender
import com.filipkin.omtx.core.SenderInfo
import com.filipkin.omtx.core.Tally
import java.util.concurrent.atomic.AtomicInteger

/**
 * Owns the camera, encoder, audio, sender, discovery and Wi-Fi lock. All public methods are
 * called on the main thread; callbacks to the UI are posted to the main thread.
 */
class StreamEngine(private val ctx: Context, private val ui: Ui) {
    interface Ui {
        fun onTally(tally: Tally)
        fun onFailure(what: String)
    }

    private val main = Handler(Looper.getMainLooper())
    val caps = CameraCaps(ctx)
    private val camera = CameraSource(ctx, caps) { what -> main.post { fail(what) } }
    private val discovery = Discovery(ctx)
    private val wifiLock = ctx.applicationContext.getSystemService(WifiManager::class.java)
        .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "omtx").apply { setReferenceCounted(false) }

    var settings: StreamSettings = StreamSettings.load(ctx)
    private var preview: Surface? = null

    @Volatile private var sender: OmtxSender? = null
    @Volatile private var encoder: VideoEncoder? = null
    private var encoderSurface: Surface? = null
    private var audio: AudioCapture? = null

    val streaming: Boolean get() = sender != null
    var audioEnabled = true

    // Shared clock: CLOCK_BOOTTIME nanoseconds at stream start; both media use it.
    private var baseBootNs = 0L
    private var monoToBootNs = 0L
    private var lastVideoTs = -1L
    private var lastAudioTs = -1L

    private val framesThisSecond = AtomicInteger()

    val port: Int get() = sender?.port ?: -1
    val receivers: Int get() = sender?.connectionCount ?: 0
    val targetBps: Int get() = sender?.targetBps ?: 0
    var tally: Tally = Tally.NONE
        private set

    /** Frames encoded since the last call. */
    fun takeFrameCount(): Int = framesThisSecond.getAndSet(0)

    fun setPreviewSurface(s: Surface?) {
        // A Surface about to be destroyed: stop the camera writing into it first.
        if (s == null && preview != null) camera.stopOutputsBlocking()
        preview = s
        reconfigureCamera()
    }

    private fun reconfigureCamera() {
        val p = preview
        if (p == null && encoderSurface == null) {
            camera.close()
            return
        }
        camera.configure(p, encoderSurface, settings.fps)
    }

    fun start() {
        if (streaming) return
        val s = settings
        baseBootNs = SystemClock.elapsedRealtimeNanos()
        monoToBootNs = SystemClock.elapsedRealtimeNanos() - System.nanoTime()
        lastVideoTs = -1L
        lastAudioTs = -1L

        val snd = OmtxSender(
            SenderInfo.toXml("omtx camera", "omtx", BuildConfig.VERSION_NAME),
            object : OmtxSender.Listener {
                override fun onKeyframeRequest() { encoder?.requestKeyframe() }
                override fun onBitrateChanged(bps: Int) { encoder?.setBitrate(bps) }
                override fun onTallyChanged(tally: Tally) {
                    main.post { this@StreamEngine.tally = tally; ui.onTally(tally) }
                }
            },
            userCeilingBps = s.maxBitrateBps,
            log = { Log.i(TAG, it) },
            configureSocket = ::setNotSentLowat,
        )
        try {
            snd.start()
        } catch (e: Exception) {
            Log.e(TAG, "sender start", e)
            ui.onFailure("port")
            return
        }
        sender = snd

        val enc = VideoEncoder(
            s.codec, s.width, s.height, s.fps, snd.targetBps,
            sink = { au, key, ptsUs -> onVideo(au, key, ptsUs) },
            onFailure = { what -> main.post { fail(what) } },
        )
        val surface = try {
            enc.prepare()
        } catch (e: Exception) {
            Log.e(TAG, "encoder prepare", e)
            enc.release()
            snd.stop()
            sender = null
            ui.onFailure("encoder")
            return
        }
        encoder = enc
        encoderSurface = surface
        enc.start()
        reconfigureCamera()

        if (audioEnabled) {
            val a = AudioCapture(
                wanted = { sender?.hasAudioSubscribers == true },
                sink = { rate, ch, spc, bootNs, planar ->
                    sender?.sendAudio(rate, ch, spc, toTimestamp(bootNs, audio = true), planar)
                },
            )
            audio = if (a.start()) a else null
        }

        discovery.register(s.sourceName, snd.port)
        wifiLock.acquire()
    }

    fun stop() {
        if (!streaming) return
        discovery.unregister()
        // Camera stops writing into the encoder Surface before the encoder goes away.
        camera.stopOutputsBlocking()
        val enc = encoder
        encoder = null
        encoderSurface?.release()
        encoderSurface = null
        enc?.release()
        audio?.stop()
        audio = null
        sender?.stop()
        sender = null
        if (wifiLock.isHeld) wifiLock.release()
        tally = Tally.NONE
        ui.onTally(Tally.NONE)
        reconfigureCamera()
    }

    fun release() {
        stop()
        camera.release()
    }

    private fun fail(what: String) {
        Log.e(TAG, "failure: $what")
        stop()
        ui.onFailure(what)
    }

    private fun onVideo(au: ByteArray, key: Boolean, ptsUs: Long) {
        val snd = sender ?: return
        val s = settings
        framesThisSecond.incrementAndGet()
        val ptsNs = ptsUs * 1000L
        var bootNs = if (caps.timestampIsBoottime) ptsNs else ptsNs + monoToBootNs
        val now = SystemClock.elapsedRealtimeNanos()
        // Guard against an unexpected sensor time base: fall back to arrival time.
        if (bootNs > now + 1_000_000_000L || bootNs < now - 1_000_000_000L) bootNs = now
        snd.sendVideo(s.codec, s.width, s.height, s.fps, 1, key, toTimestamp(bootNs, audio = false), au)
    }

    /** 100 ns units since stream start, strictly increasing per media type. */
    private fun toTimestamp(bootNs: Long, audio: Boolean): Long {
        var t = maxOf(0L, (bootNs - baseBootNs) / 100L)
        if (audio) {
            if (t <= lastAudioTs) t = lastAudioTs + 1
            lastAudioTs = t
        } else {
            if (t <= lastVideoTs) t = lastVideoTs + 1
            lastVideoTs = t
        }
        return t
    }

    companion object {
        private const val TAG = "omtx.engine"

        /** TCP_NOTSENT_LOWAT = 16 KB, as the C# fork does on Linux. Best effort. */
        fun setNotSentLowat(socket: java.net.Socket) {
            try {
                ParcelFileDescriptor.fromSocket(socket).use { pfd ->
                    Os.setsockoptInt(pfd.fileDescriptor, OsConstants.IPPROTO_TCP,
                        OmtxSender.TCP_NOTSENT_LOWAT, OmtxSender.NOTSENT_LOWAT_BYTES)
                }
            } catch (e: Exception) {
                Log.w(TAG, "TCP_NOTSENT_LOWAT: $e")
            }
        }
    }
}
