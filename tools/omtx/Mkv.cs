using System.Buffers.Binary;
using System.Text;

namespace Omtx;

/// <summary>
/// Minimal live Matroska writer for ffplay's stdin: one H.264/HEVC track (Annex B in the
/// blocks, parameter sets in band) and an optional float PCM track. Segment and clusters use
/// unknown sizes, so nothing is ever rewritten. Timestamps are milliseconds.
/// </summary>
internal sealed class MkvWriter
{
    private readonly Stream o;
    private readonly bool hasAudio;
    private long clusterStart = long.MinValue;

    public MkvWriter(Stream output, bool hevc, int width, int height, int sampleRate, int channels)
    {
        o = output;
        hasAudio = sampleRate > 0 && channels > 0;

        var ebml = Master(0x1A45DFA3,
            UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8),
            Str(0x4282, "matroska"), UInt(0x4287, 4), UInt(0x4285, 2));
        o.Write(ebml);

        // Segment, unknown size
        o.Write(Id(0x18538067)); o.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

        o.Write(Master(0x1549A966, UInt(0x2AD7B1, 1000000), Str(0x4D80, "omtx"), Str(0x5741, "omtx")));

        var video = Master(0xAE,
            UInt(0xD7, 1), UInt(0x73C5, 1), UInt(0x83, 1), UInt(0x9C, 0),
            Str(0x86, hevc ? "V_MPEGH/ISO/HEVC" : "V_MPEG4/ISO/AVC"),
            Master(0xE0, UInt(0xB0, (ulong)width), UInt(0xBA, (ulong)height)));
        if (hasAudio)
        {
            var audio = Master(0xAE,
                UInt(0xD7, 2), UInt(0x73C5, 2), UInt(0x83, 2), UInt(0x9C, 0),
                Str(0x86, "A_PCM/FLOAT/IEEE"),
                Master(0xE1, Float(0xB5, sampleRate), UInt(0x9F, (ulong)channels), UInt(0x6264, 32)));
            o.Write(Master(0x1654AE6B, video, audio));
        }
        else
        {
            o.Write(Master(0x1654AE6B, video));
        }
        o.Flush();
    }

    public void WriteVideo(long ms, byte[] data, int length, bool keyframe)
    {
        if (keyframe || ms - clusterStart > 1000 || ms < clusterStart) NewCluster(ms);
        Block(1, ms, data, length, keyframe);
    }

    public void WriteAudio(long ms, byte[] interleavedFloat, int length)
    {
        if (!hasAudio || clusterStart == long.MinValue) return;
        if (ms - clusterStart > 30000 || ms < clusterStart) NewCluster(ms);
        Block(2, ms, interleavedFloat, length, true);
    }

    private void NewCluster(long ms)
    {
        clusterStart = ms;
        o.Write(Id(0x1F43B675)); o.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        o.Write(UInt(0xE7, (ulong)Math.Max(0, ms)));
    }

    private void Block(int track, long ms, byte[] data, int length, bool key)
    {
        short rel = (short)Math.Clamp(ms - clusterStart, short.MinValue, short.MaxValue);
        o.Write(Id(0xA3));
        o.Write(Size(length + 4));
        o.WriteByte((byte)(0x80 | track));
        Span<byte> t = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(t, rel);
        o.Write(t);
        o.WriteByte(key ? (byte)0x80 : (byte)0x00);
        o.Write(data, 0, length);
        o.Flush();
    }

    // ---- EBML encoding ----
    static byte[] Id(uint id)
    {
        if (id > 0xFFFFFF) return new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id };
        if (id > 0xFFFF) return new[] { (byte)(id >> 16), (byte)(id >> 8), (byte)id };
        if (id > 0xFF) return new[] { (byte)(id >> 8), (byte)id };
        return new[] { (byte)id };
    }

    static byte[] Size(long n)
    {
        // 8-byte size vint, always valid and simple
        var b = new byte[8];
        b[0] = 0x01;
        for (int i = 7; i >= 1; i--) { b[i] = (byte)n; n >>= 8; }
        return b;
    }

    static byte[] Element(uint id, byte[] payload)
    {
        var ms = new MemoryStream();
        ms.Write(Id(id)); ms.Write(Size(payload.Length)); ms.Write(payload);
        return ms.ToArray();
    }

    static byte[] Master(uint id, params byte[][] children)
    {
        var ms = new MemoryStream();
        foreach (var c in children) ms.Write(c);
        return Element(id, ms.ToArray());
    }

    static byte[] UInt(uint id, ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        int skip = 0;
        while (skip < 7 && b[skip] == 0) skip++;
        return Element(id, b[skip..]);
    }

    static byte[] Float(uint id, double v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, v);
        return Element(id, b);
    }

    static byte[] Str(uint id, string s) => Element(id, Encoding.ASCII.GetBytes(s));
}
