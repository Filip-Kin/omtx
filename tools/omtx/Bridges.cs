using System.Diagnostics;
using System.Runtime.InteropServices;
using libomtnet;

namespace Omtx;

/// <summary>A running out/in bridge: own thread, live stats, stoppable. Used by the CLI and by omtx ui.</summary>
internal abstract class Bridge
{
    private Thread thread;
    protected volatile bool stopping;
    public string Id { get; set; }
    public abstract string Kind { get; }
    public string Source { get; protected set; }
    public volatile string State = "starting";
    public volatile string Error;

    protected bool Live => !stopping && Program.Running;

    public void Start()
    {
        thread = new Thread(() =>
        {
            try { Run(); if (State != "error") State = "stopped"; }
            catch (Exception ex) { Error = ex.Message; State = "error"; Log("omtx " + Kind + ": " + ex.Message); }
        }) { IsBackground = true, Name = Kind + " " + Source };
        thread.Start();
    }

    public void Stop()
    {
        stopping = true;
        thread?.Join(5000);
    }

    public bool Finished => thread != null && !thread.IsAlive;

    protected abstract void Run();

    /// <summary>One entry per published stream (in "*" has one per camera).</summary>
    public abstract IEnumerable<(string source, string publishedAs, StreamStats stats)> Streams();

    public static Action<string> Log = s => Console.Error.WriteLine(s);

    /// <summary>Stock OMT names this process publishes (in bridges), so the automatic out bridge
    /// never re-encodes them back to omtx.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> PublishedStock = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seconds a bridge stays connected upstream after its last viewer leaves.</summary>
    protected const int IdleSeconds = 5;
}

internal sealed class OutOptions
{
    public string Source;                 // stock OMT source name or omt:// URL
    public string Name;                   // published name, null = derived
    public bool Hevc;
    public string[] Encoders;
    // No cap: the encoders run at constant quality (see VideoEncoder) and take what the picture
    // needs. Only congestion steps the rate down.
    public long CeilingBps = 1_000_000_000, FloorBps = 3_000_000;
    public bool IntraRefresh;
    public bool Bgra;                     // ask for BGRA and give it to the GPU encoder (no CPU conversion)
    public double VbvFrames = 1.0;
    public List<(string, string)> EncoderOptions = new();
}

/// <summary>Stock OMT source (vMix output) -> encoder -> omtx sender.</summary>
internal sealed class OutBridge : Bridge
{
    private readonly OutOptions o;
    public readonly StreamStats Stats = new();
    public string PublishedAs { get; private set; }
    public override string Kind => "out";

    public OutBridge(OutOptions options)
    {
        o = options;
        Source = options.Source;
        PublishedAs = options.Name ?? OutCmd.DefaultName(options.Source);
        // x264 before AMF: through FFmpeg 8.1, AMF spends ~30 ms per 1080p frame copying it from system
        // memory to the GPU (measured on an RX 7900 XT, 2026-10-07), so it tops out near 30 fps, while
        // x264 on the same laptop does 60 fps at ~3 ms. NVENC and QSV stay first.
        o.Encoders ??= o.Hevc ? new[] { "hevc_nvenc", "hevc_qsv", "libx265", "hevc_amf" } : new[] { "h264_nvenc", "h264_qsv", "libx264", "h264_amf" };
        Stats.TargetKbps = o.CeilingBps / 1000;
    }

    public override IEnumerable<(string, string, StreamStats)> Streams() { Stats.State = State; Stats.Error = Error; yield return (Source, PublishedAs, Stats); }

    // A frame waiting for the encoder: a private copy of the picture plus what the encoder needs.
    private sealed class Job
    {
        public IntPtr Data; public int Size, Stride, Width, Height, Fmt, FpsN, FpsD;
        public float Aspect; public OMTColorSpace ColorSpace; public long Timestamp; public double RecvMs;
    }

