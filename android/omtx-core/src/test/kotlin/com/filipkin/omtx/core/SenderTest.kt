package com.filipkin.omtx.core

import java.io.BufferedInputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Real sockets on loopback against [OmtxSender]. */
class SenderTest {
    private val keyRequests = AtomicInteger()
    private val tallies = LinkedBlockingQueue<Tally>()
    private val sender = OmtxSender(
        SenderInfo.toXml("omtx camera", "omtx", "test"),
        object : OmtxSender.Listener {
            override fun onKeyframeRequest() { keyRequests.incrementAndGet() }
            override fun onTallyChanged(tally: Tally) { tallies.add(tally) }
        },
        portStart = freePort(),
        portEnd = 6600,
    )

    @AfterTest fun tearDown() = sender.stop()

    private class Client(port: Int) {
        val socket = Socket().apply { connect(InetSocketAddress("127.0.0.1", port), 2000) }
        val frames = LinkedBlockingQueue<Frame>()
        init {
            Thread {
                try {
                    val r = FrameReader(BufferedInputStream(socket.getInputStream()))
                    while (true) frames.add(r.read() ?: break)
                } catch (_: Exception) {}
            }.apply { isDaemon = true; start() }
        }
        fun send(xml: String) { socket.getOutputStream().write(Wire.metadataFrame(xml)); socket.getOutputStream().flush() }
        fun next(ms: Long = 2000): Frame? = frames.poll(ms, TimeUnit.MILLISECONDS)
        fun nextOfType(t: Int, ms: Long = 2000): Frame? {
            val deadline = System.currentTimeMillis() + ms
            while (true) {
                val left = deadline - System.currentTimeMillis()
                if (left <= 0) return null
                val f = next(left) ?: return null
                if (f.frameType == t) return f
            }
        }
    }

    private fun waitFor(cond: () -> Boolean) {
        val deadline = System.currentTimeMillis() + 3000
        while (!cond()) { check(System.currentTimeMillis() < deadline) { "timeout" }; Thread.sleep(10) }
    }

    private val keyAu = hex("00 00 00 01 67 64 00 28 AC 00 00 00 01 68 EE 3C 80 00 00 00 01 65 88 84")
    private val pAu = hex("00 00 00 01 41 9A 21")

    private fun pushVideo(key: Boolean, ts: Long) =
        sender.sendVideo(VideoCodec.H264, 1280, 720, 30, 1, key, ts, if (key) keyAu else pAu)

    @Test
    fun connectSubscribeAndReceiveKeyframeFirst() {
        val port = sender.start()
        assertTrue(port in 6400..6600)
        val c = Client(port)

        // On connect, like OMTSend.OnAccept: OMTInfo then the current tally, no subscription needed.
        val info = c.next()!!
        assertEquals(Wire.FRAME_METADATA, info.frameType)
        assertEquals("<OMTInfo ProductName=\"omtx camera\" Manufacturer=\"omtx\" Version=\"test\" />", info.xml)
        assertEquals(OmtStrings.TALLY_NONE, c.next()!!.xml)
        waitFor { sender.connectionCount == 1 }

        // No data before subscribing.
        pushVideo(true, 1)
        assertNull(c.next(200))

        c.send(OmtStrings.SUBSCRIBE_VIDEO)
        c.send(OmtStrings.SUBSCRIBE_AUDIO)
        c.send(OmtStrings.SUBSCRIBE_METADATA)
        waitFor { keyRequests.get() >= 1 }    // subscribe triggers an encoder keyframe

        pushVideo(false, 2)                   // dropped: waiting for keyframe
        pushVideo(true, 3)
        pushVideo(false, 4)
        val v1 = c.nextOfType(Wire.FRAME_VIDEO)!!
        assertEquals(3L, v1.timestamp)
        val b = v1.le()
        assertEquals(Wire.CODEC_H264, b.int)
        assertEquals(1280, b.int); assertEquals(720, b.int); assertEquals(30, b.int); assertEquals(1, b.int)
        assertEquals(16f / 9f, b.float)
        assertEquals(Wire.FLAG_KEYFRAME, b.int)
        assertEquals(709, b.int)
        assertEquals(0, v1.metadataLength)
        assertEquals(32 + keyAu.size, v1.data.size)
        val v2 = c.nextOfType(Wire.FRAME_VIDEO)!!
        assertEquals(4L, v2.timestamp)
        assertEquals(0, v2.le().getInt(24))

        waitFor { sender.hasAudioSubscribers }
        sender.sendAudio(48000, 2, 4, 5L, floatArrayOf(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f))
        val a = c.nextOfType(Wire.FRAME_AUDIO)!!
        assertEquals(Wire.CODEC_FPA1, a.le().getInt(0))

        val before = keyRequests.get()
        c.send(OmtStrings.KEYFRAME_REQUEST)
        waitFor { keyRequests.get() == before + 1 }
        c.socket.close()
        waitFor { sender.connectionCount == 0 }
    }

