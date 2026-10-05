using System.Runtime.InteropServices;

namespace Omtx;

/// <summary>
/// Low-latency H.264/HEVC encoder over libavcodec: no B-frames, CBR with a short VBV,
/// keyframes only on demand (no GOP timer, no scene-cut IDRs: each would be a burst into the
/// Wi-Fi link), bitrate changes without a restart. The one-frame VBV also caps a requested IDR at
/// about one frame's budget, so it does not burst either.
/// </summary>
internal sealed unsafe class VideoEncoder : IDisposable
{
    public readonly string Name;
    public readonly bool Hevc;
    public readonly int Width, Height, FpsN, FpsD;
    private IntPtr ctx, frame, pkt, sws;
    private int swsSrcFmt = -1, swsSrcW, swsSrcH;
    private readonly int encFmt;
    private long pts;
    private long bitrate;
    private readonly double vbvFrames;
    private readonly bool bt601;
    private readonly Queue<long> timestamps = new Queue<long>();
    private byte[] outBuf = new byte[1 << 20];

    /// <param name="encoders">Names to try in order, e.g. h264_nvenc then libx264.</param>
    public VideoEncoder(IEnumerable<string> encoders, bool hevc, int width, int height, int fpsN, int fpsD,
                        long bitrate, bool intraRefresh, double vbvFrames, bool bt601 = false)
    {
        this.bt601 = bt601;
        Hevc = hevc; Width = width; Height = height; FpsN = fpsN; FpsD = fpsD;
        this.bitrate = bitrate; this.vbvFrames = vbvFrames;
        encFmt = Av.PixFmt("nv12");
        var failures = new List<string>();
        foreach (var name in encoders)
        {
            IntPtr codec = Av.FindEncoder(name);
            if (codec == IntPtr.Zero) { failures.Add(name + ": not in this FFmpeg"); continue; }
            ctx = Av.AllocContext(codec);
            Configure(name, intraRefresh);
            int r = Av.Open(ctx, codec);
            if (r < 0) { failures.Add(name + ": " + Av.Err(r)); Av.FreeContext(ref ctx); continue; }
            Name = name;
            break;
        }
        if (ctx == IntPtr.Zero) throw new InvalidOperationException("No usable encoder. " + string.Join("; ", failures));

        frame = Av.FrameAlloc();
        Av.FrameWidth(frame) = width;
        Av.FrameHeight(frame) = height;
        Av.FrameFormat(frame) = encFmt;
        int g = Av.FrameGetBuffer(frame);
        if (g < 0) throw new InvalidOperationException("av_frame_get_buffer: " + Av.Err(g));
        pkt = Av.PacketAlloc();
    }

    private void Configure(string name, bool intraRefresh)
    {
        Must(Av.OptSet(ctx, "video_size", $"{Width}x{Height}"), "video_size");
        Must(Av.OptSet(ctx, "pixel_format", "nv12"), "pixel_format");
        Must(Av.OptSetQ(ctx, "time_base", FpsD, FpsN), "time_base");
        Av.OptSetQ(ctx, "framerate", FpsN, FpsD); // not an option in every FFmpeg; time_base covers it
        Av.OptSetInt(ctx, "bf", 0);
        // Colour tags in the stream (VUI), so every decoder picks the same matrix
        string cs = bt601 ? "smpte170m" : "bt709";
        Av.OptSet(ctx, "colorspace", cs);
        Av.OptSet(ctx, "color_primaries", cs);
        Av.OptSet(ctx, "color_trc", cs);
        Av.OptSet(ctx, "color_range", "tv");
        Av.OptSetInt(ctx, "g", Math.Max(1, FpsN / Math.Max(1, FpsD)) * 600); // keyframes on demand, not on a timer
        SetRate(bitrate);

        if (name.Contains("nvenc"))
        {
            if (Av.OptSet(ctx, "preset", "p1") < 0) Av.OptSet(ctx, "preset", "llhp"); // FFmpeg 4.x names
            Av.OptSet(ctx, "tune", "ull");
            Av.OptSet(ctx, "rc", "cbr");
            Av.OptSetInt(ctx, "zerolatency", 1);
            Av.OptSetInt(ctx, "delay", 0);
            Av.OptSetInt(ctx, "forced-idr", 1);
            Av.OptSetInt(ctx, "no-scenecut", 1);
            if (intraRefresh) Av.OptSetInt(ctx, "intra-refresh", 1);
        }
        else if (name == "libx264" || name == "libx265")
        {
            Av.OptSet(ctx, "preset", name == "libx264" ? "veryfast" : "ultrafast");
            Av.OptSet(ctx, "tune", "zerolatency");
            Av.OptSetInt(ctx, "forced-idr", 1);
            Av.OptSet(ctx, name == "libx264" ? "x264-params" : "x265-params", "scenecut=0");
            if (intraRefresh && name == "libx264") Av.OptSetInt(ctx, "intra-refresh", 1);
            Av.OptSetInt(ctx, "threads", 0);
        }
        else if (name.Contains("qsv"))
        {
            Av.OptSet(ctx, "preset", "veryfast");
            Av.OptSetInt(ctx, "low_power", 1);
            Av.OptSetInt(ctx, "forced_idr", 1);
            Av.OptSetInt(ctx, "async_depth", 1);
        }
        else if (name.Contains("amf"))
        {
            Av.OptSet(ctx, "usage", "ultralowlatency");
            Av.OptSet(ctx, "rc", "cbr");
        }
    }