    /// <summary>
    /// Two threads: this one receives (libomtnet decodes VMX inside Receive), copies the picture and
    /// queues it; the encode thread converts, encodes and sends. Their times overlap instead of adding
    /// up. The queue holds two frames; when the encoder falls behind the oldest is dropped, so delay
    /// never builds.
    /// </summary>
    protected override void Run()
    {
        string source = Program.NormaliseAddress(Source);
        var preferred = o.Bgra ? OMTPreferredVideoFormat.BGRA : OMTPreferredVideoFormat.UYVYorBGRA;
        using var send = new OMTSend(PublishedAs, OMTQuality.Default, OMTAddress.SERVICE_TYPE_OMTX);
        send.SetSenderInformation(new OMTSenderInfo("omtx out", "omtx", Program.Version));
        Log($"omtx out: {Source} -> \"{PublishedAs}\" ({OMTAddress.SERVICE_TYPE_OMTX}, {(o.Hevc ? "HEVC" : "H.264")}{(o.Bgra ? ", BGRA to the encoder" : "")})");
        State = "idle";
        // Connected to the source only while someone watches the omtx side: no VMX decode otherwise
        OMTReceive recv = null;
        var lastViewer = Stopwatch.StartNew();
        Stats.Codec = o.Hevc ? "HEVC" : "H264";

        var queue = new System.Collections.Concurrent.BlockingCollection<Job>(new System.Collections.Concurrent.ConcurrentQueue<Job>());
        var pool = new System.Collections.Concurrent.ConcurrentBag<Job>();
        long late = 0;
        Exception encodeError = null;
        var encoder = new Thread(() =>
        {
            try { EncodeLoop(queue, pool, send); }
            catch (Exception ex) { encodeError = ex; }
        }) { IsBackground = true, Name = "encode " + Source };
        encoder.Start();

        var frame = new OMTMediaFrame();
        var waitTimer = Stopwatch.StartNew();
        bool seenVideo = false;
        try
        {
            while (Live)
            {
                if (encodeError != null) throw encodeError;
                Stats.Receivers = send.Connections;
                if (send.Connections > 0) lastViewer.Restart();
                if (recv == null)
                {
                    if (send.Connections == 0) { Stats.Idle(); Thread.Sleep(100); continue; }
                    recv = new OMTReceive(source, OMTFrameType.Video | OMTFrameType.Audio, preferred, OMTReceiveFlags.None);
                    State = "waiting"; seenVideo = false; waitTimer.Restart();
                    Log($"omtx out: {PublishedAs}: viewer connected, receiving {Source}");
                }
                else if (lastViewer.Elapsed.TotalSeconds >= IdleSeconds)
                {
                    recv.Dispose(); recv = null; State = "idle";
                    Log($"omtx out: {PublishedAs}: no viewers, released {Source}");
                    continue;
                }
                long tr = Stopwatch.GetTimestamp();
                bool got = recv.Receive(OMTFrameType.Video | OMTFrameType.Audio, 200, ref frame);
                double recvMs = (Stopwatch.GetTimestamp() - tr) * 1000.0 / Stopwatch.Frequency;
                Stats.Receivers = send.Connections;
                if (!got)
                {
                    if (!seenVideo && waitTimer.ElapsedMilliseconds >= 10000)
                    {
                        Log($"omtx out: no video from {Source} yet");
                        waitTimer.Restart();
                    }
                    continue;
                }
                if (frame.Type == OMTFrameType.Audio)
                {
                    Stats.AudioRate = frame.SampleRate; Stats.AudioChannels = frame.Channels;
                    if (send.Connections > 0) send.Send(frame);
                    continue;
                }
                if (frame.Type != OMTFrameType.Video) continue;
                int fmt = Program.AvFormatOf(frame.Codec);
                if (fmt < 0) continue;
                seenVideo = true;
                Stats.Width = frame.Width; Stats.Height = frame.Height;
                Stats.FrameRate = $"{frame.FrameRateN}/{frame.FrameRateD}";
                if (send.Connections == 0) { State = "running"; continue; } // nobody watching: skip the encoder

                int size = frame.Stride * frame.Height;
                if (!pool.TryTake(out var job) || job.Size < size)
                {
                    if (job != null) Marshal.FreeHGlobal(job.Data);
                    job = new Job { Data = Marshal.AllocHGlobal(size), Size = size };
                }
                unsafe { Buffer.MemoryCopy((void*)frame.Data, (void*)job.Data, job.Size, size); }
                job.Stride = frame.Stride; job.Width = frame.Width; job.Height = frame.Height; job.Fmt = fmt;
                job.FpsN = frame.FrameRateN > 0 ? frame.FrameRateN : 30; job.FpsD = frame.FrameRateD > 0 ? frame.FrameRateD : 1;
                job.Aspect = frame.AspectRatio > 0 ? frame.AspectRatio : (float)frame.Width / frame.Height;
                job.ColorSpace = frame.ColorSpace; job.Timestamp = frame.Timestamp; job.RecvMs = recvMs;
                queue.Add(job);
                while (queue.Count > 2 && queue.TryTake(out var old)) { pool.Add(old); late++; }
                Stats.Drops = send.GetCongestionDrops() + late;
            }
        }
        finally
        {
            recv?.Dispose();
            queue.CompleteAdding();
            encoder.Join(3000);
            while (queue.TryTake(out var j)) pool.Add(j);
            foreach (var j in pool) Marshal.FreeHGlobal(j.Data);
        }
    }

