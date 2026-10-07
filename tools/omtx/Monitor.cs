using System.Runtime.InteropServices;
using libomtnet;

namespace Omtx;

/// <summary>UYVY picture -> JPEG at a requested width, through libavcodec's mjpeg encoder.</summary>
internal sealed unsafe class JpegEncoder : IDisposable
{
    public readonly int Width, Height;
    private IntPtr ctx, frame, pkt, sws;
    private int swsW, swsH;
    private readonly int dstFmt;
    private long pts;

    public JpegEncoder(int width, int height)
    {
        Width = width; Height = height;
        IntPtr codec = Av.FindEncoder("mjpeg");
        if (codec == IntPtr.Zero) throw new InvalidOperationException("mjpeg encoder missing");
        // yuvj420p is the full-range format mjpeg has always taken; newer FFmpeg also takes yuv420p + pc range
        foreach (var (fmt, range) in new[] { ("yuvj420p", (string)null), ("yuv420p", "pc") })
        {
            ctx = Av.AllocContext(codec);
            Av.OptSet(ctx, "video_size", $"{width}x{height}");
            if (Av.OptSet(ctx, "pixel_format", fmt) < 0) { Av.FreeContext(ref ctx); continue; }
            if (range != null) { Av.OptSet(ctx, "color_range", range); Av.OptSet(ctx, "strict", "-1"); }
            Av.OptSetQ(ctx, "time_base", 1, 25);
            Av.OptSet(ctx, "flags", "+qscale");
            Av.OptSetInt(ctx, "global_quality", 5 * 118); // FF_QP2LAMBDA * q
            if (Av.Open(ctx, codec) >= 0) { dstFmt = Av.PixFmt(fmt); break; }
            Av.FreeContext(ref ctx);
        }
        if (ctx == IntPtr.Zero) throw new InvalidOperationException("mjpeg encoder would not open");
        frame = Av.FrameAlloc();
        Av.FrameWidth(frame) = width; Av.FrameHeight(frame) = height; Av.FrameFormat(frame) = dstFmt;
        if (Av.FrameGetBuffer(frame) < 0) throw new InvalidOperationException("av_frame_get_buffer");
        pkt = Av.PacketAlloc();
    }

    public byte[] Encode(IntPtr uyvy, int stride, int w, int h)
    {
        if (sws == IntPtr.Zero || swsW != w || swsH != h)
        {
            Av.SwsFree(sws);
            sws = Av.SwsGet(w, h, Av.PixFmt("uyvy422"), Width, Height, dstFmt);
            swsW = w; swsH = h;
        }
        Av.FrameMakeWritable(frame);
        byte** src = stackalloc byte*[4];
        int* srcStride = stackalloc int[4];
        src[0] = (byte*)uyvy; src[1] = src[2] = src[3] = null;
        srcStride[0] = stride; srcStride[1] = srcStride[2] = srcStride[3] = 0;
        Av.SwsScale(sws, src, srcStride, h, Av.FrameData(frame), Av.FrameLinesize(frame));
        Av.FramePts(frame) = pts++; // mjpeg refuses a timestamp that does not increase
        if (Av.SendFrame(ctx, frame) < 0) return null;
        if (Av.ReceivePacket(ctx, pkt) < 0) return null;
        var bytes = new byte[Av.PacketSize(pkt)];
        Marshal.Copy(Av.PacketData(pkt), bytes, 0, bytes.Length);
        Av.PacketUnref(pkt);
        return bytes;
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
/// Receives one source for the UI: keeps the latest picture (UYVY) and live stats, makes JPEGs on
/// request. Stock OMT sources use the VMX preview layer unless Full (the Watch page), so thumbnails
/// of vMix outputs do not pull 40+ Mbps each. Stops itself when nobody has asked for it for a while.
/// </summary>
internal sealed class SourceMonitor
{
    public readonly string Name;
    public readonly bool Omtx, Full;
    public readonly StreamStats Stats = new();
    private readonly object pic = new();
    private IntPtr buf = IntPtr.Zero;
    private int bufSize, picW, picH, picStride;
    public long Seq;                       // increments per new picture
    private readonly Dictionary<int, (long seq, byte[] jpeg)> jpegCache = new();
    private readonly Dictionary<int, JpegEncoder> encoders = new();
    private long lastUsed = Environment.TickCount64;
    private volatile bool stop;
    private Thread thread;

    public SourceMonitor(string name, bool omtx, bool full)
    {
        Name = name; Omtx = omtx; Full = full;
        Stats.Codec = omtx ? null : "VMX1";
    }

    public void Touch() => Interlocked.Exchange(ref lastUsed, Environment.TickCount64);
    public bool Idle(long ms) => Environment.TickCount64 - Interlocked.Read(ref lastUsed) > ms;
    public bool Alive => thread != null && thread.IsAlive;

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "monitor " + Name };
        thread.Start();
    }

