package com.filipkin.omtx.core

import java.io.BufferedInputStream
import java.io.IOException
import java.io.OutputStream
import java.net.BindException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean

/**
 * omtx sender: the TCP server side of OMT. Receivers connect, subscribe, and get frames.
 * Behaviour follows OMTSend + OMTChannel in libomtnet, plus the omtx sender rules (§4).
 *
 * Threads: one accept thread, and per connection one reader and one writer thread. Encoded
 * frames are serialised once and the same array is queued on every connection.
 */
class OmtxSender(
    private val senderInfoXml: String,
    private val listener: Listener = object : Listener {},
    userCeilingBps: Int = BitrateController.DEFAULT_CEILING_BPS,
    floorBps: Int = BitrateController.DEFAULT_FLOOR_BPS,
    private val portStart: Int = PORT_START,
    private val portEnd: Int = PORT_END,
    private val clockMs: () -> Long = { System.nanoTime() / 1_000_000L },
    private val log: (String) -> Unit = {},
    /**
     * Called for every accepted socket after the portable options are set. The Android app
     * uses it for TCP_NOTSENT_LOWAT, which plain Java cannot set; the JVM tool leaves it empty.
     */
    private val configureSocket: (Socket) -> Unit = {},
) {
    interface Listener {
        /** Make the next encoded frame a keyframe. */
        fun onKeyframeRequest() {}
        fun onBitrateChanged(bps: Int) {}
        fun onTallyChanged(tally: Tally) {}
        fun onConnectionsChanged(count: Int) {}
    }

    private val connections = CopyOnWriteArrayList<Connection>()
    private val controller = BitrateController(userCeilingBps, floorBps)
    private var server: ServerSocket? = null
    private var acceptThread: Thread? = null
    private val running = AtomicBoolean(false)

    @Volatile var lastTally: Tally = Tally.NONE
        private set

    @Volatile var port: Int = -1
        private set

    val connectionCount: Int get() = connections.size
    val targetBps: Int get() = synchronized(controller) { controller.targetBps }
    val ceilingBps: Int get() = synchronized(controller) { controller.ceilingBps }

    @Volatile var framesDropped: Long = 0
        private set

    /** Binds the first free port in [portStart]..[portEnd] and starts accepting. */
    fun start(): Int {
        check(running.compareAndSet(false, true)) { "already started" }
        var bound: ServerSocket? = null
        for (p in portStart..portEnd) {
            val s = ServerSocket()
            try {
                s.bind(InetSocketAddress(p), 5)
                bound = s
                break
            } catch (e: BindException) {
                s.close()
            } catch (e: SocketException) {
                s.close()
            }
        }
        val s = bound ?: run { running.set(false); throw IOException("no free port in $portStart..$portEnd") }
        server = s
        port = s.localPort
        acceptThread = Thread({ acceptLoop(s) }, "omtx-accept").apply { isDaemon = true; start() }
        return port
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        try { server?.close() } catch (_: IOException) {}
        server = null
        for (c in connections) c.close()
        connections.clear()
        acceptThread?.join(1000)
        acceptThread = null
    }

    fun setUserCeiling(bps: Int) {
        val changed: Boolean
        val target: Int
        synchronized(controller) {
            changed = controller.setUserCeiling(bps)
            target = controller.targetBps
        }
        if (changed) listener.onBitrateChanged(target)
    }

    /**
     * Send one encoded access unit. `au` must already be self-contained Annex B with 4-byte
     * start codes when it is a keyframe (see [AnnexB.makeSelfContained]).
     * `timestamp` is in 100 ns units.
     */
    fun sendVideo(
        codec: VideoCodec,
        width: Int,
        height: Int,
        frameRateN: Int,
        frameRateD: Int,
        keyframe: Boolean,
        timestamp: Long,
        au: ByteArray,
        colorSpace: Int = if (height >= 720) Wire.COLORSPACE_709 else Wire.COLORSPACE_601,
    ) {
        val now = clockMs()
        var frame: ByteArray? = null
        var maxInFlight = 0
        var dropped = false
        var wantKeyframe = false
        for (c in connections) {
            val inFlight = c.videoInFlight
            val d = c.gate.offer(keyframe, inFlight, now)
            if (d.requestKeyframe) wantKeyframe = true
            if (c.gate.subscribed && inFlight > maxInFlight) maxInFlight = inFlight
            when (d.outcome) {
                VideoGate.Outcome.SEND -> {
                    if (frame == null) {
                        frame = Wire.videoFrame(
                            codec.fourCC, width, height, frameRateN, frameRateD,
                            if (keyframe) Wire.FLAG_KEYFRAME else 0, colorSpace, timestamp, au,
                        )
                    }
                    c.enqueueVideo(frame)
                }
                VideoGate.Outcome.OVERFLOW -> { dropped = true; framesDropped++ }
                else -> {}
            }
        }
        var changed: Boolean
        val target: Int
        synchronized(controller) {
            changed = if (dropped) controller.onDrop(now) else false
            changed = controller.tick(now, maxInFlight) || changed
            target = controller.targetBps
        }
        if (changed) listener.onBitrateChanged(target)
        if (wantKeyframe) listener.onKeyframeRequest()
    }

    /** True if any receiver subscribed to audio (skip capture work otherwise). */
    val hasAudioSubscribers: Boolean get() = connections.any { it.subAudio }

    /** Planar float samples, channel after channel. `timestamp` in 100 ns units. */
    fun sendAudio(sampleRate: Int, channels: Int, samplesPerChannel: Int, timestamp: Long, planar: FloatArray) {
        var frame: ByteArray? = null
        for (c in connections) {
            if (!c.subAudio) continue
            if (frame == null) frame = Wire.audioFrame(sampleRate, channels, samplesPerChannel, timestamp, planar)
            c.enqueueAudio(frame)
        }
    }

    /** Broadcast an application metadata frame to metadata subscribers. */
    fun sendMetadata(xml: String) {
        val frame = Wire.metadataFrame(xml)
        for (c in connections) if (c.subMeta) c.enqueueMetadata(frame)
    }

    private fun acceptLoop(s: ServerSocket) {
        while (running.get()) {
            val sock = try { s.accept() } catch (e: IOException) { break }
            try {
                val c = Connection(sock)
                // OMTSend.OnAccept order: sender info, tally, then add to the list.
                c.enqueueMetadata(Wire.metadataFrame(senderInfoXml))
                synchronized(this) {
                    c.enqueueMetadata(Wire.metadataFrame(lastTally.toXml()))
                    connections.add(c)
                }
                c.start()
                log("connect ${sock.remoteSocketAddress}")
                listener.onConnectionsChanged(connections.size)
                updateTally()
            } catch (e: IOException) {
                try { sock.close() } catch (_: IOException) {}
            }
        }
    }

    private fun onClosed(c: Connection) {
        if (connections.remove(c)) {
            log("disconnect ${c.remote}")
            listener.onConnectionsChanged(connections.size)
            updateTally()
            updateQuality()
        }
    }

    private fun updateTally() {
        // Compute, broadcast and notify under one lock so two reader threads changing tally at
        // the same moment cannot deliver the combined states out of order.
        synchronized(this) {
            val t = Tally.combine(connections.map { it.tally })
            if (t == lastTally) return
            lastTally = t
            // OMTSend.OnTallyChanged -> SendMetadata: metadata subscribers only.
            val frame = Wire.metadataFrame(t.toXml())
            for (c in connections) if (c.subMeta) c.enqueueMetadata(frame)
            listener.onTallyChanged(t)
        }
    }

    private fun updateQuality() {
        val q = Quality.highest(connections.filter { it.gate.subscribed }.map { it.quality })
        val changed: Boolean
        val target: Int
        synchronized(controller) {
            changed = controller.setQualityCap(q.capBps)
            target = controller.targetBps
        }
        if (changed) listener.onBitrateChanged(target)
    }

    private fun handle(c: Connection, xml: String) {
        when (val cmd = Command.parse(xml)) {
            Command.SubscribeVideo -> {
                if (c.gate.subscribe(clockMs())) listener.onKeyframeRequest()
                updateQuality()
            }
            Command.SubscribeAudio -> c.subAudio = true
            Command.SubscribeMetadata -> c.subMeta = true
            is Command.SetTally -> {
                if (cmd.tally != c.tally) { c.tally = cmd.tally; updateTally() }
            }
            // §4.4: preview has no meaning for H.264/HEVC; keep sending full frames.
            Command.PreviewOn, Command.PreviewOff -> {}
            Command.KeyframeRequest -> listener.onKeyframeRequest()
            is Command.SuggestQuality -> {
                if (cmd.quality != null) { c.quality = cmd.quality; updateQuality() }
            }
            is Command.Other -> log("metadata from ${c.remote}: $xml")
        }
    }

    private class Item(val bytes: ByteArray, val type: Int)

    inner class Connection(private val socket: Socket) {
        val remote: String = socket.remoteSocketAddress.toString()
        val gate = VideoGate()
        @Volatile var subAudio = false
        @Volatile var subMeta = false
        @Volatile var tally: Tally = Tally.NONE
        @Volatile var quality: Quality = Quality.Default

        private val lock = Object()
        private val queue = ArrayDeque<Item>()
        // Video in flight = handed to the writer and not yet through socket write(): counted
        // from enqueue until write() returns, so a frame stuck in a slow write still counts.
        private var videoCount = 0
        private var audioCount = 0
        private var metaCount = 0
        private val closed = AtomicBoolean(false)
        private val out: OutputStream

        init {
            socket.tcpNoDelay = true
            socket.keepAlive = true
            // Small kernel buffer so a slow link backs up into our queue, where the 4-frame
            // limit sees it, instead of hiding ~250 ms of video in the socket.
            try { socket.sendBufferSize = SEND_BUFFER } catch (_: SocketException) {}
            try { configureSocket(socket) } catch (e: Exception) { log("configureSocket: ${e.message}") }
            out = socket.getOutputStream()
        }

        val videoInFlight: Int get() = synchronized(lock) { videoCount }

        fun start() {
            Thread({ readLoop() }, "omtx-read $remote").apply { isDaemon = true; start() }
            Thread({ writeLoop() }, "omtx-write $remote").apply { isDaemon = true; start() }
        }

        fun enqueueVideo(frame: ByteArray) = synchronized(lock) {
            queue.addLast(Item(frame, Wire.FRAME_VIDEO)); videoCount++; lock.notifyAll()
        }

        /** Audio has its own bound so a backed-up link drops audio, never blocks video. */
        fun enqueueAudio(frame: ByteArray) = synchronized(lock) {
            if (audioCount >= MAX_AUDIO_QUEUED) return@synchronized
            queue.addLast(Item(frame, Wire.FRAME_AUDIO)); audioCount++; lock.notifyAll()
        }

        fun enqueueMetadata(frame: ByteArray) = synchronized(lock) {
            if (metaCount >= MAX_METADATA_QUEUED) return@synchronized
            queue.addLast(Item(frame, Wire.FRAME_METADATA)); metaCount++; lock.notifyAll()
        }

        private fun readLoop() {
            try {
                val r = FrameReader(BufferedInputStream(socket.getInputStream(), 65536))
                while (!closed.get()) {
                    val f = r.read() ?: break
                    if (f.frameType == Wire.FRAME_METADATA) handle(this, f.xml)
                }
            } catch (e: IOException) {
                if (!closed.get()) log("read $remote: ${e.message}")
            } finally {
                close()
            }
        }

        private fun writeLoop() {
            try {
                while (true) {
                    val item: Item
                    synchronized(lock) {
                        while (queue.isEmpty() && !closed.get()) lock.wait()
                        if (closed.get()) return
                        item = queue.removeFirst()
                        // Video stays counted until written; audio/metadata free their slot now.
                        when (item.type) {
                            Wire.FRAME_AUDIO -> audioCount--
                            Wire.FRAME_METADATA -> metaCount--
                        }
                    }
                    try {
                        out.write(item.bytes)
                    } finally {
                        if (item.type == Wire.FRAME_VIDEO) synchronized(lock) { videoCount-- }
                    }
                }
            } catch (e: IOException) {
                if (!closed.get()) log("write $remote: ${e.message}")
            } catch (e: InterruptedException) {
                // closing
            } finally {
                close()
            }
        }

        fun close() {
            if (!closed.compareAndSet(false, true)) return
            try { socket.close() } catch (_: IOException) {}
            synchronized(lock) { queue.clear(); lock.notifyAll() }
            onClosed(this)
        }
    }

    companion object {
        const val PORT_START = 6400
        const val PORT_END = 6600
        const val SEND_BUFFER = 32 * 1024
        /** IPPROTO_TCP option 25 on Linux/Android. */
        const val TCP_NOTSENT_LOWAT = 25
        const val NOTSENT_LOWAT_BYTES = 16 * 1024
        const val MAX_AUDIO_QUEUED = 8
        const val MAX_METADATA_QUEUED = 60
    }
}
