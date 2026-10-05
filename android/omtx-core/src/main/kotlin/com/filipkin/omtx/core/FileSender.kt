package com.filipkin.omtx.core

import java.io.File
import kotlin.system.exitProcess

/**
 * Test source: loops an Annex B .h264/.h265 file through the same [OmtxSender] the camera
 * app uses. No mDNS; connect to the printed port.
 *
 *   java -jar omtx-file-sender.jar <file.h264> <fps> <width> <height> [port]
 *
 * Receiver keyframe requests cannot be honoured from a file, so new subscribers start at the
 * next keyframe in the file (its GOP length).
 */
object FileSender {
    @JvmStatic
    fun main(args: Array<String>) {
        if (args.size < 4) {
            System.err.println("usage: java -jar omtx-file-sender.jar <file.h264|.h265> <fps> <width> <height> [port]")
            exitProcess(2)
        }
        val file = File(args[0])
        val fps = args[1].toInt()
        val width = args[2].toInt()
        val height = args[3].toInt()
        val port = args.getOrNull(4)?.toInt()

        val bytes = file.readBytes()
        val ext = file.extension.lowercase()
        val codec = when {
            ext == "h265" || ext == "hevc" || ext == "265" -> VideoCodec.HEVC
            ext == "h264" || ext == "avc" || ext == "264" -> VideoCodec.H264
            else -> AnnexB.sniff(bytes) ?: VideoCodec.H264
        }
        val aus = AccessUnits.split(bytes, codec)
        val keys = aus.count { it.keyframe }
        require(aus.isNotEmpty()) { "no pictures found in ${file.name}" }
        println("${file.name}: $codec, ${aus.size} pictures, $keys keyframes")

        var keyRequests = 0L
        val sender = OmtxSender(
            SenderInfo.toXml("omtx file sender", "omtx", VERSION),
            object : OmtxSender.Listener {
                override fun onKeyframeRequest() { keyRequests++ }
                override fun onConnectionsChanged(count: Int) { println("receivers $count") }
                override fun onTallyChanged(tally: Tally) { println("tally preview=${tally.preview} program=${tally.program}") }
            },
            portStart = port ?: OmtxSender.PORT_START,
            portEnd = port ?: OmtxSender.PORT_END,
            log = { println(it) },
        )
        val bound = sender.start()
        println("port $bound")
        System.out.flush()
        Runtime.getRuntime().addShutdownHook(Thread { sender.stop() })

        val intervalNs = 1_000_000_000L / fps
        val t0 = System.nanoTime()
        var n = 0L
        var lastReport = t0
        var i = 0
        while (true) {
            val due = t0 + n * intervalNs
            val wait = due - System.nanoTime()
            if (wait > 0) Thread.sleep(wait / 1_000_000L, (wait % 1_000_000L).toInt())
            val au = aus[i]
            // 100 ns units on the same monotonic clock as the pacing.
            val ts = (due - t0) / 100L
            sender.sendVideo(codec, width, height, fps, 1, au.keyframe, ts, au.data)
            n++
            i = (i + 1) % aus.size
            val now = System.nanoTime()
            if (now - lastReport >= 5_000_000_000L) {
                lastReport = now
                println("frames $n receivers ${sender.connectionCount} dropped ${sender.framesDropped} keyframe-requests $keyRequests target ${sender.targetBps / 1000} kbps")
                System.out.flush()
            }
        }
    }

    const val VERSION = "0.1.0"
}