    public void Stop() { stop = true; thread?.Join(3000); }

    private void Keep(IntPtr src, int stride, int w, int h)
    {
        lock (pic)
        {
            int size = stride * h;
            if (bufSize < size)
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size); bufSize = size;
            }
            unsafe { Buffer.MemoryCopy((void*)src, (void*)buf, bufSize, size); }
            picW = w; picH = h; picStride = stride;
            Seq++;
        }
    }

    /// <summary>JPEG of the latest picture, scaled to width (even), or null if there is none yet.</summary>
    public byte[] Jpeg(int width, out long seq)
    {
        lock (pic)
        {
            seq = Seq;
            if (buf == IntPtr.Zero || picW == 0) return null;
            width = Math.Clamp(width & ~1, 64, picW & ~1);
            if (jpegCache.TryGetValue(width, out var c) && c.seq == Seq) return c.jpeg;
            int height = Math.Max(2, (int)Math.Round((double)picH * width / picW) & ~1);
            if (!encoders.TryGetValue(width, out var enc) || enc.Height != height)
            {
                enc?.Dispose();
                enc = new JpegEncoder(width, height);
                encoders[width] = enc;
            }
            var jpeg = enc.Encode(buf, picStride, picW, picH);
            jpegCache[width] = (Seq, jpeg);
            return jpeg;
        }
    }

    private void Run()
    {
        var flags = (!Omtx && !Full) ? OMTReceiveFlags.Preview : OMTReceiveFlags.None;
        using var recv = new OMTReceive(Program.NormaliseAddress(Name), OMTFrameType.Video | OMTFrameType.Audio, OMTPreferredVideoFormat.UYVY, flags);
        VideoDecoder dec = null;
        int decCodec = 0;
        var frame = new OMTMediaFrame();
        try
        {
            while (!stop && Program.Running)
            {
                if (!recv.Receive(OMTFrameType.Video | OMTFrameType.Audio, 200, ref frame)) continue;
                if (frame.Type == OMTFrameType.Audio)
                {
                    Stats.AudioRate = frame.SampleRate; Stats.AudioChannels = frame.Channels;
                    continue;
                }
                if (frame.Type != OMTFrameType.Video) continue;
                Stats.FrameRate = $"{frame.FrameRateN}/{frame.FrameRateD}";
                Stats.Drops = recv.GetVideoStatistics().FramesDropped;
                bool key = frame.Flags.HasFlag(OMTVideoFlags.Keyframe);
                if (frame.Codec == (int)OMTCodec.H264 || frame.Codec == (int)OMTCodec.HEVC)
                {
                    Stats.Codec = StreamStats.CodecName(frame.Codec);
                    Stats.Width = frame.Width; Stats.Height = frame.Height;
                    Stats.Frame(frame.DataLength, key);
                    if (dec == null || decCodec != frame.Codec)
                    {
                        dec?.Dispose();
                        bool hevc = frame.Codec == (int)OMTCodec.HEVC;
                        dec = new VideoDecoder(VideoDecoder.Defaults(hevc), lowLatency: false);
                        decCodec = frame.Codec;
                        Stats.Decoder = dec.Name;
                    }
                    if (!dec.Decode(frame.Data, frame.DataLength, frame.Timestamp, (p, stride, w, h, pts) => Keep(p, stride, w, h)))
                        recv.RequestKeyframe();
                }
                else
                {
                    // stock OMT: libomtnet already decoded VMX (full or preview layer) to UYVY
                    Stats.Width = frame.Width; Stats.Height = frame.Height;
                    Stats.Frame(frame.CompressedLength > 0 ? frame.CompressedLength : frame.DataLength);
                    if (frame.Codec == (int)OMTCodec.UYVY || frame.Codec == (int)OMTCodec.UYVA)
                        Keep(frame.Data, frame.Stride, frame.Width, frame.Height);
                }
            }
        }
        finally
        {
            dec?.Dispose();
            lock (pic)
            {
                foreach (var e in encoders.Values) e.Dispose();
                encoders.Clear();
                if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
            }
        }
    }
}