    private void EncodeLoop(System.Collections.Concurrent.BlockingCollection<Job> queue,
                            System.Collections.Concurrent.ConcurrentBag<Job> pool, OMTSend send)
    {
        var rc = new RateControl(o.FloorBps, o.CeilingBps, o.CeilingBps);
        var annexB = new AnnexB(o.Hevc);
        var sent = new RateMeter();
        var outFrame = new OMTMediaFrame();
        VideoEncoder enc = null;
        int fpsN = 0, fpsD = 1;
        try
        {
            var lastJob = Stopwatch.StartNew();
            while (!queue.IsCompleted)
            {
                if (!queue.TryTake(out var job, 500))
                {
                    // Idle: give the encoder back (an NVENC session is a limited resource on GeForce cards)
                    if (enc != null && lastJob.Elapsed.TotalSeconds >= IdleSeconds) { enc.Dispose(); enc = null; fpsN = 0; }
                    continue;
                }
                lastJob.Restart();
                try
                {
                    if (enc == null || enc.Width != job.Width || enc.Height != job.Height || job.FpsN != fpsN || job.FpsD != fpsD)
                    {
                        enc?.Dispose();
                        fpsN = job.FpsN; fpsD = job.FpsD;
                        enc = new VideoEncoder(o.Encoders, o.Hevc, job.Width, job.Height, fpsN, fpsD, rc.Current, o.IntraRefresh, o.VbvFrames,
                                               job.ColorSpace == OMTColorSpace.BT601 || (job.ColorSpace == OMTColorSpace.Undefined && job.Height < 720),
                                               o.EncoderOptions, o.Bgra ? "bgr0" : "nv12");
                        Stats.Encoder = enc.Name;
                        Log($"omtx out: {enc.Name} {job.Width}x{job.Height} {fpsN}/{fpsD} {rc.Current / 1000} kbps");
                        send.ConsumeKeyframeRequest();
                    }
                    State = "running";
                    if (rc.Update(send.GetCongestionDrops(), send.GetMaxVideoFramesInFlight(), send.ReceiverSuggestedQuality, sent.BitsPerSecond))
                        enc.SetRate(rc.Current);
                    Stats.TargetKbps = rc.Current / 1000;

                    bool force = send.ConsumeKeyframeRequest();
                    var cs = job.ColorSpace == OMTColorSpace.BT601 ? OMTColorSpace.BT601 : OMTColorSpace.BT709;
                    enc.Encode(job.Data, job.Stride, job.Width, job.Height, job.Fmt, job.Timestamp, force, (buf, len, key, ts) =>
                    {
                        // Keyframe = an IDR/IRAP NAL is present. The packet key flag is not enough: x264 with
                        // intra refresh sets it on recovery-point frames, which a decoder cannot start on.
                        byte[] au = annexB.Process(buf, len, out bool isKey);
                        sent.Add(au.Length);
                        var handle = GCHandle.Alloc(au, GCHandleType.Pinned);
                        try
                        {
                            outFrame.Type = OMTFrameType.Video;
                            outFrame.Codec = o.Hevc ? (int)OMTCodec.HEVC : (int)OMTCodec.H264;
                            outFrame.Width = job.Width; outFrame.Height = job.Height;
                            outFrame.FrameRateN = fpsN; outFrame.FrameRateD = fpsD;
                            outFrame.AspectRatio = job.Aspect;
                            outFrame.ColorSpace = cs;
                            outFrame.Flags = isKey ? OMTVideoFlags.Keyframe : OMTVideoFlags.None;
                            outFrame.Timestamp = ts;
                            outFrame.Data = handle.AddrOfPinnedObject();
                            outFrame.DataLength = au.Length;
                            outFrame.FrameMetadata = IntPtr.Zero;
                            outFrame.FrameMetadataLength = 0;
                            send.Send(outFrame);
                        }
                        finally { handle.Free(); }
                        Stats.Frame(au.Length, isKey);
                    });
                    Stats.Timings(job.RecvMs, enc.LastConvertMs, enc.LastEncodeMs);
                }
                finally { pool.Add(job); }
            }
        }
        finally
        {
            enc?.Dispose();
        }
    }
}

