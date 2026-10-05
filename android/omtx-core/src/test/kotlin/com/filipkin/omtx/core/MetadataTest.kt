package com.filipkin.omtx.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs

class MetadataTest {
    @Test
    fun exactStrings() {
        assertEquals("<OMTSubscribe Video=\"true\" />", OmtStrings.SUBSCRIBE_VIDEO)
        assertEquals("<OMTTally Preview=\"false\" Program==\"true\" />", OmtStrings.TALLY_PROGRAM)
        assertEquals("<OMTTally Preview=\"true\" Program==\"false\" />", OmtStrings.TALLY_PREVIEW)
        assertEquals("<OMTTally Preview=\"true\" Program==\"true\" />", OmtStrings.TALLY_PREVIEWPROGRAM)
        assertEquals("<OMTTally Preview=\"false\" Program==\"false\" />", OmtStrings.TALLY_NONE)
        assertEquals("<OMTKeyframeRequest />", OmtStrings.KEYFRAME_REQUEST)
        assertEquals(
            "<OMTInfo ProductName=\"omtx camera\" Manufacturer=\"omtx\" Version=\"1.0\" />",
            SenderInfo.toXml("omtx camera", "omtx", "1.0"),
        )
    }

    @Test
    fun tallyXmlMatchesFromTally() {
        assertEquals(OmtStrings.TALLY_NONE, Tally(false, false).toXml())
        assertEquals(OmtStrings.TALLY_PREVIEW, Tally(true, false).toXml())
        assertEquals(OmtStrings.TALLY_PROGRAM, Tally(false, true).toXml())
        assertEquals(OmtStrings.TALLY_PREVIEWPROGRAM, Tally(true, true).toXml())
    }

    @Test
    fun tallyCombining() {
        assertEquals(Tally.NONE, Tally.combine(emptyList()))
        assertEquals(Tally(true, false), Tally.combine(listOf(Tally(true, false), Tally.NONE)))
        assertEquals(Tally(true, true), Tally.combine(listOf(Tally(true, false), Tally(false, true))))
        assertEquals(Tally(false, true), Tally.combine(listOf(Tally(false, true), Tally(false, true))))
    }

    @Test
    fun commandParsing() {
        assertEquals(Command.SubscribeVideo, Command.parse(OmtStrings.SUBSCRIBE_VIDEO))
        assertEquals(Command.SubscribeAudio, Command.parse(OmtStrings.SUBSCRIBE_AUDIO))
        assertEquals(Command.SubscribeMetadata, Command.parse(OmtStrings.SUBSCRIBE_METADATA))
        assertEquals(Command.SetTally(Tally(false, true)), Command.parse(OmtStrings.TALLY_PROGRAM))
        assertEquals(Command.SetTally(Tally(true, true)), Command.parse(OmtStrings.TALLY_PREVIEWPROGRAM))
        assertEquals(Command.KeyframeRequest, Command.parse("<OMTKeyframeRequest />"))
        assertEquals(Command.PreviewOn, Command.parse(OmtStrings.PREVIEW_ON))
        assertEquals(Command.SuggestQuality(Quality.High), Command.parse("<OMTSettings Quality=\"High\" />"))
        assertEquals(Command.SuggestQuality(Quality.Low), Command.parse("<OMTSettings Quality=\"Low\" />"))
        assertEquals(Command.SuggestQuality(null), Command.parse("<OMTSettings Quality=\"Ultra\" />"))
        // Exact matching only: a reformatted tally is not a tally.
        assertIs<Command.Other>(Command.parse("<OMTTally Preview=\"false\" Program=\"true\" />"))
        assertIs<Command.Other>(Command.parse("<OMTSubscribe Video=\"true\"/>"))
    }

    @Test
    fun qualityCaps() {
        assertEquals(Quality.Default, Quality.highest(listOf(Quality.Default, Quality.Default)))
        assertEquals(Quality.Low, Quality.highest(listOf(Quality.Default, Quality.Low)))
        assertEquals(Quality.High, Quality.highest(listOf(Quality.Medium, Quality.High, Quality.Low)))
        assertEquals(4_000_000, Quality.Low.capBps)
        assertEquals(8_000_000, Quality.Medium.capBps)
        assertEquals(15_000_000, Quality.High.capBps)
        assertEquals(null, Quality.Default.capBps)
    }
}
