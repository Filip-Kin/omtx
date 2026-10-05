package com.filipkin.omtx.core

import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * OMT 1.0 wire format plus the omtx additions (docs/PROTOCOL-OMTX.md).
 *
 * Every frame is a 16-byte little-endian header, an optional extended header (32 bytes for
 * video, 24 for audio) and the payload. Encoders here produce one ByteArray holding the whole
 * frame so the same immutable array can be queued on every connection without a copy.
 */
object Wire {
    const val VERSION: Int = 1
    const val HEADER_LENGTH = 16
    const val VIDEO_EXT_LENGTH = 32
    const val AUDIO_EXT_LENGTH = 24

    const val FRAME_METADATA = 1
    const val FRAME_VIDEO = 2
    const val FRAME_AUDIO = 4

    /** FourCC values as little-endian INT32, same as OMTCodec in libomtnet. */
    const val CODEC_H264 = 0x34363248
    const val CODEC_HEVC = 0x43564548
    const val CODEC_FPA1 = 0x31415046
    const val CODEC_VMX1 = 0x31584D56

    const val FLAG_INTERLACED = 1
    const val FLAG_ALPHA = 2
    const val FLAG_PREMULTIPLIED = 4
    const val FLAG_PREVIEW = 8
    const val FLAG_HIGH_BIT_DEPTH = 16
    const val FLAG_KEYFRAME = 32

    const val COLORSPACE_601 = 601
    const val COLORSPACE_709 = 709

    /** libomtnet OMTConstants.VIDEO_MAX_SIZE: receivers reject anything larger. */
    const val MAX_FRAME_LENGTH = 10 * 1024 * 1024

    /**
     * Whether outgoing metadata carries a trailing NUL byte.
     *
     * PROTOCOL.md says DataLength includes a NUL. The C# reference (OMTBuffer.FromMetadata)
     * does NOT write one, and its receiver compares the decoded payload with `==` against the
     * command strings, so a trailing NUL makes libomtnet ignore tally and fail to parse OMTInfo.
     * We match the reference on the wire and accept either form on input.
     */
    const val METADATA_NUL_TERMINATED = false

    fun fourCC(codec: Int): String =
        String(byteArrayOf(codec.toByte(), (codec shr 8).toByte(), (codec shr 16).toByte(), (codec shr 24).toByte()), Charsets.US_ASCII)

    private fun header(buf: ByteBuffer, frameType: Int, timestamp: Long, metadataLength: Int, dataLength: Int) {
        buf.put(VERSION.toByte())
        buf.put(frameType.toByte())
        buf.putLong(timestamp)
        buf.putShort(metadataLength.toShort())
        buf.putInt(dataLength)
    }

    /**
     * One encoded video access unit as a complete OMT video frame.
     * No per-frame metadata (spec §1), so DataLength = 32 + payload.
     */
    fun videoFrame(
        codec: Int,
        width: Int,
        height: Int,
        frameRateN: Int,
        frameRateD: Int,
        flags: Int,
        colorSpace: Int,
        timestamp: Long,
        payload: ByteArray,
        payloadOffset: Int = 0,
        payloadLength: Int = payload.size - payloadOffset,
    ): ByteArray {
        val out = ByteArray(HEADER_LENGTH + VIDEO_EXT_LENGTH + payloadLength)
        val b = ByteBuffer.wrap(out).order(ByteOrder.LITTLE_ENDIAN)
        header(b, FRAME_VIDEO, timestamp, 0, VIDEO_EXT_LENGTH + payloadLength)
        b.putInt(codec)
        b.putInt(width)
        b.putInt(height)
        b.putInt(frameRateN)
        b.putInt(frameRateD)
        b.putFloat(width.toFloat() / height.toFloat())
        b.putInt(flags)
        b.putInt(colorSpace)
        System.arraycopy(payload, payloadOffset, out, HEADER_LENGTH + VIDEO_EXT_LENGTH, payloadLength)
        return out
    }