/// <summary>One omtx source (a phone) -> decoder -> stock OMT sender for vMix/OBS. Tally goes back to the phone.</summary>
internal sealed class InBridge : Bridge
{
    private readonly string decoderList;
    public readonly StreamStats Stats = new();
    public string PublishedAs { get; }
    public override string Kind => "in";

    public InBridge(string source, string decoders)
    {
        Source = source;
        decoderList = decoders;
        // "PIXEL-9 (Camera)" is republished as "<this PC> (PIXEL-9 Camera)"
        PublishedAs = "omtx camera";
        int open = source.IndexOf('(');
        if (open > 0 && source.EndsWith(")"))
            PublishedAs = source.Substring(0, open).Trim() + " " + source.Substring(open + 1, source.Length - open - 2);
    }

    public override IEnumerable<(string, string, StreamStats)> Streams() { Stats.State = State; Stats.Error = Error; yield return (Source, PublishedAs, Stats); }

    protected override void Run()
    {
        Log($"omtx in: {Source} -> \"{PublishedAs}\"");
        PublishedStock[PublishedAs] = 0;
        using var send = new OMTSend(PublishedAs, OMTQuality.Default);
        send.SetSenderInformation(new OMTSenderInfo("omtx in", "omtx", Program.Version));
        State = "idle";
        // Connected to the camera only while something (vMix) has the stock OMT source open: no
        // decode, no VMX encode, and the phone can stop encoding, when nobody uses it
        OMTReceive recv = null;
        var lastViewer = Stopwatch.StartNew();

        VideoDecoder dec = null;
        using var nv12 = new Nv12Packer();
        int decCodec = 0;
        var frame = new OMTMediaFrame();
        var outFrame = new OMTMediaFrame();
        var tally = new OMTTally();
        var lastTally = new OMTTally(-1, -1);
        var lastKeyRequest = DateTime.MinValue;

        try
        {
            while (Live)
            {
                Stats.Receivers = send.Connections;
                if (send.Connections > 0) lastViewer.Restart();
                if (recv == null)
                {
                    if (send.Connections == 0) { Stats.Idle(); Thread.Sleep(100); continue; }
                    recv = new OMTReceive(Program.NormaliseAddress(Source), OMTFrameType.Video | OMTFrameType.Audio, OMTPreferredVideoFormat.UYVY, OMTReceiveFlags.None);
                    lastTally = new OMTTally(-1, -1); State = "waiting";
                    Log($"omtx in: {PublishedAs}: viewer connected, receiving {Source}");
                }
                else if (lastViewer.Elapsed.TotalSeconds >= IdleSeconds)
                {
                    recv.Dispose(); recv = null; State = "idle";
                    Log($"omtx in: {PublishedAs}: no viewers, released {Source}");
                    continue;
                }
                send.GetTally(0, ref tally);
                if (tally.Preview != lastTally.Preview || tally.Program != lastTally.Program)
                {
                    recv.SetTally(tally); // vMix tally back to the phone
                    lastTally = tally;
                }
                long tr = Stopwatch.GetTimestamp();
                if (!recv.Receive(OMTFrameType.Video | OMTFrameType.Audio, 100, ref frame)) continue;
                double recvMs = (Stopwatch.GetTimestamp() - tr) * 1000.0 / Stopwatch.Frequency;

                if (frame.Type == OMTFrameType.Audio)
                {
                    Stats.AudioRate = frame.SampleRate; Stats.AudioChannels = frame.Channels;
                    send.Send(frame);
                    continue;
                }
                if (frame.Type != OMTFrameType.Video) continue;
                if (frame.Codec != (int)OMTCodec.H264 && frame.Codec != (int)OMTCodec.HEVC) continue;

                if (dec == null || decCodec != frame.Codec)
                {
                    dec?.Dispose();
                    bool hevc = frame.Codec == (int)OMTCodec.HEVC;
                    dec = new VideoDecoder(decoderList != null
                        ? decoderList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        : VideoDecoder.Defaults(hevc));
                    decCodec = frame.Codec;
                    Stats.Decoder = dec.Name;
                    Stats.Codec = StreamStats.CodecName(frame.Codec);
                    Log($"omtx in: {Source}: decoder {dec.Name}");
                }
                State = "running";
                Stats.Width = frame.Width; Stats.Height = frame.Height;
                Stats.FrameRate = $"{frame.FrameRateN}/{frame.FrameRateD}";

                var src = frame;
                long td = Stopwatch.GetTimestamp();
                double sendMs = 0;
                bool ok = dec.DecodeFrames(frame.Data, frame.DataLength, frame.Timestamp, f =>
                {
                    // NV12 straight to the OMT sender (VMX takes it as is): no 4:2:2 conversion
                    var (ptr, stride, w, h) = nv12.From(f);
                    long ts0 = Stopwatch.GetTimestamp();
                    outFrame.Type = OMTFrameType.Video;
                    outFrame.Codec = (int)OMTCodec.NV12;
                    outFrame.Width = w; outFrame.Height = h; outFrame.Stride = stride;
                    outFrame.FrameRateN = src.FrameRateN > 0 ? src.FrameRateN : 30;
                    outFrame.FrameRateD = src.FrameRateD > 0 ? src.FrameRateD : 1;
                    outFrame.AspectRatio = src.AspectRatio > 0 ? src.AspectRatio : (float)w / h;
                    outFrame.ColorSpace = src.ColorSpace;
                    outFrame.Flags = OMTVideoFlags.None;
                    outFrame.Timestamp = src.Timestamp;
                    outFrame.Data = ptr;
                    outFrame.DataLength = stride * h * 3 / 2;
                    outFrame.FrameMetadata = IntPtr.Zero;
                    outFrame.FrameMetadataLength = 0;
                    send.Send(outFrame);
                    sendMs += (Stopwatch.GetTimestamp() - ts0) * 1000.0 / Stopwatch.Frequency;
                });
                double decMs = (Stopwatch.GetTimestamp() - td) * 1000.0 / Stopwatch.Frequency - sendMs;
                // for in: "convert" is the H.264 decode, "encode" is the stock OMT (VMX) send
                Stats.Timings(recvMs, decMs, sendMs);
                Stats.Frame(frame.DataLength, src.Flags.HasFlag(OMTVideoFlags.Keyframe));
                Stats.Drops = recv.GetVideoStatistics().FramesDropped;
                if (!ok && DateTime.UtcNow - lastKeyRequest > TimeSpan.FromSeconds(1))
                {
                    recv.RequestKeyframe();
                    lastKeyRequest = DateTime.UtcNow;
                }
            }
        }
        finally
        {
            recv?.Dispose();
            dec?.Dispose();
            PublishedStock.TryRemove(PublishedAs, out _);
        }
    }
}