    static void Must(int r, string what)
    {
        if (r < 0) throw new InvalidOperationException($"Encoder option {what}: {Av.Err(r)}");
    }

    public long Bitrate => bitrate;

    /// <summary>Change the target bitrate. Takes effect on the next frame (NVENC and x264 reconfigure in place).</summary>
    public void SetRate(long bps)
    {
        bitrate = bps;
        Av.OptSetInt(ctx, "b", bps);
        Av.OptSetInt(ctx, "maxrate", bps);
        long fps = Math.Max(1, FpsN / Math.Max(1, FpsD));
        Av.OptSetInt(ctx, "bufsize", (long)(bps * vbvFrames / fps));
    }

    /// <summary>
    /// Encode one picture. <paramref name="srcFmt"/> is an FFmpeg pixel format (uyvy422, bgra, nv12...).
    /// Packets come back through <paramref name="onPacket"/> with the source timestamp.
    /// </summary>
    public void Encode(IntPtr src, int stride, int srcW, int srcH, int srcFmt, long timestamp, bool forceKeyframe,
                       Action<byte[], int, bool, long> onPacket)
    {
        if (sws == IntPtr.Zero || swsSrcFmt != srcFmt || swsSrcW != srcW || swsSrcH != srcH)
        {
            Av.SwsFree(sws);
            sws = Av.SwsGet(srcW, srcH, srcFmt, Width, Height, encFmt);
            swsSrcFmt = srcFmt; swsSrcW = srcW; swsSrcH = srcH;
            if (sws == IntPtr.Zero) throw new InvalidOperationException("sws_getContext failed");
        }
        Av.FrameMakeWritable(frame);
        byte** srcData = stackalloc byte*[4];
        int* srcStride = stackalloc int[4];
        srcData[0] = (byte*)src; srcData[1] = srcData[2] = srcData[3] = null;
        srcStride[0] = stride; srcStride[1] = srcStride[2] = srcStride[3] = 0;
        if (srcFmt == Av.PixFmt("nv12"))
        {
            srcData[1] = (byte*)src + stride * srcH;
            srcStride[1] = stride;
        }
        Av.SwsScale(sws, srcData, srcStride, srcH, Av.FrameData(frame), Av.FrameLinesize(frame));

        Av.FramePts(frame) = pts++;
        Av.FramePictType(frame) = forceKeyframe ? Av.AV_PICTURE_TYPE_I : 0;
        timestamps.Enqueue(timestamp);
        int r = Av.SendFrame(ctx, frame);
        if (r < 0) { timestamps.Clear(); throw new InvalidOperationException("avcodec_send_frame: " + Av.Err(r)); }
        Drain(onPacket);
    }

    private void Drain(Action<byte[], int, bool, long> onPacket)
    {
        while (true)
        {
            int r = Av.ReceivePacket(ctx, pkt);
            if (r == Av.AVERROR_EAGAIN || r == Av.AVERROR_EOF) return;
            if (r < 0) throw new InvalidOperationException("avcodec_receive_packet: " + Av.Err(r));
            int size = Av.PacketSize(pkt);
            if (outBuf.Length < size) outBuf = new byte[size * 2];
            Marshal.Copy(Av.PacketData(pkt), outBuf, 0, size);
            bool key = (Av.PacketFlags(pkt) & Av.AV_PKT_FLAG_KEY) != 0;
            long ts = timestamps.Count > 0 ? timestamps.Dequeue() : -1;
            Av.PacketUnref(pkt);
            onPacket(outBuf, size, key, ts);
        }
    }

    public void Dispose()
    {
        Av.SwsFree(sws); sws = IntPtr.Zero;
        Av.FrameFree(ref frame);
        Av.PacketFree(ref pkt);
        Av.FreeContext(ref ctx);
    }
}