    @Test
    fun tallyIsCombinedAndBroadcastToMetadataSubscribers() {
        val port = sender.start()
        val a = Client(port); val b = Client(port)
        repeat(2) { a.next(); b.next() }
        a.send(OmtStrings.SUBSCRIBE_METADATA)
        b.send(OmtStrings.SUBSCRIBE_METADATA)
        waitFor { sender.connectionCount == 2 }
        Thread.sleep(100)

        a.send(OmtStrings.TALLY_PREVIEW)
        assertEquals(OmtStrings.TALLY_PREVIEW, a.nextOfType(Wire.FRAME_METADATA)!!.xml)
        assertEquals(OmtStrings.TALLY_PREVIEW, b.nextOfType(Wire.FRAME_METADATA)!!.xml)
        b.send(OmtStrings.TALLY_PROGRAM)
        assertEquals(OmtStrings.TALLY_PREVIEWPROGRAM, a.nextOfType(Wire.FRAME_METADATA)!!.xml)
        assertEquals(OmtStrings.TALLY_PREVIEWPROGRAM, b.nextOfType(Wire.FRAME_METADATA)!!.xml)
        assertEquals(Tally(true, true), sender.lastTally)

        // A new connection is told the combined tally at once.
        val c = Client(port)
        c.next()
        assertEquals(OmtStrings.TALLY_PREVIEWPROGRAM, c.next()!!.xml)

        // Disconnecting the program source drops program from the combined tally.
        b.socket.close()
        assertEquals(OmtStrings.TALLY_PREVIEW, a.nextOfType(Wire.FRAME_METADATA)!!.xml)
        assertEquals(Tally(true, false), sender.lastTally)
        assertNotNull(tallies.poll())
    }

    @Test
    fun qualityHintCapsCeiling() {
        val port = sender.start()
        val a = Client(port)
        a.send(OmtStrings.SUBSCRIBE_VIDEO)
        a.send("<OMTSettings Quality=\"Low\" />")
        waitFor { sender.ceilingBps == 8_000_000 }
        assertEquals(8_000_000, sender.targetBps)
        a.send("<OMTSettings Quality=\"Default\" />")
        waitFor { sender.ceilingBps == BitrateController.DEFAULT_CEILING_BPS }
    }

    @Test
    fun connectionCountIsReportedOnConnectAndDisconnect() {
        // The camera app encodes only while this count is above zero.
        val counts = LinkedBlockingQueue<Int>()
        val s = OmtxSender(SenderInfo.toXml("t", "t", "t"), object : OmtxSender.Listener {
            override fun onConnectionsChanged(count: Int) { counts.add(count) }
        }, portStart = freePort())
        try {
            val port = s.start()
            val a = Client(port)
            assertEquals(1, counts.poll(2, TimeUnit.SECONDS))
            val b = Client(port)
            assertEquals(2, counts.poll(2, TimeUnit.SECONDS))
            a.socket.close()
            assertEquals(1, counts.poll(2, TimeUnit.SECONDS))
            b.socket.close()
            assertEquals(0, counts.poll(2, TimeUnit.SECONDS))
        } finally {
            s.stop()
        }
    }

    @Test
    fun slowReceiverOverflowsTheInFlightLimit() {
        // Small kernel buffers both ends, receiver never reads: frames stuck in write() or the
        // queue must count as in flight, so the 4-frame limit drops and asks for a keyframe.
        val hooked = AtomicInteger()
        val s = OmtxSender(SenderInfo.toXml("t", "t", "t"), object : OmtxSender.Listener {
            override fun onKeyframeRequest() { keyRequests.incrementAndGet() }
        }, portStart = freePort(), configureSocket = { hooked.incrementAndGet() })
        try {
            val port = s.start()
            val sock = Socket()
            sock.receiveBufferSize = 4096
            sock.connect(InetSocketAddress("127.0.0.1", port), 2000)
            sock.getOutputStream().write(Wire.metadataFrame(OmtStrings.SUBSCRIBE_VIDEO))
            waitFor { keyRequests.get() >= 1 }
            assertEquals(1, hooked.get())
            val big = hex("00 00 00 01 65") + ByteArray(256 * 1024) { 0x55 }
            val small = hex("00 00 00 01 41") + ByteArray(256 * 1024) { 0x55 }
            s.sendVideo(VideoCodec.H264, 1280, 720, 30, 1, true, 1, big)
            for (i in 2..20) { s.sendVideo(VideoCodec.H264, 1280, 720, 30, 1, false, i.toLong(), small); Thread.sleep(20) }
            assertTrue(s.framesDropped > 0, "no drops with a stalled receiver")
            assertTrue(keyRequests.get() >= 2, "drop did not request a keyframe")
            sock.close()
        } finally {
            s.stop()
        }
    }

    companion object {
        /** Start each test on its own port so parallel/lingering sockets do not collide. */
        fun freePort(): Int {
            for (p in 6400..6600) {
                try { ServerSocket(p).close(); return p } catch (_: Exception) {}
            }
            error("no port")
        }
    }
}