    /**
     * FPA1 audio frame from planar float samples (channel 0 samples, then channel 1, ...).
     * Matches OMTFPA1Codec.Encode: a channel whose samples are all exactly zero is left out of
     * the payload and its ActiveChannels bit stays clear.
     */
    fun audioFrame(
        sampleRate: Int,
        channels: Int,
        samplesPerChannel: Int,
        timestamp: Long,
        planar: FloatArray,
    ): ByteArray {
        require(channels in 1..32) { "channels" }
        require(planar.size >= channels * samplesPerChannel) { "planar buffer too small" }
        var active = 0L
        var activeCount = 0
        for (c in 0 until channels) {
            val base = c * samplesPerChannel
            var silent = true
            for (i in 0 until samplesPerChannel) {
                // Bit pattern test, like the byte-wise IsEmpty in C#: -0.0f counts as data.
                if (java.lang.Float.floatToRawIntBits(planar[base + i]) != 0) { silent = false; break }
            }
            if (!silent) { active = active or (1L shl c); activeCount++ }
        }
        val payloadLength = activeCount * samplesPerChannel * 4
        val out = ByteArray(HEADER_LENGTH + AUDIO_EXT_LENGTH + payloadLength)
        val b = ByteBuffer.wrap(out).order(ByteOrder.LITTLE_ENDIAN)
        header(b, FRAME_AUDIO, timestamp, 0, AUDIO_EXT_LENGTH + payloadLength)
        b.putInt(CODEC_FPA1)
        b.putInt(sampleRate)
        b.putInt(samplesPerChannel)
        b.putInt(channels)
        b.putInt(active.toInt())
        b.putInt(0)
        for (c in 0 until channels) {
            if (active and (1L shl c) == 0L) continue
            val base = c * samplesPerChannel
            for (i in 0 until samplesPerChannel) b.putFloat(planar[base + i])
        }
        return out
    }

    /** Metadata frame: 16-byte header, MetadataLength = 0, UTF-8 XML (plus NUL if enabled). */
    fun metadataFrame(xml: String, timestamp: Long = 0, nulTerminated: Boolean = METADATA_NUL_TERMINATED): ByteArray {
        val utf8 = xml.toByteArray(Charsets.UTF_8)
        val dataLength = utf8.size + if (nulTerminated) 1 else 0
        val out = ByteArray(HEADER_LENGTH + dataLength)
        val b = ByteBuffer.wrap(out).order(ByteOrder.LITTLE_ENDIAN)
        header(b, FRAME_METADATA, timestamp, 0, dataLength)
        System.arraycopy(utf8, 0, out, HEADER_LENGTH, utf8.size)
        return out
    }

    /** Decode a metadata payload, dropping any trailing NUL bytes. */
    fun decodeXml(data: ByteArray, offset: Int = 0, length: Int = data.size - offset): String {
        var end = offset + length
        while (end > offset && data[end - 1] == 0.toByte()) end--
        return String(data, offset, end - offset, Charsets.UTF_8)
    }

    /** Interleaved -> planar. `out` must hold frames * channels samples. */
    fun deinterleave(interleaved: FloatArray, channels: Int, frames: Int, out: FloatArray) {
        for (c in 0 until channels) {
            val base = c * frames
            var src = c
            for (i in 0 until frames) {
                out[base + i] = interleaved[src]
                src += channels
            }
        }
    }
}

/** A parsed incoming frame. `data` is everything after the 16-byte header. */
class Frame(
    val version: Int,
    val frameType: Int,
    val timestamp: Long,
    val metadataLength: Int,
    val data: ByteArray,
) {
    fun le(): ByteBuffer = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)
    val xml: String get() = Wire.decodeXml(data)
}

class ProtocolException(msg: String) : IOException(msg)

/** Blocking frame reader for one TCP stream. */
class FrameReader(private val input: InputStream, private val maxLength: Int = Wire.MAX_FRAME_LENGTH) {
    private val head = ByteArray(Wire.HEADER_LENGTH)

    /** Next frame, or null on clean EOF at a frame boundary. */
    fun read(): Frame? {
        if (!readFully(head, allowEof = true)) return null
        val b = ByteBuffer.wrap(head).order(ByteOrder.LITTLE_ENDIAN)
        val version = b.get().toInt() and 0xff
        val type = b.get().toInt() and 0xff
        val ts = b.long
        val metaLen = b.short.toInt() and 0xffff
        val dataLen = b.int
        if (version != Wire.VERSION) throw ProtocolException("version $version")
        if (type != Wire.FRAME_METADATA && type != Wire.FRAME_VIDEO && type != Wire.FRAME_AUDIO) {
            throw ProtocolException("frame type $type")
        }
        if (dataLen < 0 || dataLen > maxLength) throw ProtocolException("data length $dataLen")
        val data = ByteArray(dataLen)
        readFully(data, allowEof = false)
        return Frame(version, type, ts, metaLen, data)
    }

    private fun readFully(buf: ByteArray, allowEof: Boolean): Boolean {
        var off = 0
        while (off < buf.size) {
            val n = input.read(buf, off, buf.size - off)
            if (n < 0) {
                if (off == 0 && allowEof) return false
                throw EOFException("stream ended inside a frame")
            }
            off += n
        }
        return true
    }
}
