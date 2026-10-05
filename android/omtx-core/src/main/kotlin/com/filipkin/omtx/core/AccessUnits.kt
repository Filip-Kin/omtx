package com.filipkin.omtx.core

/** One picture from an elementary stream, self-contained if it is a keyframe. */
class AccessUnit(val data: ByteArray, val keyframe: Boolean)

/**
 * Splits a whole Annex B elementary stream into access units (one picture each) and makes
 * every keyframe self-contained: if an IDR/IRAP access unit lacks parameter sets, the most
 * recent ones seen in the stream are inserted. Access units that already carry them inline are
 * kept as they are (apart from start code normalisation).
 */
object AccessUnits {
    fun split(stream: ByteArray, codec: VideoCodec): List<AccessUnit> {
        val nals = AnnexB.split(stream, codec)
        val groups = ArrayList<List<Nal>>()
        var cur = ArrayList<Nal>()
        var curHasVcl = false
        for (n in nals) {
            val vcl = codec.isVcl(n.type)
            val boundary = if (vcl) {
                curHasVcl && AnnexB.startsPicture(stream, n, codec)
            } else {
                curHasVcl && startsNewAuWhenAfterVcl(codec, n.type)
            }
            if (boundary && cur.isNotEmpty()) {
                groups.add(cur); cur = ArrayList(); curHasVcl = false
            }
            cur.add(n)
            if (vcl) curHasVcl = true
        }
        if (cur.isNotEmpty()) groups.add(cur)

        val latestPs = HashMap<Int, Nal>()
        val out = ArrayList<AccessUnit>(groups.size)
        for (g in groups) {
            if (g.none { codec.isVcl(it.type) }) {
                // Trailing parameter sets/SEI with no picture: remember the sets, emit nothing.
                for (n in g) if (codec.isParameterSet(n.type)) latestPs[n.type] = n
                continue
            }
            for (n in g) if (codec.isParameterSet(n.type)) latestPs[n.type] = n
            val au = AnnexB.join(stream, g)
            val key = g.any { codec.isKeyframeNal(it.type) }
            if (!key) { out.add(AccessUnit(au, false)); continue }
            val ps = AnnexB.join(stream, codec.requiredParameterSets.sorted().mapNotNull { latestPs[it] })
            val (fixed, _) = AnnexB.makeSelfContained(au, codec, ps)
            out.add(AccessUnit(fixed, true))
        }
        return out
    }

    /** NAL types that open a new access unit when they follow a picture's slices. */
    private fun startsNewAuWhenAfterVcl(codec: VideoCodec, type: Int): Boolean = when (codec) {
        // AUD, SPS, PPS, SEI, prefix NAL / subset SPS / reserved 14..18
        VideoCodec.H264 -> type == 9 || type == 7 || type == 8 || type == 6 || type in 14..18
        // AUD, VPS, SPS, PPS, prefix SEI, reserved 41..44, unspecified 48..55
        VideoCodec.HEVC -> type == 35 || type in 32..34 || type == 39 || type in 41..44 || type in 48..55
    }
}
