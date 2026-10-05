package com.filipkin.omtx.core

import java.io.ByteArrayOutputStream

enum class VideoCodec(val fourCC: Int) {
    H264(Wire.CODEC_H264),
    HEVC(Wire.CODEC_HEVC);

    /** NAL unit type from the first header byte. */
    fun nalType(firstByte: Byte): Int = when (this) {
        H264 -> firstByte.toInt() and 0x1f
        HEVC -> (firstByte.toInt() shr 1) and 0x3f
    }

    fun isVcl(type: Int): Boolean = when (this) {
        H264 -> type in 1..5
        HEVC -> type in 0..31
    }

    /** H.264 IDR (5) or HEVC IRAP (16..21), spec §2. */
    fun isKeyframeNal(type: Int): Boolean = when (this) {
        H264 -> type == 5
        HEVC -> type in 16..21
    }

    fun isAud(type: Int): Boolean = when (this) {
        H264 -> type == 9
        HEVC -> type == 35
    }

    /** VPS/SPS/PPS. */
    fun isParameterSet(type: Int): Boolean = when (this) {
        H264 -> type == 7 || type == 8
        HEVC -> type in 32..34
    }

    /** NAL types whose presence makes a keyframe self-contained. */
    val requiredParameterSets: Set<Int>
        get() = when (this) {
            H264 -> setOf(7, 8)
            HEVC -> setOf(32, 33, 34)
        }

    val headerLength: Int get() = if (this == H264) 1 else 2
}

/** A NAL unit inside some buffer: [offset, offset+length) excludes the start code. */
data class Nal(val offset: Int, val length: Int, val type: Int)

object AnnexB {
    val START_CODE = byteArrayOf(0, 0, 0, 1)

    /**
     * Split an Annex B buffer into NAL units. Accepts 3- and 4-byte start codes. Trailing zero
     * bytes before the next start code (trailing_zero_8bits) are not part of the NAL.
     */
    fun split(data: ByteArray, codec: VideoCodec, offset: Int = 0, length: Int = data.size - offset): List<Nal> {
        val end = offset + length
        val out = ArrayList<Nal>(8)
        var nalStart = -1
        var i = offset
        while (i + 2 < end) {
            if (data[i] == 0.toByte() && data[i + 1] == 0.toByte() && data[i + 2] == 1.toByte()) {
                if (nalStart >= 0) addNal(data, codec, nalStart, i, out)
                i += 3
                nalStart = i
            } else {
                i++
            }
        }
        if (nalStart >= 0) addNal(data, codec, nalStart, end, out)
        return out
    }

    private fun addNal(data: ByteArray, codec: VideoCodec, start: Int, endExclusive: Int, out: MutableList<Nal>) {
        var e = endExclusive
        while (e > start && data[e - 1] == 0.toByte()) e--
        if (e > start) out.add(Nal(start, e - start, codec.nalType(data[start])))
    }

    /** True when every NAL is preceded by exactly 00 00 00 01 and nothing precedes the first. */
    fun isCanonical(data: ByteArray, nals: List<Nal>, offset: Int = 0, length: Int = data.size - offset): Boolean {
        var expect = offset
        for (n in nals) {
            if (n.offset != expect + 4) return false
            if (data[expect] != 0.toByte() || data[expect + 1] != 0.toByte() ||
                data[expect + 2] != 0.toByte() || data[expect + 3] != 1.toByte()) return false
            expect = n.offset + n.length
        }
        return expect == offset + length
    }

    /** Rebuild with 4-byte start codes. Returns the input unchanged if already canonical. */
    fun toFourByteStartCodes(data: ByteArray, codec: VideoCodec): ByteArray {
        val nals = split(data, codec)
        if (isCanonical(data, nals)) return data
        return join(data, nals)
    }

    fun join(data: ByteArray, nals: List<Nal>): ByteArray {
        val out = ByteArrayOutputStream(data.size + nals.size)
        for (n in nals) {
            out.write(START_CODE)
            out.write(data, n.offset, n.length)
        }
        return out.toByteArray()
    }

    fun isKeyframe(data: ByteArray, codec: VideoCodec): Boolean =
        split(data, codec).any { codec.isKeyframeNal(it.type) }

    /** The parameter-set NALs in `data`, joined with 4-byte start codes (empty if none). */
    fun extractParameterSets(data: ByteArray, codec: VideoCodec): ByteArray {
        val nals = split(data, codec).filter { codec.isParameterSet(it.type) }
        return join(data, nals)
    }

    /**
     * Spec §1: every keyframe carries its parameter sets in band directly before the IDR slice.
     * If `au` is a keyframe and lacks any required parameter set, the NALs of `parameterSets`
     * are inserted after any leading AUD (AUD must stay first). Output always uses 4-byte start
     * codes. Non-keyframes, and keyframes that already carry their parameter sets, are only
     * normalised.
     */
    fun makeSelfContained(au: ByteArray, codec: VideoCodec, parameterSets: ByteArray?): Pair<ByteArray, Boolean> {
        val nals = split(au, codec)
        val key = nals.any { codec.isKeyframeNal(it.type) }
        val canonical = isCanonical(au, nals)
        if (!key || parameterSets == null || parameterSets.isEmpty()) {
            return Pair(if (canonical) au else join(au, nals), key)
        }
        val present = nals.mapTo(HashSet()) { it.type }
        if (present.containsAll(codec.requiredParameterSets)) {
            return Pair(if (canonical) au else join(au, nals), true)
        }
        val ps = split(parameterSets, codec).filter { codec.isParameterSet(it.type) }
        val out = ByteArrayOutputStream(au.size + parameterSets.size + 16)
        var inserted = false
        for (n in nals) {
            if (!inserted && !codec.isAud(n.type)) {
                for (p in ps) { out.write(START_CODE); out.write(parameterSets, p.offset, p.length) }
                inserted = true
            }
            out.write(START_CODE)
            out.write(au, n.offset, n.length)
        }
        return Pair(out.toByteArray(), true)
    }

    /**
     * True if this VCL NAL starts a new picture: first_mb_in_slice == 0 (H.264) or
     * first_slice_segment_in_pic_flag == 1 (HEVC). Both are the first bit after the NAL header
     * (ue(v) of 0 is the single bit 1).
     */
    fun startsPicture(data: ByteArray, nal: Nal, codec: VideoCodec): Boolean {
        val idx = nal.offset + codec.headerLength
        if (idx >= nal.offset + nal.length) return true
        return (data[idx].toInt() and 0x80) != 0
    }

    /** Guess the codec of an elementary stream from its first NAL header. */
    fun sniff(data: ByteArray): VideoCodec? {
        val nals = split(data, VideoCodec.H264, 0, minOf(data.size, 4096))
        val first = nals.firstOrNull() ?: return null
        val b0 = data[first.offset].toInt() and 0xff
        val b1 = if (first.length > 1) data[first.offset + 1].toInt() and 0xff else -1
        val hevcType = (b0 shr 1) and 0x3f
        if ((b0 and 0x81) == 0 && b1 == 1 && hevcType in setOf(32, 33, 34, 35, 39, 40) ) return VideoCodec.HEVC
        val avcType = b0 and 0x1f
        if ((b0 and 0x80) == 0 && avcType in setOf(1, 5, 6, 7, 8, 9)) return VideoCodec.H264
        return null
    }
}
