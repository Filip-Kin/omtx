package com.filipkin.omtx.core

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertSame
import kotlin.test.assertTrue

class AnnexBTest {
    private val sps = hex("67 64 00 28 AC")
    private val pps = hex("68 EE 3C 80")
    private val idr = hex("65 88 84 00 33")
    private val p = hex("41 9A 21 6C")
    private val aud = hex("09 F0")
    private fun sc4(vararg nals: ByteArray) = nals.fold(ByteArray(0)) { acc, n -> acc + hex("00 00 00 01") + n }

    @Test
    fun threeByteStartCodesBecomeFour() {
        val mixed = hex("00 00 01") + sps + hex("00 00 00 01") + pps + hex("00 00 01") + idr
        val out = AnnexB.toFourByteStartCodes(mixed, VideoCodec.H264)
        assertContentEquals(sc4(sps, pps, idr), out)
        val canonical = sc4(sps, pps, idr)
        assertSame(canonical, AnnexB.toFourByteStartCodes(canonical, VideoCodec.H264))
    }

    @Test
    fun nalTypes() {
        val nals = AnnexB.split(sc4(aud, sps, pps, idr), VideoCodec.H264)
        assertEquals(listOf(9, 7, 8, 5), nals.map { it.type })
        assertTrue(AnnexB.isKeyframe(sc4(idr), VideoCodec.H264))
        assertFalse(AnnexB.isKeyframe(sc4(p), VideoCodec.H264))
    }

    @Test
    fun keyframeGetsParameterSetsAfterAud() {
        val config = sc4(sps, pps)
        val (out, key) = AnnexB.makeSelfContained(sc4(aud, idr), VideoCodec.H264, config)
        assertTrue(key)
        assertContentEquals(sc4(aud, sps, pps, idr), out)
        val (out2, _) = AnnexB.makeSelfContained(hex("00 00 01") + idr, VideoCodec.H264, config)
        assertContentEquals(sc4(sps, pps, idr), out2)
    }

    @Test
    fun keyframeWithInlineParameterSetsIsKept() {
        val au = sc4(sps, pps, idr)
        val (out, key) = AnnexB.makeSelfContained(au, VideoCodec.H264, sc4(hex("67 42 00 1F"), hex("68 CE")))
        assertTrue(key)
        assertSame(au, out)
    }

    @Test
    fun nonKeyframeUntouched() {
        val au = sc4(p)
        val (out, key) = AnnexB.makeSelfContained(au, VideoCodec.H264, sc4(sps, pps))
        assertFalse(key)
        assertSame(au, out)
    }

    @Test
    fun hevcIrapAndParameterSets() {
        val vps = hex("40 01 0C 01")
        val hsps = hex("42 01 01 01")
        val hpps = hex("44 01 C1 72")
        val idrW = hex("26 01 AF 06")   // type 19 IDR_W_RADL
        val cra = hex("2A 01 AF 06")    // type 21 CRA
        val trail = hex("02 01 D0 09")  // type 1 TRAIL_R
        assertEquals(listOf(32, 33, 34, 19, 21, 1), AnnexB.split(sc4(vps, hsps, hpps, idrW, cra, trail), VideoCodec.HEVC).map { it.type })
        assertTrue(AnnexB.isKeyframe(sc4(cra), VideoCodec.HEVC))
        assertFalse(AnnexB.isKeyframe(sc4(trail), VideoCodec.HEVC))
        val (out, key) = AnnexB.makeSelfContained(sc4(idrW), VideoCodec.HEVC, sc4(vps, hsps, hpps))
        assertTrue(key)
        assertContentEquals(sc4(vps, hsps, hpps, idrW), out)
        assertEquals(VideoCodec.HEVC, AnnexB.sniff(sc4(vps, hsps)))
        assertEquals(VideoCodec.H264, AnnexB.sniff(sc4(sps, pps)))
    }

    @Test
    fun accessUnitSplitAddsMissingParameterSets() {
        // SPS PPS IDR | P | P(2 slices) | IDR without parameter sets | P
        val p2a = hex("41 9A 22")        // first_mb_in_slice = 0
        val p2b = hex("41 4A 22")        // first_mb_in_slice != 0 (first bit 0): same picture
        val stream = sc4(sps, pps, idr, p, p2a, p2b, idr, p)
        val aus = AccessUnits.split(stream, VideoCodec.H264)
        assertEquals(5, aus.size)
        assertEquals(listOf(true, false, false, true, false), aus.map { it.keyframe })
        assertContentEquals(sc4(sps, pps, idr), aus[0].data)
        assertContentEquals(sc4(p2a, p2b), aus[2].data)
        assertContentEquals(sc4(sps, pps, idr), aus[3].data)
    }

    @Test
    fun accessUnitSplitKeepsInlineHeadersAndSei() {
        val sei = hex("06 05 01 AA 80")
        val stream = sc4(sps, pps, sei, idr, p, sps, pps, sei, idr)
        val aus = AccessUnits.split(stream, VideoCodec.H264)
        assertEquals(3, aus.size)
        assertContentEquals(sc4(sps, pps, sei, idr), aus[0].data)
        assertContentEquals(sc4(sps, pps, sei, idr), aus[2].data)
    }
}
