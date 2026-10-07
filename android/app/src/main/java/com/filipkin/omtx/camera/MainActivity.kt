package com.filipkin.omtx.camera

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.PointF
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.GestureDetector
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.SeekBar
import android.widget.Spinner
import android.widget.TextView
import com.filipkin.omtx.core.Tally
import com.filipkin.omtx.core.VideoCodec
import java.util.Locale

class MainActivity : Activity(), StreamEngine.Ui, SurfaceHolder.Callback {
    private lateinit var tallyFrame: AspectFrameLayout
    private lateinit var previewView: SurfaceView
    private lateinit var status: TextView
    private lateinit var startButton: Button
    private lateinit var settingsButton: Button
    private lateinit var permissionButton: Button
    private lateinit var panel: View
    private lateinit var sourceName: EditText
    private lateinit var resolutionSpinner: Spinner
    private lateinit var fpsSpinner: Spinner
    private lateinit var codecSpinner: Spinner
    private lateinit var overlay: PreviewOverlay
    private lateinit var controlsColumn: View
    private lateinit var zoomButton: Button
    private lateinit var cameraButton: Button
    private lateinit var focusLockButton: Button
    private lateinit var exposureLockButton: Button
    private lateinit var exposureButton: Button
    private lateinit var gridButton: Button
    private lateinit var tallyModeButton: Button
    private lateinit var exposureRow: View
    private lateinit var exposureSeek: SeekBar
    private lateinit var exposureValue: TextView
    private lateinit var connectionDot: View

    private val main = Handler(Looper.getMainLooper())
    private var engine: StreamEngine? = null
    private var settings = StreamSettings()
    private var caps: CameraCaps? = null
    private var surfaceReady = false
    private var resumed = false
    private var wantStreaming = false
    private var failure: Int? = null
    private var populating = false
    private var fps = 0
    private var permissionAsked = false

    // Zoom, focus and exposure for this run of the app; reset when the camera changes.
    private var controls = CameraControls()
    private var facings: List<Facing> = emptyList()
    private var exposureOpen = false
    private var otherCaps: CameraCaps? = null
    private var tally = Tally.NONE

