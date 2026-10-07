using System.Diagnostics;
using System.Runtime.InteropServices;
using libomtnet;

namespace Omtx;

/// <summary>
/// omtx out: stock OMT source (e.g. vMix output over loopback) -> encoder -> omtx sender.
/// </summary>
internal static class OutCmd
{
    public static int Run(Args a)
    {
        Av.Load(a.Get("--ffmpeg"));
        string sourceName = a.Positional.Count > 0 ? a.Positional[0] : PickLocalSource();
        if (sourceName == null) return 0;
        string source = Program.NormaliseAddress(sourceName);
        bool hevc = a.Get("--codec", "h264").ToLowerInvariant() is "hevc" or "h265";
        string[] encoders = a.List("--encoder", hevc ? "hevc_nvenc,hevc_qsv,hevc_amf,libx265" : "h264_nvenc,h264_qsv,h264_amf,libx264");
        long ceiling = a.Int("--bitrate", 10000) * 1000L;
        long floor = Math.Min(ceiling, a.Int("--min", 3000) * 1000L);
        bool intraRefresh = a.Has("--intra-refresh");
        double vbv = a.Dbl("--vbv", 1.0);
        bool stats = a.Has("--stats");
        string name = a.Get("--name") ?? DefaultName(sourceName);
        var encOpts = a.List("--enc-opts", "").Select(kv => kv.Split('=', 2)).Where(x => x.Length == 2)
                       .Select(x => (x[0].Trim(), x[1].Trim())).ToList();

        using var recv = new OMTReceive(source, OMTFrameType.Video | OMTFrameType.Audio, OMTPreferredVideoFormat.UYVYorBGRA, OMTReceiveFlags.None);
        using var send = new OMTSend(name, OMTQuality.Default, OMTAddress.SERVICE_TYPE_OMTX);
        send.SetSenderInformation(new OMTSenderInfo("omtx out", "omtx", Program.Version));
        Console.Error.WriteLine($"omtx out: {sourceName} -> \"{name}\" ({OMTAddress.SERVICE_TYPE_OMTX}, {(hevc ? "HEVC" : "H.264")})");

        var rc = new RateControl(floor, ceiling, ceiling);
        var annexB = new AnnexB(hevc);
        var sent = new RateMeter();
        VideoEncoder enc = null;
        var frame = new OMTMediaFrame();
        var outFrame = new OMTMediaFrame();
        var statTimer = Stopwatch.StartNew();
        long statFrames = 0, statBytes = 0, statKeys = 0, statIn = 0;
        double sumRecv = 0, sumConv = 0, sumEnc = 0, sumSend = 0;
        int srcFpsN = 0, srcFpsD = 1;
        float aspect = 16f / 9f;
        OMTColorSpace colorSpace = OMTColorSpace.BT709;

        try
        {
            while (Program.Running)
            {
                long tr = Stopwatch.GetTimestamp();
                bool got = recv.Receive(OMTFrameType.Video | OMTFrameType.Audio, 200, ref frame);
                double recvMs = (Stopwatch.GetTimestamp() - tr) * 1000.0 / Stopwatch.Frequency;
                if (!got)
                {
                    if (enc == null && statTimer.ElapsedMilliseconds >= 10000)
                    {
                        Console.Error.WriteLine($"omtx out: no video from {sourceName} yet");
                        statTimer.Restart();
                    }
                    continue;
                }
                if (frame.Type == OMTFrameType.Audio)
                {
                    if (send.Connections > 0) send.Send(frame);
                    continue;
                }
                if (frame.Type != OMTFrameType.Video) continue;

                int fmt = Program.AvFormatOf(frame.Codec);
                if (fmt < 0) continue;
                if (send.Connections == 0)
                {
                    // Nobody watching: skip the encoder. The first subscriber asks for a keyframe anyway.
                    continue;
                }

                if (enc == null || enc.Width != frame.Width || enc.Height != frame.Height || frame.FrameRateN != srcFpsN || frame.FrameRateD != srcFpsD)
                {
                    enc?.Dispose();
                    srcFpsN = frame.FrameRateN > 0 ? frame.FrameRateN : 30;
                    srcFpsD = frame.FrameRateD > 0 ? frame.FrameRateD : 1;
                    enc = new VideoEncoder(encoders, hevc, frame.Width, frame.Height, srcFpsN, srcFpsD, rc.Current, intraRefresh, vbv,
                                           frame.ColorSpace == OMTColorSpace.BT601 || (frame.ColorSpace == OMTColorSpace.Undefined && frame.Height < 720),
                                           encOpts);
                    Console.Error.WriteLine($"omtx out: {enc.Name} {frame.Width}x{frame.Height} {srcFpsN}/{srcFpsD} {rc.Current / 1000} kbps");
                    send.ConsumeKeyframeRequest();
                    aspect = frame.AspectRatio > 0 ? frame.AspectRatio : (float)frame.Width / frame.Height;
                    colorSpace = frame.ColorSpace == OMTColorSpace.BT601 ? OMTColorSpace.BT601 : OMTColorSpace.BT709;
                }

                if (rc.Update(send.GetCongestionDrops(), send.GetMaxVideoFramesInFlight(), send.ReceiverSuggestedQuality, sent.BitsPerSecond))
                {
                    enc.SetRate(rc.Current);
                    if (stats) Console.Error.WriteLine($"[out] bitrate {rc.Current / 1000} kbps");
                }

                bool force = send.ConsumeKeyframeRequest();
                int w = frame.Width, h = frame.Height;
                statIn++; sumRecv += recvMs;
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
                        outFrame.Codec = hevc ? (int)OMTCodec.HEVC : (int)OMTCodec.H264;
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
                    statFrames++; statBytes += au.Length; if (isKey) statKeys++;
                });
                sumConv += enc.LastConvertMs; sumEnc += enc.LastEncodeMs; sumSend += enc.LastSendMs;

                if (stats && statTimer.ElapsedMilliseconds >= 5000)
                {
                    double sec = statTimer.Elapsed.TotalSeconds;
                    double n = Math.Max(1, statIn);
                    Console.Error.WriteLine($"[out] {statFrames / sec:F1} fps  {statBytes * 8 / sec / 1e6:F2} Mbps  keyframes {statKeys}  receivers {send.Connections}  in-flight {send.GetMaxVideoFramesInFlight()}  drops {send.GetCongestionDrops()}  target {rc.Current / 1000} kbps  ms/frame receive {sumRecv / n:F1} convert {sumConv / n:F1} encode {sumEnc / n:F1} (send {sumSend / n:F1})  source {srcFpsN}/{srcFpsD}");
                    statTimer.Restart(); statFrames = 0; statBytes = 0; statKeys = 0; statIn = 0; sumRecv = sumConv = sumEnc = sumSend = 0;
                }
            }
        }
        finally
        {
            enc?.Dispose();
        }
        return 0;
    }

    /// <summary>
    /// No source given: wait for stock OMT sources on this PC and take the vMix one
    /// (preferring Output 1), or the only one there is.
    /// </summary>
    static string PickLocalSource()
    {
        var discovery = OMTDiscovery.GetInstance();
        string machine = OMTAddress.SanitizeName(Environment.MachineName);
        var said = DateTime.MinValue;
        while (Program.Running)
        {
            var local = discovery.GetSources()
                .Where(s => s.ServiceType == OMTAddress.SERVICE_TYPE_OMT && string.Equals(s.MachineName, machine, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.ToString()).OrderBy(s => s).ToList();
            string pick = local.FirstOrDefault(s => s.Contains("vMix", StringComparison.OrdinalIgnoreCase) && s.Contains("Output 1", StringComparison.OrdinalIgnoreCase))
                       ?? local.FirstOrDefault(s => s.Contains("vMix", StringComparison.OrdinalIgnoreCase))
                       ?? (local.Count == 1 ? local[0] : null);
            if (pick != null) return pick;
            if (DateTime.UtcNow - said > TimeSpan.FromSeconds(10))
            {
                Console.Error.WriteLine(local.Count == 0
                    ? "omtx out: waiting for an OMT source on this PC (vMix: Settings > Outputs / NDI / SRT > OMT)"
                    : "omtx out: several OMT sources on this PC, name one: " + string.Join(", ", local.Select(s => $"\"{s}\"")));
                said = DateTime.UtcNow;
            }
            Thread.Sleep(500);
        }
        return null;
    }

    /// <summary>"FIMVIDEO3 (vMix - Output 1)" -> "vMix - Output 1 omtx". The machine name is added by OMT.</summary>
    static string DefaultName(string source)
    {
        int open = source.IndexOf('('), close = source.LastIndexOf(')');
        if (open < 0 || close < open) return "omtx"; // a URL has no name to keep
        return source.Substring(open + 1, close - open - 1) + " omtx";
    }
}
