package com.filipkin.omtx.core

import java.io.ByteArrayInputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNull

fun hex(s: String): ByteArray =
    s.split(Regex("\\s+")).filter { it.isNotEmpty() }.map { it.toInt(16).toByte() }.toByteArray()

class WireTest {
    /** PROTOCOL-OMTX.md §7, byte for byte. */
    @Test
    fun wireExampleKeyframe1080p60() {
        val payload = ByteArray(61440)
        hex("00 00 00 01 67").copyInto(payload)
        val ts = 0x0807060504030201L
        val frame = Wire.videoFrame(
            Wire.CODEC_H264, 1920, 1080, 60, 1, Wire.FLAG_KEYFRAME, Wire.COLORSPACE_709, ts, payload,
        )
        val expected = hex(
            """
            01
            02
            01 02 03 04 05 06 07 08
            00 00
            20 F0 00 00
            48 32 36 34
            80 07 00 00
            38 04 00 00
            3C 00 00 00
            01 00 00 00
            39 8E E3 3F
            20 00 00 00
            C5 02 00 00
            00 00 00 01 67
            """
        )
        assertContentEquals(expected, frame.copyOfRange(0, expected.size))
        assertEquals(16 + 32 + 61440, frame.size)
        assertEquals("H264", Wire.fourCC(Wire.CODEC_H264))
        assertEquals("HEVC", Wire.fourCC(Wire.CODEC_HEVC))
        assertEquals("FPA1", Wire.fourCC(Wire.CODEC_FPA1))
    }

    @Test
    fun metadataFrameLayout() {
        val xml = OmtStrings.TALLY_PROGRAM
        val f = Wire.metadataFrame(xml)
        val utf8 = xml.toByteArray(Charsets.UTF_8)
        val b = ByteBuffer.wrap(f).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(1, b.get().toInt())          // Version
        assertEquals(1, b.get().toInt())          // FrameType = Metadata
        assertEquals(0L, b.long)                  // Timestamp
        assertEquals(0, b.short.toInt())          // MetadataLength
        // C# OMTBuffer.FromMetadata writes the bare UTF-8 bytes (see Wire.METADATA_NUL_TERMINATED).
        assertEquals(utf8.size, b.int)
        assertContentEquals(utf8, f.copyOfRange(16, f.size))
        assertEquals(16 + utf8.size, f.size)

        // Spec text variant: NUL-terminated, DataLength = bytes + 1.
        val n = Wire.metadataFrame(xml, nulTerminated = true)
        val nb = ByteBuffer.wrap(n).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(utf8.size + 1, nb.getInt(12))
        assertEquals(0, n.last().toInt())
        assertEquals(16 + utf8.size + 1, n.size)
    }

    @Test
    fun metadataInputAcceptsBothForms() {
        val plain = Wire.metadataFrame(OmtStrings.SUBSCRIBE_VIDEO, nulTerminated = false)
        val nul = Wire.metadataFrame(OmtStrings.SUBSCRIBE_VIDEO, nulTerminated = true)
        val r = FrameReader(ByteArrayInputStream(plain + nul))
        assertEquals(OmtStrings.SUBSCRIBE_VIDEO, r.read()!!.xml)
        assertEquals(OmtStrings.SUBSCRIBE_VIDEO, r.read()!!.xml)
        assertNull(r.read())
    }

    @Test
    fun audioFramePlanarWithSilentChannelSkipped() {
        val spc = 4
        // 3 channels planar; channel 1 silent.
        val planar = floatArrayOf(0.5f, -0.5f, 0.25f, 1f, 0f, 0f, 0f, 0f, 0.1f, 0.2f, 0.3f, 0.4f)
        val f = Wire.audioFrame(48000, 3, spc, 1234L, planar)
        val b = ByteBuffer.wrap(f).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(1, b.get().toInt())
        assertEquals(4, b.get().toInt())                 // Audio
        assertEquals(1234L, b.long)
        assertEquals(0, b.short.toInt())
        assertEquals(24 + 2 * spc * 4, b.int)            // two active channels
        assertEquals(Wire.CODEC_FPA1, b.int)
        assertEquals(48000, b.int)
        assertEquals(spc, b.int)
        assertEquals(3, b.int)
        assertEquals(0b101, b.int)                       // ActiveChannels
        assertEquals(0, b.int)                           // Reserved1
        val got = FloatArray(8) { b.float }
        assertContentEquals(floatArrayOf(0.5f, -0.5f, 0.25f, 1f, 0.1f, 0.2f, 0.3f, 0.4f), got)
        assertEquals(f.size, b.position())
    }

    @Test
    fun deinterleave() {
        val inter = floatArrayOf(1f, 10f, 2f, 20f, 3f, 30f)
        val out = FloatArray(6)
        Wire.deinterleave(inter, 2, 3, out)
        assertContentEquals(floatArrayOf(1f, 2f, 3f, 10f, 20f, 30f), out)
    }

    @Test
    fun readerRoundTripVideo() {
        val payload = hex("00 00 00 01 65 88 84")
        val f = Wire.videoFrame(Wire.CODEC_HEVC, 1280, 720, 30, 1, 0, 709, 99L, payload)
        val r = FrameReader(ByteArrayInputStream(f)).read()!!
        assertEquals(Wire.FRAME_VIDEO, r.frameType)
        assertEquals(99L, r.timestamp)
        val b = r.le()
        assertEquals(Wire.CODEC_HEVC, b.int)
        assertEquals(1280, b.int)
        assertEquals(720, b.int)
        assertEquals(32 + payload.size, r.data.size)
        assertContentEquals(payload, r.data.copyOfRange(32, r.data.size))
    }
}
