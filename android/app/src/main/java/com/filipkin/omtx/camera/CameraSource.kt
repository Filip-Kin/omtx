package com.filipkin.omtx.camera

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.PointF
import android.graphics.Rect
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.params.MeteringRectangle
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.MediaCodec
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.util.Range
import android.util.Size
import android.view.Surface
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executor
import java.util.concurrent.TimeUnit
import kotlin.math.roundToInt

/** What one camera (rear or front) can do, read once from CameraCharacteristics. */
class CameraCaps(ctx: Context, val facing: Facing) {
    private val manager = ctx.getSystemService(CameraManager::class.java)

    /**
     * The camera for this facing with the widest zoom range. On a Pixel that is the logical
     * multi-camera, whose CONTROL_ZOOM_RATIO reaches into the ultrawide (<1x) and telephoto
     * lenses. Ties keep the system's order, so the default camera wins.
     */
    val cameraId: String? = candidates(manager, facing).minByOrNull { zoomRangeOf(manager.getCameraCharacteristics(it)).lower }

    private val chars = cameraId?.let { manager.getCameraCharacteristics(it) }

    /** True if sensor timestamps are on the elapsedRealtime (boot time) base. */
    val timestampIsBoottime: Boolean =
        chars?.get(CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE) ==
            CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE_REALTIME

    private val fpsRanges: List<Range<Int>> =
        chars?.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.toList() ?: emptyList()

    fun supportsSize(w: Int, h: Int): Boolean {
        val map = chars?.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: return false
        return map.getOutputSizes(MediaCodec::class.java)?.any { it.width == w && it.height == h } == true
    }

    /** A normal (not high speed) session can deliver `fps` at this size. */
    fun supportsFps(w: Int, h: Int, fps: Int): Boolean {
        if (fpsRanges.none { it.upper >= fps }) return false
        val map = chars?.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: return false
        val minDur = runCatching { map.getOutputMinFrameDuration(MediaCodec::class.java, Size(w, h)) }.getOrDefault(0L)
        return minDur == 0L || minDur <= 1_000_000_000L / fps + 1_000L
    }

    /** Fixed [fps, fps] if offered, else the range whose top is `fps` with the highest floor. */
    fun aeRange(fps: Int): Range<Int>? =
        fpsRanges.firstOrNull { it.lower == fps && it.upper == fps }
            ?: fpsRanges.filter { it.upper == fps }.maxByOrNull { it.lower }
            ?: fpsRanges.filter { it.upper >= fps }.minByOrNull { it.upper }

