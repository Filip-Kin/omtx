package com.filipkin.omtx.core

import com.filipkin.omtx.core.VideoGate.Outcome as O
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class VideoGateTest {
    @Test
    fun nothingBeforeSubscribe() {
        val g = VideoGate()
        assertEquals(O.NOT_SUBSCRIBED, g.offer(true, 0, 0).outcome)
    }

    @Test
    fun newSubscriberWaitsForKeyframeAndRequestsOne() {
        val g = VideoGate()
        assertTrue(g.subscribe(0))            // request at once
        assertFalse(g.subscribe(1))           // second subscribe is a no-op
        assertEquals(O.WAITING_FOR_KEYFRAME, g.offer(false, 0, 10).outcome)
        assertEquals(O.WAITING_FOR_KEYFRAME, g.offer(false, 0, 20).outcome)
        assertEquals(O.SEND, g.offer(true, 0, 30).outcome)
        assertEquals(O.SEND, g.offer(false, 1, 40).outcome)
    }

    @Test
    fun overflowDropsUntilNextKeyframe() {
        val g = VideoGate(maxInFlight = 4)
        g.subscribe(0)
        assertEquals(O.SEND, g.offer(true, 0, 0).outcome)
        assertEquals(O.SEND, g.offer(false, 3, 33).outcome)
        val d = g.offer(false, 4, 66)
        assertEquals(O.OVERFLOW, d.outcome)
        assertTrue(d.requestKeyframe)
        // Queue drained, but still no non-keyframes until a keyframe.
        assertEquals(O.WAITING_FOR_KEYFRAME, g.offer(false, 0, 100).outcome)
        assertEquals(O.WAITING_FOR_KEYFRAME, g.offer(false, 0, 133).outcome)
        assertEquals(O.SEND, g.offer(true, 0, 166).outcome)
        assertEquals(O.SEND, g.offer(false, 1, 200).outcome)
    }

    @Test
    fun frameWaitingTooLongOverflows() {
        val g = VideoGate()
        g.subscribe(0)
        assertEquals(O.SEND, g.offer(true, 0, 0).outcome)
        assertEquals(O.SEND, g.offer(false, 10, 10, oldestAgeMs = 200).outcome)   // a hiccup: ride it out
        assertEquals(O.OVERFLOW, g.offer(false, 3, 20, oldestAgeMs = 300).outcome) // latency bound
    }

    @Test
    fun keyframeThatDoesNotFitIsDroppedToo() {
        val g = VideoGate(maxInFlight = 4)
        g.subscribe(0)
        g.offer(true, 0, 0)
        assertEquals(O.OVERFLOW, g.offer(true, 4, 10).outcome)
        assertTrue(g.waitingForKeyframe)
        assertEquals(O.SEND, g.offer(true, 0, 20).outcome)
    }

    @Test
    fun keyframeRequestsAreRateLimitedWhileWaiting() {
        val g = VideoGate(maxInFlight = 4, retryMs = 1000)
        assertTrue(g.subscribe(0))
        assertFalse(g.offer(false, 0, 500).requestKeyframe)
        assertTrue(g.offer(false, 0, 1000).requestKeyframe)
        assertFalse(g.offer(false, 0, 1500).requestKeyframe)
        assertEquals(O.SEND, g.offer(true, 0, 1600).outcome)
        // Entering the wait from an overflow requests at once even inside the retry window.
        assertTrue(g.offer(false, 4, 1700).requestKeyframe)
        assertFalse(g.offer(false, 4, 1800).requestKeyframe)
        assertFalse(g.offer(false, 0, 1900).requestKeyframe)
        assertTrue(g.offer(false, 0, 2700).requestKeyframe)
    }
}

class BitrateControllerTest {
    @Test
    fun startsAtCeiling() {
        val c = BitrateController(10_000_000)
        assertEquals(10_000_000, c.targetBps)
        assertEquals(3_000_000, c.floorBps)
    }

    @Test
    fun dropStepsDownWithOneSecondHold() {
        val c = BitrateController(10_000_000)
        assertTrue(c.onDrop(0))
        assertEquals(8_000_000, c.targetBps)
        assertFalse(c.onDrop(500))           // inside the 1 s hold
        assertEquals(8_000_000, c.targetBps)
        assertTrue(c.onDrop(1000))
        assertEquals(6_400_000, c.targetBps)
        c.onDrop(2000); c.onDrop(3000); c.onDrop(4000); c.onDrop(5000)
        assertEquals(3_000_000, c.targetBps) // floor
    }

    @Test
    fun stepsUpAfterOneCalmSecond() {
        val c = BitrateController(10_000_000, initialBps = 5_000_000)
        assertFalse(c.tick(0, 0))
        assertFalse(c.tick(999, 4))
        assertTrue(c.tick(1000, 4))
        assertEquals(6_250_000, c.targetBps)
        assertFalse(c.tick(1500, 0))         // window restarted at the step
        assertTrue(c.tick(2000, 0))
        assertEquals(7_812_500, c.targetBps)
    }

    @Test
    fun moreThanOneInFlightOrADropRestartsTheWindow() {
        val c = BitrateController(10_000_000, initialBps = 5_000_000)
        c.tick(0, 0)
        c.tick(700, 5)                       // backlog
        assertFalse(c.tick(1500, 0))
        assertTrue(c.tick(1700, 0))
        val after = c.targetBps
        c.onDrop(2000)
        assertFalse(c.tick(2999, 0))
        assertTrue(c.tick(3000, 0))
        assertEquals(6_250_000, after)
        assertEquals(6_250_000, c.targetBps)
    }

    @Test
    fun neverAboveCeilingAndQualityCapClamps() {
        val c = BitrateController(10_000_000, initialBps = 9_800_000)
        c.tick(0, 0)
        assertTrue(c.tick(1000, 0))
        assertEquals(10_000_000, c.targetBps)
        assertFalse(c.tick(2000, 0))
        assertTrue(c.setQualityCap(Quality.Low.capBps))
        assertEquals(8_000_000, c.targetBps)
        assertEquals(8_000_000, c.ceilingBps)
        c.setQualityCap(Quality.Medium.capBps)  // 15 Mbps cap above user ceiling: user wins
        assertEquals(10_000_000, c.ceilingBps)
        c.setQualityCap(null)
        assertEquals(10_000_000, c.ceilingBps)
        assertEquals(8_000_000, c.targetBps)   // cap lifted: target climbs back by steps, not at once
        assertTrue(c.setUserCeiling(5_000_000))  // ceiling below the target pulls it down
        assertEquals(5_000_000, c.ceilingBps)
        assertEquals(5_000_000, c.targetBps)
        val d = BitrateController(10_000_000)
        assertTrue(d.setUserCeiling(5_000_000))
        assertEquals(5_000_000, d.targetBps)
    }
}
