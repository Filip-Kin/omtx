package com.filipkin.omtx.core

/**
 * Fixed command strings, copied character for character from OMTMetadataConstants and
 * OMTMetadataTemplates in libomtnet. Receivers match these with string equality, so they
 * must never be reformatted. The double `==` in the tally strings is the upstream typo that
 * every implementation keeps.
 */
object OmtStrings {
    const val SUBSCRIBE_VIDEO = "<OMTSubscribe Video=\"true\" />"
    const val SUBSCRIBE_AUDIO = "<OMTSubscribe Audio=\"true\" />"
    const val SUBSCRIBE_METADATA = "<OMTSubscribe Metadata=\"true\" />"
    const val PREVIEW_ON = "<OMTSettings Preview=\"true\" />"
    const val PREVIEW_OFF = "<OMTSettings Preview=\"false\" />"

    const val TALLY_PREVIEW = "<OMTTally Preview=\"true\" Program==\"false\" />"
    const val TALLY_PROGRAM = "<OMTTally Preview=\"false\" Program==\"true\" />"
    const val TALLY_PREVIEWPROGRAM = "<OMTTally Preview=\"true\" Program==\"true\" />"
    const val TALLY_NONE = "<OMTTally Preview=\"false\" Program==\"false\" />"

    const val QUALITY_PREFIX = "<OMTSettings Quality="
    const val SENDER_INFO_PREFIX = "<OMTInfo"

    /** omtx addition, PROTOCOL-OMTX.md §3. */
    const val KEYFRAME_REQUEST = "<OMTKeyframeRequest />"
}

data class Tally(val preview: Boolean, val program: Boolean) {
    fun toXml(): String = when {
        !preview && !program -> OmtStrings.TALLY_NONE
        preview && !program -> OmtStrings.TALLY_PREVIEW
        !preview && program -> OmtStrings.TALLY_PROGRAM
        else -> OmtStrings.TALLY_PREVIEWPROGRAM
    }

    companion object {
        val NONE = Tally(false, false)

        /** OMTSend.GetTallyInternal: OR of every connection's tally. */
        fun combine(all: Iterable<Tally>): Tally {
            var pv = false
            var pg = false
            for (t in all) { pv = pv || t.preview; pg = pg || t.program }
            return Tally(pv, pg)
        }
    }
}

/** OMTQuality enum values; the ordering is what "highest requested" compares. */
enum class Quality(val level: Int) {
    Default(0), Low(1), Medium(50), High(100);

    /** PROTOCOL-OMTX.md §4.5 ceiling cap in bits per second, null (no cap) for Default and High. */
    val capBps: Int?
        get() = when (this) {
            Default -> null
            Low -> 8_000_000
            Medium -> 15_000_000
            High -> null
        }

    companion object {
        fun parse(name: String): Quality? = entries.firstOrNull { it.name == name }

        /** Highest quality across receivers, as OMTSend.Send computes suggestedQuality. */
        fun highest(all: Iterable<Quality>): Quality {
            var q = Default
            for (x in all) if (x.level > q.level) q = x
            return q
        }
    }
}

sealed class Command {
    object SubscribeVideo : Command()
    object SubscribeAudio : Command()
    object SubscribeMetadata : Command()
    object PreviewOn : Command()
    object PreviewOff : Command()
    object KeyframeRequest : Command()
    data class SetTally(val tally: Tally) : Command()
    /** `quality` is null when the XML parses but the value is not a known OMTQuality. */
    data class SuggestQuality(val quality: Quality?) : Command()
    data class Other(val xml: String) : Command()

    companion object {
        private val qualityAttr = Regex("""\bQuality\s*=\s*["']([^"']*)["']""")

        /** Same order and matching rules as OMTChannel.ProcessMetadata. */
        fun parse(xml: String): Command = when {
            xml == OmtStrings.SUBSCRIBE_VIDEO -> SubscribeVideo
            xml == OmtStrings.SUBSCRIBE_AUDIO -> SubscribeAudio
            xml == OmtStrings.SUBSCRIBE_METADATA -> SubscribeMetadata
            xml == OmtStrings.TALLY_PREVIEWPROGRAM -> SetTally(Tally(true, true))
            xml == OmtStrings.TALLY_PROGRAM -> SetTally(Tally(false, true))
            xml == OmtStrings.TALLY_PREVIEW -> SetTally(Tally(true, false))
            xml == OmtStrings.TALLY_NONE -> SetTally(Tally(false, false))
            xml == OmtStrings.PREVIEW_ON -> PreviewOn
            xml == OmtStrings.PREVIEW_OFF -> PreviewOff
            xml == OmtStrings.KEYFRAME_REQUEST -> KeyframeRequest
            xml.startsWith(OmtStrings.QUALITY_PREFIX) ->
                SuggestQuality(qualityAttr.find(xml)?.groupValues?.get(1)?.let { Quality.parse(it) })
            else -> Other(xml)
        }
    }
}

object SenderInfo {
    /**
     * Same text as OMTSenderInfo.ToXML (XmlTextWriter, one empty element, attributes in this
     * order, " />" close). Attribute values are escaped the way XmlTextWriter escapes them.
     */
    fun toXml(productName: String, manufacturer: String, version: String): String =
        "<OMTInfo ProductName=\"${esc(productName)}\" Manufacturer=\"${esc(manufacturer)}\" Version=\"${esc(version)}\" />"

    private fun esc(s: String): String {
        val sb = StringBuilder(s.length)
        for (ch in s) when (ch) {
            '&' -> sb.append("&amp;")
            '<' -> sb.append("&lt;")
            '>' -> sb.append("&gt;")
            '"' -> sb.append("&quot;")
            else -> sb.append(ch)
        }
        return sb.toString()
    }
}