    private var resolutions: List<Pair<Int, Int>> = StreamSettings.RESOLUTIONS
    private var frameRates: List<Int> = StreamSettings.FRAME_RATES
    private var codecs: List<VideoCodec> = listOf(VideoCodec.H264)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_main)
        tallyFrame = findViewById(R.id.tally)
        previewView = findViewById(R.id.preview)
        status = findViewById(R.id.status)
        startButton = findViewById(R.id.start)
        settingsButton = findViewById(R.id.settings)
        permissionButton = findViewById(R.id.permission)
        panel = findViewById(R.id.settings_panel)
        sourceName = findViewById(R.id.source_name)
        resolutionSpinner = findViewById(R.id.resolution)
        fpsSpinner = findViewById(R.id.frame_rate)
        codecSpinner = findViewById(R.id.codec)
        overlay = findViewById(R.id.overlay)
        controlsColumn = findViewById(R.id.controls)
        zoomButton = findViewById(R.id.zoom)
        cameraButton = findViewById(R.id.camera)
        focusLockButton = findViewById(R.id.focus_lock)
        exposureLockButton = findViewById(R.id.exposure_lock)
        exposureButton = findViewById(R.id.exposure)
        gridButton = findViewById(R.id.grid)
        tallyModeButton = findViewById(R.id.tally_mode)
        exposureRow = findViewById(R.id.exposure_row)
        exposureSeek = findViewById(R.id.exposure_comp)
        exposureValue = findViewById(R.id.exposure_value)
        connectionDot = findViewById(R.id.connection)

        settings = StreamSettings.load(this)
        previewView.holder.addCallback(this)
        previewView.holder.setFixedSize(settings.width, settings.height)

        startButton.setOnClickListener { toggleStreaming() }
        settingsButton.setOnClickListener { setPanelVisible(panel.visibility != View.VISIBLE) }
        permissionButton.setOnClickListener { askPermissionsOrOpenSettings() }
        sourceName.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_DONE) { commitSourceName(); hideKeyboard(); true } else false
        }
        sourceName.setOnFocusChangeListener { _, hasFocus -> if (!hasFocus) commitSourceName() }
        listOf(resolutionSpinner, fpsSpinner, codecSpinner).forEach {
            it.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
                override fun onItemSelected(p: AdapterView<*>?, v: View?, pos: Int, id: Long) { onSpinnerChanged() }
                override fun onNothingSelected(p: AdapterView<*>?) {}
            }
        }
        setUpCameraControls()
        updateUi()
    }

    override fun onResume() {
        super.onResume()
        resumed = true
        hideSystemBars()
        if (!hasCamera()) {
            // Ask once on its own; the dialog itself pauses and resumes this activity.
            if (!permissionAsked) {
                permissionAsked = true
                requestPermissions(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO), REQ_PERMS)
            }
        } else {
            createEngine()
        }
        main.post(tick)
        updateUi()
    }

    override fun onPause() {
        resumed = false
        main.removeCallbacks(tick)
        commitSourceName()
        val e = engine
        if (e != null) {
            wantStreaming = e.streaming
            e.release()
        }
        engine = null
        super.onPause()
    }

    private fun hasCamera() = checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
    private fun hasMic() = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQ_PERMS) return
        if (hasCamera() && resumed) createEngine()
        updateUi()
    }

    private fun askPermissionsOrOpenSettings() {
        if (shouldShowRequestPermissionRationale(Manifest.permission.CAMERA) || !permissionAsked) {
            requestPermissions(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO), REQ_PERMS)
        } else {
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null)))
        }
        permissionAsked = true
    }

    private fun createEngine() {
        if (engine != null) return
        val e = StreamEngine(this, this)
        e.settings = settings
        e.audioEnabled = hasMic()
        engine = e
        caps = e.caps
        populateSettings()
        facings = CameraCaps.available(this)
        e.setControls(controls, displayRotationDeg())
        if (surfaceReady) e.setPreviewSurface(previewView.holder.surface)
        if (wantStreaming) {
            wantStreaming = false
            e.start()
        }
        updateUi()
    }

    private fun toggleStreaming() {
        val e = engine ?: return
        failure = null
        if (e.streaming) {
            e.stop()
        } else {
            commitSourceName()
            setPanelVisible(false)
            e.settings = settings
            e.audioEnabled = hasMic()
            e.start()
        }
        updateUi()
    }

    // SurfaceHolder.Callback
    override fun surfaceCreated(holder: SurfaceHolder) {}

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        // Only hand the camera a surface that already has the stream size.
        surfaceReady = width == settings.width && height == settings.height
        if (surfaceReady) engine?.setPreviewSurface(holder.surface)
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surfaceReady = false
        engine?.setPreviewSurface(null)
    }

    // StreamEngine.Ui
    override fun onTally(tally: Tally) {
        this.tally = tally
        showTally()
    }

    override fun onReceiversChanged(count: Int) = updateStatus()

    private fun showTally() {
        val color = when {
            tally.program -> Color.rgb(0xE5, 0x39, 0x35)
            tally.preview -> Color.rgb(0x43, 0xA0, 0x47)
            else -> Color.TRANSPARENT
        }
        tallyFrame.setBackgroundColor(if (settings.tallyMode == TallyMode.BORDER) color else Color.TRANSPARENT)
        overlay.tint = if (settings.tallyMode == TallyMode.FULL_SCREEN && color != Color.TRANSPARENT) {
            Color.argb(TINT_ALPHA, Color.red(color), Color.green(color), Color.blue(color))
        } else {
            Color.TRANSPARENT
        }
    }

    override fun onFailure(what: String) {
        failure = when {
            what.startsWith("encoder") -> R.string.encoder_error
            what.startsWith("port") -> R.string.port_error
            else -> R.string.camera_error
        }
        updateUi()
    }

    private val tick = object : Runnable {
        override fun run() {
            fps = engine?.takeFrameCount() ?: 0
            updateStatus()
            main.postDelayed(this, 1000)
        }
    }

    private fun updateUi() {
        val e = engine
        val streaming = e?.streaming == true
        permissionButton.visibility = if (!hasCamera()) View.VISIBLE else View.GONE
        startButton.isEnabled = e != null
        startButton.setText(if (streaming) R.string.stop else R.string.start)
        // Settings only apply when stopped, so the control is hidden while streaming.
        settingsButton.visibility = if (streaming || e == null) View.GONE else View.VISIBLE
        if (streaming || e == null) setPanelVisible(false)
        updateControls()
        updateStatus()
    }

    private fun updateStatus() {
        val e = engine
        val f = failure
        connectionDot.visibility = if (e?.streaming == true) View.VISIBLE else View.GONE
        connectionDot.backgroundTintList = ColorStateList.valueOf(
            if ((e?.receivers ?: 0) > 0) Color.rgb(0x43, 0xA0, 0x47) else Color.rgb(0x75, 0x75, 0x75),
        )
        status.text = when {
            f != null -> getString(f)
            e == null -> ""
            e.streaming -> buildString {
                append(getString(R.string.receivers)).append(' ').append(e.receivers)
                append("   ").append(String.format(Locale.US, "%.1f", e.targetBps / 1_000_000.0))
                append(' ').append(getString(R.string.mbps_unit))
                append("   ").append(fps).append(' ').append(getString(R.string.fps_unit))
                append("   ").append(getString(R.string.port)).append(' ').append(e.port)
                if (!e.audioEnabled) append("   ").append(getString(R.string.audio_off))
            }
            else -> "${settings.width}x${settings.height}   ${settings.fps} ${getString(R.string.fps_unit)}   ${codecLabel(settings.codec)}"
        }
    }

    private fun setPanelVisible(visible: Boolean) {
        if (!visible) {
            commitSourceName()
            hideKeyboard()
        }
        panel.visibility = if (visible) View.VISIBLE else View.GONE
        updateControls()
    }

    private fun populateSettings() {
        val c = caps ?: return
        populating = true
        resolutions = StreamSettings.RESOLUTIONS.filter { c.supportsSize(it.first, it.second) }
            .ifEmpty { StreamSettings.RESOLUTIONS }
        if ((settings.width to settings.height) !in resolutions) {
            settings = settings.copy(width = resolutions[0].first, height = resolutions[0].second)
        }
        frameRates = StreamSettings.FRAME_RATES.filter { c.supportsFps(settings.width, settings.height, it) }
            .ifEmpty { listOf(30) }
        if (settings.fps !in frameRates) settings = settings.copy(fps = frameRates[0])
        codecs = VideoCodec.entries.filter { VideoEncoder.hasEncoder(it, settings.width, settings.height) }
            .ifEmpty { listOf(VideoCodec.H264) }
        if (settings.codec !in codecs) settings = settings.copy(codec = codecs[0])

        sourceName.setText(settings.sourceName)
        bind(resolutionSpinner, resolutions.map { "${it.first}x${it.second}" }, resolutions.indexOf(settings.width to settings.height))
        bind(fpsSpinner, frameRates.map { "$it ${getString(R.string.fps_unit)}" }, frameRates.indexOf(settings.fps))
        bind(codecSpinner, codecs.map { codecLabel(it) }, codecs.indexOf(settings.codec))
        populating = false
        applySettings()
    }

    private fun bind(spinner: Spinner, items: List<String>, selected: Int) {
        val a = ArrayAdapter(this, android.R.layout.simple_spinner_item, items)
        a.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item)
        spinner.adapter = a
        spinner.setSelection(selected.coerceAtLeast(0), false)
    }

    private fun onSpinnerChanged() {
        if (populating) return
        val res = resolutions.getOrNull(resolutionSpinner.selectedItemPosition) ?: return
        val fps = frameRates.getOrNull(fpsSpinner.selectedItemPosition) ?: settings.fps
        val codec = codecs.getOrNull(codecSpinner.selectedItemPosition) ?: settings.codec
        val next = settings.copy(width = res.first, height = res.second, fps = fps, codec = codec)
        if (next == settings) return
        val resChanged = next.width != settings.width || next.height != settings.height
        settings = next
        if (resChanged) {
            // Frame rates and codecs depend on the size.
            populateSettings()
        } else {
            applySettings()
        }
    }

    private fun commitSourceName() {
        val name = sourceName.text?.toString()?.trim().orEmpty().ifEmpty { StreamSettings.DEFAULT_SOURCE_NAME }
        if (name != settings.sourceName) {
            settings = settings.copy(sourceName = name)
            applySettings()
        }
    }

    private fun applySettings() {
        settings.save(this)
        val e = engine
        val sizeChanged = e != null && (e.settings.width != settings.width || e.settings.height != settings.height)
        val fpsChanged = e != null && e.settings.fps != settings.fps
        e?.settings = settings
        if (sizeChanged) {
            surfaceReady = false
            previewView.holder.setFixedSize(settings.width, settings.height)
        } else if (fpsChanged && surfaceReady) {
            e?.setPreviewSurface(previewView.holder.surface)
        }
        updateStatus()
    }

    private fun hideKeyboard() {
        getSystemService(InputMethodManager::class.java)?.hideSoftInputFromWindow(sourceName.windowToken, 0)
        sourceName.clearFocus()
    }

    @Suppress("DEPRECATION")
    private fun hideSystemBars() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.insetsController?.let {
                it.hide(WindowInsets.Type.systemBars())
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_FULLSCREEN or
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION)
        }
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent?): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK && panel.visibility == View.VISIBLE) {
            setPanelVisible(false)
            return true
        }
        if (keyCode == KeyEvent.KEYCODE_BACK && exposureOpen) {
            exposureOpen = false
            updateControls()
            return true
        }
        return super.onKeyDown(keyCode, event)
    }

    private fun codecLabel(c: VideoCodec) = when (c) {
        VideoCodec.H264 -> "H.264"
        VideoCodec.HEVC -> "HEVC"
    }

    // Camera controls

    // The tap path calls overlay.performClick() from the gesture detector.
    @SuppressLint("ClickableViewAccessibility")
    private fun setUpCameraControls() {
        val scale = ScaleGestureDetector(this, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(d: ScaleGestureDetector): Boolean {
                val c = caps ?: return false
                setControls(controls.copy(zoom = c.clampZoom(controls.zoom * d.scaleFactor)))
                return true
            }
        })
        val taps = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onDown(e: MotionEvent) = true
            override fun onSingleTapUp(e: MotionEvent): Boolean {
                overlay.performClick()
                onPreviewTap(e.x / overlay.width, e.y / overlay.height)
                return true
            }
        })
        overlay.setOnTouchListener { _, ev ->
            scale.onTouchEvent(ev)
            if (!scale.isInProgress) taps.onTouchEvent(ev)
            true
        }
        zoomButton.setOnClickListener { setControls(controls.copy(zoom = caps?.clampZoom(1f) ?: 1f)) }
        cameraButton.setOnClickListener { switchCamera() }
        focusLockButton.setOnClickListener { setControls(controls.copy(focusLocked = !controls.focusLocked)) }
        exposureLockButton.setOnClickListener { setControls(controls.copy(exposureLocked = !controls.exposureLocked)) }
        exposureButton.setOnClickListener { exposureOpen = !exposureOpen; updateControls() }
        gridButton.setOnClickListener { saveDisplaySettings(settings.copy(grid = !settings.grid)) }
        tallyModeButton.setOnClickListener {
            val modes = TallyMode.entries
            saveDisplaySettings(settings.copy(tallyMode = modes[(settings.tallyMode.ordinal + 1) % modes.size]))
        }
        exposureSeek.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar, progress: Int, fromUser: Boolean) {
                val c = caps ?: return
                if (fromUser) setControls(controls.copy(exposureComp = c.aeCompRange.lower + progress))
            }
            override fun onStartTrackingTouch(sb: SeekBar) {}
            override fun onStopTrackingTouch(sb: SeekBar) {}
        })
        exposureValue.setOnClickListener { setControls(controls.copy(exposureComp = 0)) }
    }

    private fun onPreviewTap(x: Float, y: Float) {
        if (panel.visibility == View.VISIBLE) { setPanelVisible(false); return }
        val c = caps ?: return
        if (!c.hasAf && c.maxAeRegions == 0) return
        val p = PointF(x.coerceIn(0f, 1f), y.coerceIn(0f, 1f))
        setControls(controls.copy(focusPoint = p))
        overlay.focusPoint = p
        main.removeCallbacks(hideFocusMarker)
        if (!controls.focusLocked) main.postDelayed(hideFocusMarker, FOCUS_MARKER_MS)
    }

    private val hideFocusMarker = Runnable { if (!controls.focusLocked) overlay.focusPoint = null }

    private fun setControls(c: CameraControls) {
        val unlocked = controls.focusLocked && !c.focusLocked
        controls = c
        engine?.setControls(c, displayRotationDeg())
        if (unlocked) main.postDelayed(hideFocusMarker, FOCUS_MARKER_MS)
        updateControls()
    }

    private fun switchCamera() {
        val e = engine ?: return
        val next = otherFacing() ?: return
        controls = CameraControls()
        main.removeCallbacks(hideFocusMarker)
        settings = settings.copy(facing = next)
        e.setFacing(next, controls)
        caps = e.caps
        if (e.streaming) {
            settings.save(this)
        } else {
            // Sizes, frame rates and codecs depend on the camera.
            populateSettings()
        }
        updateControls()
    }

    private fun otherFacing(): Facing? = facings.firstOrNull { it != settings.facing }

    /** While streaming the stream size cannot change, so the other camera must offer it. */
    private fun canSwitchCamera(): Boolean {
        val next = otherFacing() ?: return false
        val e = engine ?: return false
        if (!e.streaming) return true
        val other = otherCaps?.takeIf { it.facing == next } ?: CameraCaps(this, next).also { otherCaps = it }
        return other.supportsSize(settings.width, settings.height) &&
            other.supportsFps(settings.width, settings.height, settings.fps)
    }

    /** Grid and tally display: apply at once, streaming or not. */
    private fun saveDisplaySettings(next: StreamSettings) {
        settings = next
        settings.save(this)
        engine?.let { it.settings = it.settings.copy(grid = next.grid, tallyMode = next.tallyMode) }
        updateControls()
    }

    private fun updateControls() {
        val c = caps
        val show = engine != null && c != null && panel.visibility != View.VISIBLE
        controlsColumn.visibility = if (show) View.VISIBLE else View.GONE
        overlay.grid = settings.grid
        overlay.focusLocked = controls.focusLocked
        // The marker shows after a tap and stays while focus is locked.
        when {
            controls.focusPoint == null -> overlay.focusPoint = null
            controls.focusLocked -> overlay.focusPoint = controls.focusPoint
        }
        showTally()
        if (c == null) { exposureRow.visibility = View.GONE; return }

        val zoomable = c.zoomRange.upper > c.zoomRange.lower
        zoomButton.visibility = if (zoomable) View.VISIBLE else View.GONE
        zoomButton.text = String.format(Locale.US, "%.1fx", c.clampZoom(controls.zoom))

        cameraButton.visibility = if (facings.size > 1) View.VISIBLE else View.GONE
        cameraButton.setText(if (settings.facing == Facing.FRONT) R.string.front else R.string.rear)
        cameraButton.isEnabled = canSwitchCamera()

        focusLockButton.visibility = if (c.hasAf) View.VISIBLE else View.GONE
        focusLockButton.isActivated = controls.focusLocked

        exposureLockButton.visibility = if (c.aeLockAvailable) View.VISIBLE else View.GONE
        exposureLockButton.isActivated = controls.exposureLocked

        val hasComp = c.aeCompRange.upper > c.aeCompRange.lower && c.aeCompStep > 0f
        exposureButton.visibility = if (hasComp) View.VISIBLE else View.GONE
        if (!hasComp) exposureOpen = false
        exposureButton.isActivated = exposureOpen
        exposureRow.visibility = if (show && exposureOpen) View.VISIBLE else View.GONE
        if (hasComp) {
            exposureSeek.max = c.aeCompRange.upper - c.aeCompRange.lower
            exposureSeek.progress = controls.exposureComp.coerceIn(c.aeCompRange.lower, c.aeCompRange.upper) - c.aeCompRange.lower
            exposureValue.text = String.format(Locale.US, "%+.1f %s", controls.exposureComp * c.aeCompStep, getString(R.string.ev_unit))
        }

        gridButton.isActivated = settings.grid
        tallyModeButton.setText(
            when (settings.tallyMode) {
                TallyMode.BORDER -> R.string.tally_border
                TallyMode.FULL_SCREEN -> R.string.tally_full_screen
                TallyMode.OFF -> R.string.tally_off
            },
        )
    }

    @Suppress("DEPRECATION")
    private fun displayRotationDeg(): Int {
        val r = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) display?.rotation else windowManager.defaultDisplay.rotation
        return when (r) {
            Surface.ROTATION_90 -> 90
            Surface.ROTATION_180 -> 180
            Surface.ROTATION_270 -> 270
            else -> 0
        }
    }

    companion object {
        private const val REQ_PERMS = 1
        private const val FOCUS_MARKER_MS = 1500L
        private const val TINT_ALPHA = 0x66
    }
}