    private val afModes: IntArray = chars?.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES) ?: IntArray(0)

    val continuousVideoAf: Boolean = afModes.contains(CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)

    /** A focusing lens (fixed-focus front cameras have none): tap to focus and focus lock. */
    val hasAf: Boolean = afModes.contains(CaptureRequest.CONTROL_AF_MODE_AUTO) &&
        (chars?.get(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE) ?: 0f) > 0f

    val maxAfRegions: Int = chars?.get(CameraCharacteristics.CONTROL_MAX_REGIONS_AF) ?: 0
    val maxAeRegions: Int = chars?.get(CameraCharacteristics.CONTROL_MAX_REGIONS_AE) ?: 0
    val aeLockAvailable: Boolean = chars?.get(CameraCharacteristics.CONTROL_AE_LOCK_AVAILABLE) == true
    val aeCompRange: Range<Int> = chars?.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_RANGE) ?: Range(0, 0)
    val aeCompStep: Float = chars?.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_STEP)?.toFloat() ?: 0f

    /** CONTROL_ZOOM_RATIO (API 30+) when the camera lists a range, else SCALER_CROP_REGION. */
    val usesZoomRatio: Boolean = Build.VERSION.SDK_INT >= Build.VERSION_CODES.R &&
        chars?.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE) != null
    val zoomRange: Range<Float> = chars?.let { zoomRangeOf(it) } ?: Range(1f, 1f)

    private val activeArray: Rect = chars?.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE) ?: Rect(0, 0, 1, 1)
    private val sensorOrientation: Int = chars?.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90

    fun clampZoom(z: Float): Float = z.coerceIn(zoomRange.lower, zoomRange.upper)

    /** Digital crop for devices without CONTROL_ZOOM_RATIO, in active-array coordinates. */
    fun cropRegion(zoom: Float): Rect {
        val w = activeArray.width()
        val h = activeArray.height()
        val cw = (w / zoom).roundToInt()
        val ch = (h / zoom).roundToInt()
        val l = (w - cw) / 2
        val t = (h - ch) / 2
        return Rect(l, t, l + cw, t + ch)
    }

    /**
     * A point on the preview (0..1 across the shown picture, display orientation) as a metering
     * rectangle in sensor coordinates. The picture is the centre of the sensor cropped to the
     * stream aspect; with CONTROL_ZOOM_RATIO the region coordinates already follow the zoomed
     * field of view, with a crop region they are inside that crop.
     */
    fun meteringRect(point: PointF, displayRotationDeg: Int, aspect: Double, zoom: Float): MeteringRectangle {
        val base = if (usesZoomRatio) Rect(0, 0, activeArray.width(), activeArray.height()) else cropRegion(zoom)
        val visible = centreCrop(base, aspect)
        // Same rotation as the JPEG_ORIENTATION formula: front cameras turn the other way and
        // their preview is mirrored.
        val front = facing == Facing.FRONT
        val rot = if (front) (sensorOrientation + displayRotationDeg) % 360 else (sensorOrientation - displayRotationDeg + 360) % 360
        var x = point.x
        var y = point.y
        if (rot == 180) { x = 1f - x; y = 1f - y }
        if (front) x = 1f - x
        val cx = visible.left + (x.coerceIn(0f, 1f) * visible.width()).roundToInt()
        val cy = visible.top + (y.coerceIn(0f, 1f) * visible.height()).roundToInt()
        val half = (minOf(visible.width(), visible.height()) * 0.08f).roundToInt().coerceAtLeast(1)
        val r = Rect(
            (cx - half).coerceAtLeast(base.left), (cy - half).coerceAtLeast(base.top),
            (cx + half).coerceAtMost(base.right), (cy + half).coerceAtMost(base.bottom),
        )
        return MeteringRectangle(r, MeteringRectangle.METERING_WEIGHT_MAX - 1)
    }

    companion object {
        private fun lensFacing(f: Facing) = when (f) {
            Facing.REAR -> CameraCharacteristics.LENS_FACING_BACK
            Facing.FRONT -> CameraCharacteristics.LENS_FACING_FRONT
        }

        private fun candidates(manager: CameraManager, facing: Facing): List<String> =
            manager.cameraIdList.filter { id ->
                val c = manager.getCameraCharacteristics(id)
                c.get(CameraCharacteristics.LENS_FACING) == lensFacing(facing) &&
                    c.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES)
                        ?.contains(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES_BACKWARD_COMPATIBLE) == true
            }

        private fun zoomRangeOf(c: CameraCharacteristics): Range<Float> {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                c.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE)?.let { return it }
            }
            val max = c.get(CameraCharacteristics.SCALER_AVAILABLE_MAX_DIGITAL_ZOOM) ?: 1f
            return Range(1f, maxOf(1f, max))
        }

        /** Facings this phone has a usable camera for. */
        fun available(ctx: Context): List<Facing> {
            val m = ctx.getSystemService(CameraManager::class.java)
            return Facing.entries.filter { candidates(m, it).isNotEmpty() }
        }

        private fun centreCrop(r: Rect, aspect: Double): Rect {
            val w = r.width()
            val h = r.height()
            return if (w.toDouble() / h > aspect) {
                val nw = (h * aspect).roundToInt()
                val l = r.left + (w - nw) / 2
                Rect(l, r.top, l + nw, r.bottom)
            } else {
                val nh = (w / aspect).roundToInt()
                val t = r.top + (h - nh) / 2
                Rect(r.left, t, r.right, t + nh)
            }
        }
    }
}

/**
 * Per-session camera controls. `focusPoint` is on the preview, 0..1 in display orientation;
 * null means the whole frame.
 */
data class CameraControls(
    val zoom: Float = 1f,
    val focusPoint: PointF? = null,
    val focusLocked: Boolean = false,
    val exposureLocked: Boolean = false,
    val exposureComp: Int = 0,
)

/**
 * Camera2 camera feeding a preview Surface and, while streaming, the encoder's input Surface
 * (zero copy). The session is rebuilt whenever the set of outputs changes. The encoder Surface
 * stays in the session while streaming but is only a target of the repeating request while a
 * receiver is connected, so with nobody watching the encoder gets no frames and sits idle.
 */
