package com.filipkin.omtx.core

/**
 * PROTOCOL-OMTX.md §4.3. One controller per encoder; every connection reports into it.
 *
 * - On a drop: B = max(floor, 0.8 B), then no further step down for 1 s.
 * - After 2 s with no drop and at most 1 frame in flight on any connection:
 *   B = min(ceiling, 1.05 B). The 2 s window restarts after each step up, after each drop and
 *   whenever more than one frame is in flight.
 *
 * Ceiling = min(user ceiling, quality cap from §4.5). Time is passed in (milliseconds, any
 * monotonic base) so the logic is testable. Not thread safe; callers synchronise.
 */
class BitrateController(
    userCeilingBps: Int,
    val floorBps: Int = DEFAULT_FLOOR_BPS,
    initialBps: Int? = null,
) {
    var userCeilingBps: Int = userCeilingBps
        private set
    var qualityCapBps: Int? = null
        private set
    var targetBps: Int = clamp(initialBps ?: userCeilingBps)
        private set

    private var lastStepDownMs: Long? = null
    private var calmSinceMs: Long? = null

    val ceilingBps: Int
        get() = maxOf(floorBps, qualityCapBps?.let { minOf(it, userCeilingBps) } ?: userCeilingBps)

    private fun clamp(v: Int): Int = v.coerceIn(floorBps, ceilingBps)

    /** Returns true if the target changed. */
    fun setUserCeiling(bps: Int): Boolean {
        userCeilingBps = bps
        return reclamp()
    }

    /** Returns true if the target changed. */
    fun setQualityCap(capBps: Int?): Boolean {
        qualityCapBps = capBps
        return reclamp()
    }

    private fun reclamp(): Boolean {
        val n = targetBps.coerceAtMost(ceilingBps).coerceAtLeast(floorBps)
        val changed = n != targetBps
        targetBps = n
        return changed
    }

    /** A frame was dropped on some connection. Returns true if the target changed. */
    fun onDrop(nowMs: Long): Boolean {
        calmSinceMs = nowMs
        val last = lastStepDownMs
        if (last != null && nowMs - last < STEP_DOWN_HOLD_MS) return false
        lastStepDownMs = nowMs
        val n = maxOf(floorBps, (targetBps * 0.8).toInt())
        val changed = n != targetBps
        targetBps = n
        return changed
    }

    /**
     * Call once per encoded frame with the largest in-flight count across connections.
     * Returns true if the target changed.
     */
    fun tick(nowMs: Long, maxInFlight: Int): Boolean {
        val since = calmSinceMs
        if (since == null || maxInFlight > 1) {
            calmSinceMs = nowMs
            return false
        }
        if (nowMs - since < STEP_UP_CALM_MS) return false
        calmSinceMs = nowMs
        val ceiling = ceilingBps
        if (targetBps >= ceiling) return false
        val n = minOf(ceiling, maxOf(targetBps + 1, (targetBps * 1.05).toInt()))
        targetBps = n
        return true
    }

    companion object {
        const val DEFAULT_FLOOR_BPS = 3_000_000
        // No cap: the camera encoder clamps this to the most it can do. Only congestion steps it down (§4.3)
        const val DEFAULT_CEILING_BPS = 1_000_000_000
        const val STEP_DOWN_HOLD_MS = 1_000L
        const val STEP_UP_CALM_MS = 2_000L
    }
}

/**
 * Per-connection video admission, PROTOCOL-OMTX.md §4.1 and §4.2.
 *
 * - Nothing until the connection subscribes to video.
 * - After subscribing, nothing until a keyframe.
 * - At most [maxInFlight] video frames in flight. A frame that does not fit is dropped, the
 *   connection waits for the next keyframe, and a keyframe is requested.
 *
 * Keyframe requests: one at once on subscribe and on entering the wait after an overflow;
 * while still waiting, at most one more per [retryMs] so a congested link does not turn into
 * an IDR storm.
 */
class VideoGate(val maxInFlight: Int = MAX_IN_FLIGHT, val retryMs: Long = KEYFRAME_RETRY_MS) {
    enum class Outcome { SEND, NOT_SUBSCRIBED, WAITING_FOR_KEYFRAME, OVERFLOW }

    data class Decision(val outcome: Outcome, val requestKeyframe: Boolean) {
        val send: Boolean get() = outcome == Outcome.SEND
    }

    var subscribed = false
        private set
    var waitingForKeyframe = true
        private set
    private var lastRequestMs: Long? = null

    /** Returns true if a keyframe should be requested now (first subscription only). */
    @Synchronized
    fun subscribe(nowMs: Long): Boolean {
        if (subscribed) return false
        subscribed = true
        waitingForKeyframe = true
        lastRequestMs = nowMs
        return true
    }

    @Synchronized
    fun offer(isKeyframe: Boolean, inFlight: Int, nowMs: Long): Decision {
        if (!subscribed) return Decision(Outcome.NOT_SUBSCRIBED, false)
        if (inFlight >= maxInFlight) {
            val entering = !waitingForKeyframe
            waitingForKeyframe = true
            return Decision(Outcome.OVERFLOW, if (entering) requestNow(nowMs) else retry(nowMs))
        }
        if (waitingForKeyframe) {
            if (!isKeyframe) return Decision(Outcome.WAITING_FOR_KEYFRAME, retry(nowMs))
            waitingForKeyframe = false
        }
        return Decision(Outcome.SEND, false)
    }

    private fun requestNow(nowMs: Long): Boolean { lastRequestMs = nowMs; return true }

    private fun retry(nowMs: Long): Boolean {
        val last = lastRequestMs
        if (last != null && nowMs - last < retryMs) return false
        lastRequestMs = nowMs
        return true
    }

    companion object {
        const val MAX_IN_FLIGHT = 4
        const val KEYFRAME_RETRY_MS = 1_000L
    }
}