/// <summary>
/// A decoded AVFrame as one contiguous NV12 buffer (Y rows, then interleaved UV rows, one stride),
/// the layout OMTSend takes. NV12 from a GPU decoder is copied row block by row block; anything
/// else (software yuv420p) is converted by swscale.
/// </summary>
internal sealed unsafe class Nv12Packer : IDisposable
{
    private readonly int nv12 = Av.PixFmt("nv12");
    private IntPtr buf, sws;
    private int size, swsFmt = -1, swsW, swsH;

    public (IntPtr data, int stride, int w, int h) From(IntPtr f)
    {
        int w = Av.FrameWidth(f), h = Av.FrameHeight(f), fmt = Av.FrameFormat(f);
        byte** d = Av.FrameData(f); int* l = Av.FrameLinesize(f);
        int stride = (w + 63) & ~63;
        int need = stride * h * 3 / 2;
        if (size < need)
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            buf = Marshal.AllocHGlobal(need); size = need;
        }
        byte* y = (byte*)buf, uv = y + stride * h;
        if (fmt == nv12)
        {
            Rows(d[0], l[0], y, stride, w, h);
            Rows(d[1], l[1], uv, stride, w, h / 2);
        }
        else
        {
            if (sws == IntPtr.Zero || fmt != swsFmt || w != swsW || h != swsH)
            {
                Av.SwsFree(sws);
                sws = Av.SwsGet(w, h, fmt, w, h, nv12);
                swsFmt = fmt; swsW = w; swsH = h;
            }
            byte** dst = stackalloc byte*[4];
            int* ds = stackalloc int[4];
            dst[0] = y; dst[1] = uv; dst[2] = dst[3] = null;
            ds[0] = ds[1] = stride; ds[2] = ds[3] = 0;
            Av.SwsScale(sws, d, l, h, dst, ds);
        }
        return (buf, stride, w, h);
    }

    static void Rows(byte* src, int srcStride, byte* dst, int dstStride, int bytes, int rows)
    {
        if (srcStride == dstStride) { Buffer.MemoryCopy(src, dst, (long)dstStride * rows, (long)dstStride * rows); return; }
        for (int r = 0; r < rows; r++) Buffer.MemoryCopy(src + (long)r * srcStride, dst + (long)r * dstStride, dstStride, bytes);
    }

    public void Dispose()
    {
        Av.SwsFree(sws); sws = IntPtr.Zero;
        if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
    }
}