/// <summary>
/// H.264/HEVC decoder over libavcodec, converting every picture to UYVY for a stock OMT sender.
/// </summary>
internal sealed unsafe class VideoDecoder : IDisposable
{
    public readonly string Name;
    private IntPtr ctx, frame, pkt, sws;
    private int swsFmt = -1, swsW, swsH;
    private readonly int uyvy;
    private byte[] packetBuf = new byte[1 << 20];
    private GCHandle packetPin;
    private IntPtr outBuf = IntPtr.Zero;
    private int outSize;

    public VideoDecoder(IEnumerable<string> decoders)
    {
        uyvy = Av.PixFmt("uyvy422");
        var failures = new List<string>();
        foreach (var name in decoders)
        {
            IntPtr codec = Av.FindDecoder(name);
            if (codec == IntPtr.Zero) { failures.Add(name + ": not in this FFmpeg"); continue; }
            ctx = Av.AllocContext(codec);
            Av.OptSet(ctx, "flags", "low_delay");
            Av.OptSet(ctx, "thread_type", "slice");
            if (name.Contains("cuvid")) { Av.OptSetInt(ctx, "surfaces", 4); Av.OptSetInt(ctx, "delay", 0); }
            int r = Av.Open(ctx, codec);
            if (r < 0) { failures.Add(name + ": " + Av.Err(r)); Av.FreeContext(ref ctx); continue; }
            Name = name;
            break;
        }
        if (ctx == IntPtr.Zero) throw new InvalidOperationException("No usable decoder. " + string.Join("; ", failures));
        frame = Av.FrameAlloc();
        pkt = Av.PacketAlloc();
        packetPin = GCHandle.Alloc(packetBuf, GCHandleType.Pinned);
    }

    /// <summary>
    /// Decode one access unit. Each picture is handed to <paramref name="onPicture"/> as UYVY
    /// (pointer, stride, width, height). Returns false when the decoder rejected the data.
    /// </summary>
    public bool Decode(IntPtr data, int length, long timestamp, Action<IntPtr, int, int, int, long> onPicture)
    {
        // libavcodec wants AV_INPUT_BUFFER_PADDING_SIZE (64) zero bytes after the data
        if (packetBuf.Length < length + 64)
        {
            packetPin.Free();
            packetBuf = new byte[(length + 64) * 2];
            packetPin = GCHandle.Alloc(packetBuf, GCHandleType.Pinned);
        }
        Marshal.Copy(data, packetBuf, 0, length);
        Array.Clear(packetBuf, length, 64);
        Av.PacketData(pkt) = packetPin.AddrOfPinnedObject();
        Av.PacketSize(pkt) = length;
        Av.PacketPts(pkt) = timestamp;
        Av.PacketDts(pkt) = timestamp;
        int r = Av.SendPacket(ctx, pkt);
        Av.PacketData(pkt) = IntPtr.Zero;
        Av.PacketSize(pkt) = 0;
        if (r < 0 && r != Av.AVERROR_EAGAIN) return false;
        bool ok = true;
        while (true)
        {
            r = Av.ReceiveFrame(ctx, frame);
            if (r == Av.AVERROR_EAGAIN || r == Av.AVERROR_EOF) break;
            if (r < 0) { ok = false; break; }
            int w = Av.FrameWidth(frame), h = Av.FrameHeight(frame), fmt = Av.FrameFormat(frame);
            if (sws == IntPtr.Zero || fmt != swsFmt || w != swsW || h != swsH)
            {
                Av.SwsFree(sws);
                sws = Av.SwsGet(w, h, fmt, w, h, uyvy);
                swsFmt = fmt; swsW = w; swsH = h;
            }
            int stride = w * 2;
            if (outSize < stride * h)
            {
                if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
                outSize = stride * h;
                outBuf = Marshal.AllocHGlobal(outSize);
            }
            byte** dst = stackalloc byte*[4];
            int* dstStride = stackalloc int[4];
            dst[0] = (byte*)outBuf; dst[1] = dst[2] = dst[3] = null;
            dstStride[0] = stride; dstStride[1] = dstStride[2] = dstStride[3] = 0;
            Av.SwsScale(sws, Av.FrameData(frame), Av.FrameLinesize(frame), h, dst, dstStride);
            onPicture(outBuf, stride, w, h, Av.FramePts(frame));
        }
        return ok;
    }

    public void Dispose()
    {
        Av.SwsFree(sws); sws = IntPtr.Zero;
        Av.FrameFree(ref frame);
        Av.PacketFree(ref pkt);
        Av.FreeContext(ref ctx);
        if (packetPin.IsAllocated) packetPin.Free();
        if (outBuf != IntPtr.Zero) { Marshal.FreeHGlobal(outBuf); outBuf = IntPtr.Zero; }
    }
}
