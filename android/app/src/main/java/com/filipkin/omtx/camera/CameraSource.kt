package com.filipkin.omtx.camera

import android.annotation.SuppressLint
import android.content.Context
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.MediaCodec
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.util.Range
import android.util.Size
import android.view.Surface
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executor
import java.util.concurrent.TimeUnit

/** What the back camera can do, read once from CameraCharacteristics. */
class CameraCaps(ctx: Context) {
    private val manager = ctx.getSystemService(CameraManager::class.java)
    val cameraId: String? = manager.cameraIdList.firstOrNull {
        manager.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_BACK
    } ?: manager.cameraIdList.firstOrNull()

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

    val continuousVideoAf: Boolean =
        chars?.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES)
            ?.contains(CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO) == true
}

/**
 * Camera2 back camera feeding a preview Surface and, while streaming, the encoder's input
 * Surface (zero copy). The session is rebuilt whenever the set of outputs changes.
 */
class CameraSource(private val ctx: Context, private val caps: CameraCaps, private val onFailure: (String) -> Unit) {
    private val manager = ctx.getSystemService(CameraManager::class.java)
    private val thread = HandlerThread("camera").apply { start() }
    private val handler = Handler(thread.looper)
    private val executor = Executor { handler.post(it) }

    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var opening = false
    private var pending: Config? = null
    private var closeLatch: CountDownLatch? = null

    private data class Config(val preview: Surface?, val encoder: Surface?, val fps: Int)

    /** Open (if needed) and run with these outputs. Calls are serialised on the camera thread. */
    fun configure(preview: Surface?, encoder: Surface?, fps: Int) = handler.post {
        pending = Config(preview, encoder, fps)
        if (device == null) open() else applyPending()
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
                    device = d
                    applyPending()
                }
                override fun onDisconnected(d: CameraDevice) {
                    opening = false
                    d.close()
                    if (device === d) { device = null; session = null }
                    onFailure("camera disconnected")
                }
                override fun onError(d: CameraDevice, error: Int) {
                    opening = false
                    d.close()
                    if (device === d) { device = null; session = null }
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
    }

    private fun applyPending() {
        val d = device ?: return
        val cfg = pending ?: return
        closeSession()
        val outputs = listOfNotNull(cfg.preview, cfg.encoder)
        if (outputs.isEmpty()) return
        val sc = SessionConfiguration(
            SessionConfiguration.SESSION_REGULAR,
            outputs.map { OutputConfiguration(it) },
            executor,
            object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(s: CameraCaptureSession) {
                    if (pending !== cfg || device !== d) { s.close(); return }
                    session = s
                    try {
                        val template = if (cfg.encoder != null) CameraDevice.TEMPLATE_RECORD else CameraDevice.TEMPLATE_PREVIEW
                        val req = d.createCaptureRequest(template).apply {
                            outputs.forEach { addTarget(it) }
                            caps.aeRange(cfg.fps)?.let { set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) }
                            if (caps.continuousVideoAf) {
                                set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
                            }
                            set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO)
                        }.build()
                        s.setRepeatingRequest(req, null, handler)
                    } catch (e: Exception) {
                        Log.e(TAG, "setRepeatingRequest", e)
                        onFailure("camera")
                    }
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

    companion object { private const val TAG = "omtx.camera" }
}