class CameraSource(
    private val ctx: Context,
    private var caps: CameraCaps,
    private val onFailure: (String) -> Unit,
) {
    private val manager = ctx.getSystemService(CameraManager::class.java)
    private val thread = HandlerThread("camera").apply { start() }
    private val handler = Handler(thread.looper)
    private val executor = Executor { handler.post(it) }

    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var active: Config? = null
    private var opening = false
    private var pending: Config? = null
    private var closeLatch: CountDownLatch? = null

    // Camera thread only.
    private var controls = CameraControls()
    private var encoderActive = false
    private var displayRotationDeg = 90
    /** Focus locked by a tap while locked: AF_MODE_AUTO scanned at that point, then held. */
    private var scanLock = false

    private data class Config(val preview: Surface?, val encoder: Surface?, val fps: Int, val aspect: Double)

    /** Open (if needed) and run with these outputs. Calls are serialised on the camera thread. */
    fun configure(preview: Surface?, encoder: Surface?, fps: Int, aspect: Double) = handler.post {
        pending = Config(preview, encoder, fps, aspect)
        if (device == null) open() else applyPending()
    }

    fun setControls(c: CameraControls, displayRotation: Int) = handler.post {
        val old = controls
        controls = c
        displayRotationDeg = displayRotation
        applyControls(old, c)
    }

    /** Feed the encoder Surface (a receiver is connected) or only the preview. */
    fun setEncoderActive(on: Boolean) = handler.post {
        if (encoderActive == on) return@post
        encoderActive = on
        repeat()
    }

    /** Close the current camera and open the one in `newCaps` with the same outputs. */
    fun switchCamera(newCaps: CameraCaps, c: CameraControls) = handler.post {
        caps = newCaps
        controls = c
        scanLock = false
        closeSession()
        device?.close()
        device = null
        if (pending != null) open()
    }

    /**
     * Stop all capture and wait (bounded) until the session has closed, so a Surface can be
     * released afterwards without the camera still writing into it. Keeps the device open.
     */
    fun stopOutputsBlocking() {
        val latch = CountDownLatch(1)
        handler.post {
            pending = null
            val s = session
            session = null
            active = null
            if (s == null) {
                latch.countDown()
            } else {
                closeLatch = latch
                try { s.abortCaptures() } catch (_: Exception) {}
                try { s.close() } catch (_: Exception) { latch.countDown() }
            }
        }
        latch.await(1, TimeUnit.SECONDS)
    }

    fun close() = handler.post {
        pending = null
        closeSession()
        device?.close()
        device = null
    }

    fun release() {
        close()
        thread.quitSafely()
    }

    @SuppressLint("MissingPermission")
    private fun open() {
        if (opening) return
        val id = caps.cameraId ?: run { onFailure("camera"); return }
        opening = true
        try {
            manager.openCamera(id, executor, object : CameraDevice.StateCallback() {
                override fun onOpened(d: CameraDevice) {
                    opening = false
                    if (pending == null) { d.close(); return }
                    if (d.id != caps.cameraId) {
                        // The camera was switched while this one was opening.
                        d.close()
                        open()
                        return
                    }
                    device = d
                    applyPending()
                }
                override fun onDisconnected(d: CameraDevice) {
                    opening = false
                    d.close()
                    if (device === d) { device = null; session = null; active = null }
                    onFailure("camera disconnected")
                }
                override fun onError(d: CameraDevice, error: Int) {
                    opening = false
                    d.close()
                    if (device === d) { device = null; session = null; active = null }
                    onFailure("camera $error")
                }
            })
        } catch (e: Exception) {
            opening = false
            Log.e(TAG, "openCamera", e)
            onFailure("camera")
        }
    }

    private fun closeSession() {
        try { session?.close() } catch (_: Exception) {}
        session = null
        active = null
    }

    private fun applyPending() {
        val d = device ?: return
        val cfg = pending ?: return
        closeSession()
        val outputs = listOfNotNull(cfg.preview, cfg.encoder)
        if (outputs.isEmpty()) return
        val outputConfigs = outputs.map { s ->
            OutputConfiguration(s).also {
                // The picture that goes out is never mirrored, whatever the preview does.
                if (s === cfg.encoder && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    it.mirrorMode = OutputConfiguration.MIRROR_MODE_NONE
                }
            }
        }
        val sc = SessionConfiguration(
            SessionConfiguration.SESSION_REGULAR,
            outputConfigs,
            executor,
            object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(s: CameraCaptureSession) {
                    if (pending !== cfg || device !== d) { s.close(); return }
                    session = s
                    active = cfg
                    scanLock = false
                    repeat()
                    // A new session starts unlocked; lock again where the user had it.
                    if (controls.focusLocked) trigger(CaptureRequest.CONTROL_AF_TRIGGER_START)
                }
                override fun onClosed(s: CameraCaptureSession) {
                    closeLatch?.countDown()
                }
                override fun onConfigureFailed(s: CameraCaptureSession) {
                    Log.e(TAG, "session configure failed")
                    onFailure("camera session")
                }
            },
        )
        try {
            d.createCaptureSession(sc)
        } catch (e: Exception) {
            Log.e(TAG, "createCaptureSession", e)
            onFailure("camera session")
        }
    }

    private fun applyControls(old: CameraControls, c: CameraControls) {
        if (!caps.hasAf) { repeat(); return }
        val pointMoved = c.focusPoint != old.focusPoint
        when {
            c.focusLocked && !old.focusLocked -> {
                // Freeze the focus where continuous AF has it now.
                scanLock = false
                repeat()
                trigger(CaptureRequest.CONTROL_AF_TRIGGER_START)
            }
            !c.focusLocked && old.focusLocked -> {
                scanLock = false
                repeat()
                trigger(CaptureRequest.CONTROL_AF_TRIGGER_CANCEL)
            }
            c.focusLocked && pointMoved -> {
                // Tap while locked: one scan at the new point, then hold it.
                scanLock = true
                repeat()
                trigger(CaptureRequest.CONTROL_AF_TRIGGER_START)
            }
            else -> repeat()
        }
    }

    private fun targets(cfg: Config): List<Surface> =
        listOfNotNull(cfg.preview, cfg.encoder?.takeIf { encoderActive })

    private fun buildRequest(d: CameraDevice, cfg: Config, targets: List<Surface>): CaptureRequest.Builder {
        val template = if (cfg.encoder != null) CameraDevice.TEMPLATE_RECORD else CameraDevice.TEMPLATE_PREVIEW
        val c = controls
        return d.createCaptureRequest(template).apply {
            targets.forEach { addTarget(it) }
            set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO)
            caps.aeRange(cfg.fps)?.let { set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) }

            val zoom = caps.clampZoom(c.zoom)
            if (caps.usesZoomRatio) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) set(CaptureRequest.CONTROL_ZOOM_RATIO, zoom)
            } else if (zoom > 1f) {
                set(CaptureRequest.SCALER_CROP_REGION, caps.cropRegion(zoom))
            }

            if (caps.hasAf) {
                val mode = if (scanLock || !caps.continuousVideoAf) CaptureRequest.CONTROL_AF_MODE_AUTO
                else CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO
                set(CaptureRequest.CONTROL_AF_MODE, mode)
            } else if (caps.continuousVideoAf) {
                set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
            }
            c.focusPoint?.let { p ->
                val r = caps.meteringRect(p, displayRotationDeg, cfg.aspect, zoom)
                if (caps.hasAf && caps.maxAfRegions > 0) set(CaptureRequest.CONTROL_AF_REGIONS, arrayOf(r))
                if (caps.maxAeRegions > 0) set(CaptureRequest.CONTROL_AE_REGIONS, arrayOf(r))
            }

            set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
            if (caps.aeLockAvailable) set(CaptureRequest.CONTROL_AE_LOCK, c.exposureLocked)
            set(
                CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION,
                c.exposureComp.coerceIn(caps.aeCompRange.lower, caps.aeCompRange.upper),
            )
        }
    }

    /** (Re)issue the repeating request for the current session, controls and encoder state. */
    private fun repeat() {
        val d = device ?: return
        val s = session ?: return
        val cfg = active ?: return
        try {
            s.setRepeatingRequest(buildRequest(d, cfg, targets(cfg)).build(), null, handler)
        } catch (e: Exception) {
            Log.e(TAG, "setRepeatingRequest", e)
            onFailure("camera")
        }
    }

    /** One capture carrying an AF trigger; the repeating request keeps the mode. */
    private fun trigger(afTrigger: Int) {
        val d = device ?: return
        val s = session ?: return
        val cfg = active ?: return
        try {
            val req = buildRequest(d, cfg, targets(cfg)).apply {
                set(CaptureRequest.CONTROL_AF_TRIGGER, afTrigger)
            }.build()
            s.capture(req, null, handler)
        } catch (e: Exception) {
            Log.w(TAG, "AF trigger: $e")
        }
    }

    companion object { private const val TAG = "omtx.camera" }
}