/// <summary>
/// One bridge per matching source, added as sources appear. "in *": every omtx source on the network
/// (not this PC's own) into stock OMT. "out *": every stock OMT source on this PC (not the ones this
/// process publishes) out as omtx. Each one only works while it has a viewer (see IdleSeconds).
/// </summary>
internal sealed class AllBridge : Bridge
{
    private readonly string kind;
    private readonly Func<OMTAddress, bool> wanted;
    private readonly Func<string, Bridge> make;
    private readonly Dictionary<string, Bridge> bridges = new();
    private readonly Dictionary<string, DateTime> lastSeen = new();
    public override string Kind => kind;

    private AllBridge(string kind, Func<OMTAddress, bool> wanted, Func<string, Bridge> make)
    {
        this.kind = kind; this.wanted = wanted; this.make = make; Source = "*";
    }

    public static AllBridge In(string decoders) => new("in",
        s => s.ServiceType == OMTAddress.SERVICE_TYPE_OMTX && !InCmd.IsOwnMachine(s),
        name => new InBridge(name, decoders));

    public static AllBridge Out(Func<string, OutOptions> options) => new("out",
        s => s.ServiceType == OMTAddress.SERVICE_TYPE_OMT && InCmd.IsOwnMachine(s) && !PublishedStock.ContainsKey(s.Name),
        name => new OutBridge(options(name)));

    public override IEnumerable<(string, string, StreamStats)> Streams()
    {
        Bridge[] list;
        lock (bridges) list = bridges.Values.ToArray();
        foreach (var b in list) foreach (var st in b.Streams()) yield return st;
    }

    protected override void Run()
    {
        var discovery = OMTDiscovery.GetInstance();
        Log(kind == "in" ? "omtx in: bridging every omtx source on the network" : "omtx out: bridging every OMT source on this PC");
        State = "running";
        try
        {
            while (Live)
            {
                var now = DateTime.UtcNow;
                foreach (var s in discovery.GetSources())
                {
                    if (!wanted(s)) continue;
                    string name = s.ToString();
                    lastSeen[name] = now;
                    lock (bridges)
                    {
                        if (bridges.TryGetValue(name, out var old) && !old.Finished) continue;
                        var b = make(name);
                        bridges[name] = b;
                        b.Start();
                    }
                }
                // A source gone from the network for 10 s: stop its bridge, so its republished
                // name goes away too instead of advertising a stream that can never come
                foreach (var name in lastSeen.Where(x => now - x.Value > TimeSpan.FromSeconds(10)).Select(x => x.Key).ToList())
                {
                    lastSeen.Remove(name);
                    Bridge gone;
                    lock (bridges) { if (!bridges.Remove(name, out gone)) continue; }
                    Log($"omtx {kind}: {name} left the network, bridge stopped");
                    gone.Stop();
                }
                for (int i = 0; i < 20 && Live; i++) Thread.Sleep(100);
            }
        }
        finally
        {
            Bridge[] list;
            lock (bridges) list = bridges.Values.ToArray();
            foreach (var b in list) b.Stop();
        }
    }
}
