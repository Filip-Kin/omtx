package com.filipkin.omtx.camera

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.AudioTimestamp
import android.media.MediaRecorder
import android.os.Process
import android.os.SystemClock
import android.util.Log
import com.filipkin.omtx.core.Wire

/**
 * 48 kHz float capture (stereo, mono fallback), delivered as planar blocks of
 * [SAMPLES_PER_CHANNEL] samples per channel with the capture time of the first sample in
 * CLOCK_BOOTTIME nanoseconds (the same base as [Clock]).
 */
class AudioCapture(
    private val wanted: () -> Boolean,
    private val sink: (sampleRate: Int, channels: Int, samplesPerChannel: Int, bootNs: Long, planar: FloatArray) -> Unit,
) {
    @Volatile private var running = false
    private var thread: Thread? = null
    var channels = 0
        private set

    @SuppressLint("MissingPermission")
    fun start(): Boolean {
        val rec = open(AudioFormat.CHANNEL_IN_STEREO, 2) ?: open(AudioFormat.CHANNEL_IN_MONO, 1) ?: return false
        running = true
        thread = Thread({ loop(rec) }, "audio").apply { start() }
        return true
    }

    fun stop() {
        running = false
        thread?.join(1000)
        thread = null
    }

    @SuppressLint("MissingPermission")
    private fun open(mask: Int, ch: Int): AudioRecord? {
        val fmt = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_FLOAT)
            .setSampleRate(SAMPLE_RATE)
            .setChannelMask(mask)
            .build()
        val min = AudioRecord.getMinBufferSize(SAMPLE_RATE, mask, AudioFormat.ENCODING_PCM_FLOAT)
        if (min <= 0) return null
        val size = maxOf(min, SAMPLES_PER_CHANNEL * ch * 4 * 4)
        for (source in listOf(MediaRecorder.AudioSource.CAMCORDER, MediaRecorder.AudioSource.MIC)) {
            val r = try {
                AudioRecord.Builder().setAudioSource(source).setAudioFormat(fmt).setBufferSizeInBytes(size).build()
            } catch (e: Exception) {
                Log.w(TAG, "AudioRecord source=$source ch=$ch: $e"); null
            } ?: continue
            if (r.state == AudioRecord.STATE_INITIALIZED) { channels = ch; return r }
            r.release()
        }
        return null
    }

    private fun loop(rec: AudioRecord) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        val ch = channels
        val inter = FloatArray(SAMPLES_PER_CHANNEL * ch)
        val planar = FloatArray(SAMPLES_PER_CHANNEL * ch)
        val ts = AudioTimestamp()
        var framesRead = 0L
        try {
            rec.startRecording()
            while (running) {
                var got = 0
                while (got < inter.size && running) {
                    val n = rec.read(inter, got, inter.size - got, AudioRecord.READ_BLOCKING)
                    if (n < 0) { Log.e(TAG, "read $n"); running = false; break }
                    got += n
                }
                if (got < inter.size) break
                val blockStart = framesRead
                framesRead += SAMPLES_PER_CHANNEL
                if (!wanted()) continue
                val bootNs = if (rec.getTimestamp(ts, AudioTimestamp.TIMEBASE_BOOTTIME) == AudioRecord.SUCCESS) {
                    ts.nanoTime + (blockStart - ts.framePosition) * 1_000_000_000L / SAMPLE_RATE
                } else {
                    // No HAL timestamp: the block just finished, so it started one block ago.
                    SystemClock.elapsedRealtimeNanos() - SAMPLES_PER_CHANNEL * 1_000_000_000L / SAMPLE_RATE
                }
                if (ch == 1) inter.copyInto(planar) else Wire.deinterleave(inter, ch, SAMPLES_PER_CHANNEL, planar)
                sink(SAMPLE_RATE, ch, SAMPLES_PER_CHANNEL, bootNs, planar)
            }
        } catch (e: Exception) {
            Log.e(TAG, "audio loop", e)
        } finally {
            try { rec.stop() } catch (_: Exception) {}
            rec.release()
        }
    }

    companion object {
        private const val TAG = "omtx.audio"
        const val SAMPLE_RATE = 48_000
        const val SAMPLES_PER_CHANNEL = 1024
    }
}
