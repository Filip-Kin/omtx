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
}

internal sealed class OutOptions
{
    public string Source;                 // stock OMT source name or omt:// URL
    public string Name;                   // published name, null = derived
    public bool Hevc;
    public string[] Encoders;
    public long CeilingBps = 10_000_000, FloorBps = 3_000_000;
    public bool IntraRefresh;
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
        o.Encoders ??= o.Hevc ? new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265" } : new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
        Stats.TargetKbps = o.CeilingBps / 1000;
    }

    public override IEnumerable<(string, string, StreamStats)> Streams() { yield return (Source, PublishedAs, Stats); }

    protected override void Run()
    {
        string source = Program.NormaliseAddress(Source);
        using var recv = new OMTReceive(source, OMTFrameType.Video | OMTFrameType.Audio, OMTPreferredVideoFormat.UYVYorBGRA, OMTReceiveFlags.None);
        using var send = new OMTSend(PublishedAs, OMTQuality.Default, OMTAddress.SERVICE_TYPE_OMTX);
        send.SetSenderInformation(new OMTSenderInfo("omtx out", "omtx", Program.Version));
        Log($"omtx out: {Source} -> \"{PublishedAs}\" ({OMTAddress.SERVICE_TYPE_OMTX}, {(o.Hevc ? "HEVC" : "H.264")})");
        State = "waiting";

        var rc = new RateControl(o.FloorBps, o.CeilingBps, o.CeilingBps);
        var annexB = new AnnexB(o.Hevc);
        var sent = new RateMeter();
        VideoEncoder enc = null;
        var frame = new OMTMediaFrame();
        var outFrame = new OMTMediaFrame();
        var waitTimer = Stopwatch.StartNew();
        int srcFpsN = 0, srcFpsD = 1;
        float aspect = 16f / 9f;
        OMTColorSpace colorSpace = OMTColorSpace.BT709;
        Stats.Codec = o.Hevc ? "HEVC" : "H264";

        try
        {
            while (Live)
            {
                long tr = Stopwatch.GetTimestamp();
                bool got = recv.Receive(OMTFrameType.Video | OMTFrameType.Audio, 200, ref frame);
                double recvMs = (Stopwatch.GetTimestamp() - tr) * 1000.0 / Stopwatch.Frequency;
                Stats.Receivers = send.Connections;
                if (!got)
                {
                    if (enc == null && waitTimer.ElapsedMilliseconds >= 10000)
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
                Stats.Width = frame.Width; Stats.Height = frame.Height;
                Stats.FrameRate = $"{frame.FrameRateN}/{frame.FrameRateD}";
                if (send.Connections == 0)
                {
                    // Nobody watching: skip the encoder. The first subscriber asks for a keyframe anyway.
                    State = "running";
                    continue;
                }

                if (enc == null || enc.Width != frame.Width || enc.Height != frame.Height || frame.FrameRateN != srcFpsN || frame.FrameRateD != srcFpsD)
                {
                    enc?.Dispose();
                    srcFpsN = frame.FrameRateN > 0 ? frame.FrameRateN : 30;
                    srcFpsD = frame.FrameRateD > 0 ? frame.FrameRateD : 1;
                    enc = new VideoEncoder(o.Encoders, o.Hevc, frame.Width, frame.Height, srcFpsN, srcFpsD, rc.Current, o.IntraRefresh, o.VbvFrames,
                                           frame.ColorSpace == OMTColorSpace.BT601 || (frame.ColorSpace == OMTColorSpace.Undefined && frame.Height < 720),
                                           o.EncoderOptions);
                    Stats.Encoder = enc.Name;
                    Log($"omtx out: {enc.Name} {frame.Width}x{frame.Height} {srcFpsN}/{srcFpsD} {rc.Current / 1000} kbps");
                    send.ConsumeKeyframeRequest();
                    aspect = frame.AspectRatio > 0 ? frame.AspectRatio : (float)frame.Width / frame.Height;
                    colorSpace = frame.ColorSpace == OMTColorSpace.BT601 ? OMTColorSpace.BT601 : OMTColorSpace.BT709;
                }
                State = "running";

                if (rc.Update(send.GetCongestionDrops(), send.GetMaxVideoFramesInFlight(), send.ReceiverSuggestedQuality, sent.BitsPerSecond))
                    enc.SetRate(rc.Current);
                Stats.TargetKbps = rc.Current / 1000;
                Stats.Drops = send.GetCongestionDrops();

                bool force = send.ConsumeKeyframeRequest();
                int w = frame.Width, h = frame.Height;
                enc.Encode(frame.Data, frame.Stride, w, h, fmt, frame.Timestamp, force, (buf, len, key, ts) =>
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
                        outFrame.Width = w; outFrame.Height = h;
                        outFrame.FrameRateN = srcFpsN; outFrame.FrameRateD = srcFpsD;
                        outFrame.AspectRatio = aspect;
                        outFrame.ColorSpace = colorSpace;
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
                Stats.Timings(recvMs, enc.LastConvertMs, enc.LastEncodeMs);
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

    public override IEnumerable<(string, string, StreamStats)> Streams() { yield return (Source, PublishedAs, Stats); }

    protected override void Run()
    {
        Log($"omtx in: {Source} -> \"{PublishedAs}\"");
        using var recv = new OMTReceive(Program.NormaliseAddress(Source), OMTFrameType.Video | OMTFrameType.Audio, OMTPreferredVideoFormat.UYVY, OMTReceiveFlags.None);
        using var send = new OMTSend(PublishedAs, OMTQuality.Default);
        send.SetSenderInformation(new OMTSenderInfo("omtx in", "omtx", Program.Version));
        State = "waiting";

        VideoDecoder dec = null;
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
                send.GetTally(0, ref tally);
                if (tally.Preview != lastTally.Preview || tally.Program != lastTally.Program)
                {
                    recv.SetTally(tally); // vMix tally back to the phone
                    lastTally = tally;
                }
                Stats.Receivers = send.Connections;
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
                    string list = decoderList ?? (hevc ? "hevc_cuvid,hevc" : "h264_cuvid,h264");
                    dec = new VideoDecoder(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
                bool ok = dec.Decode(frame.Data, frame.DataLength, frame.Timestamp, (ptr, stride, w, h, pts) =>
                {
                    long ts0 = Stopwatch.GetTimestamp();
                    outFrame.Type = OMTFrameType.Video;
                    outFrame.Codec = (int)OMTCodec.UYVY;
                    outFrame.Width = w; outFrame.Height = h; outFrame.Stride = stride;
                    outFrame.FrameRateN = src.FrameRateN > 0 ? src.FrameRateN : 30;
                    outFrame.FrameRateD = src.FrameRateD > 0 ? src.FrameRateD : 1;
                    outFrame.AspectRatio = src.AspectRatio > 0 ? src.AspectRatio : (float)w / h;
                    outFrame.ColorSpace = src.ColorSpace;
                    outFrame.Flags = OMTVideoFlags.None;
                    outFrame.Timestamp = src.Timestamp;
                    outFrame.Data = ptr;
                    outFrame.DataLength = stride * h;
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
            dec?.Dispose();
        }
    }
}

/// <summary>Bridges every omtx source that appears on the network (not this PC's own).</summary>
internal sealed class InAllBridge : Bridge
{
    private readonly string decoderList;
    private readonly Dictionary<string, InBridge> bridges = new();
    public override string Kind => "in";

    public InAllBridge(string decoders) { decoderList = decoders; Source = "*"; }

    public override IEnumerable<(string, string, StreamStats)> Streams()
    {
        InBridge[] list;
        lock (bridges) list = bridges.Values.ToArray();
        foreach (var b in list) yield return (b.Source, b.PublishedAs, b.Stats);
    }

    protected override void Run()
    {
        var discovery = OMTDiscovery.GetInstance();
        Log("omtx in: bridging every omtx source on the network");
        State = "running";
        try
        {
            while (Live)
            {
                foreach (var s in discovery.GetSources())
                {
                    if (s.ServiceType != OMTAddress.SERVICE_TYPE_OMTX || InCmd.IsOwnMachine(s)) continue;
                    string name = s.ToString();
                    lock (bridges)
                    {
                        if (bridges.TryGetValue(name, out var old) && !old.Finished) continue;
                        var b = new InBridge(name, decoderList);
                        bridges[name] = b;
                        b.Start();
                    }
                }
                for (int i = 0; i < 20 && Live; i++) Thread.Sleep(100);
            }
        }
        finally
        {
            InBridge[] list;
            lock (bridges) list = bridges.Values.ToArray();
            foreach (var b in list) b.Stop();
        }
    }
}
