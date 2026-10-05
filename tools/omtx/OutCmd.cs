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
        if (a.Positional.Count < 1) return Program.Fail("omtx out: source missing (see omtx list)");
        Av.Load(a.Get("--ffmpeg"));
        string source = Program.NormaliseAddress(a.Positional[0]);
        bool hevc = a.Get("--codec", "h264").ToLowerInvariant() is "hevc" or "h265";
        string[] encoders = a.List("--encoder", hevc ? "hevc_nvenc,hevc_qsv,hevc_amf,libx265" : "h264_nvenc,h264_qsv,h264_amf,libx264");
        long ceiling = a.Int("--bitrate", 10000) * 1000L;
        long floor = Math.Min(ceiling, a.Int("--min", 3000) * 1000L);
        bool intraRefresh = !a.Has("--no-intra-refresh");
        double vbv = a.Dbl("--vbv", 1.0);
        bool stats = a.Has("--stats");
        string name = a.Get("--name") ?? DefaultName(a.Positional[0]);

        using var recv = new OMTReceive(source, OMTFrameType.Video | OMTFrameType.Audio, OMTPreferredVideoFormat.UYVYorBGRA, OMTReceiveFlags.None);
        using var send = new OMTSend(name, OMTQuality.Default, OMTAddress.SERVICE_TYPE_OMTX);
        send.SetSenderInformation(new OMTSenderInfo("omtx out", "omtx", Program.Version));
        Console.Error.WriteLine($"omtx out: {a.Positional[0]} -> \"{name}\" ({OMTAddress.SERVICE_TYPE_OMTX}, {(hevc ? "HEVC" : "H.264")})");

        var rc = new RateControl(floor, ceiling, ceiling);
        var annexB = new AnnexB(hevc);
        VideoEncoder enc = null;
        var frame = new OMTMediaFrame();
        var outFrame = new OMTMediaFrame();
        var statTimer = Stopwatch.StartNew();
        long statFrames = 0, statBytes = 0, statKeys = 0;
        int srcFpsN = 0, srcFpsD = 1;
        float aspect = 16f / 9f;
        OMTColorSpace colorSpace = OMTColorSpace.BT709;

        try
        {
            while (Program.Running)
            {
                if (!recv.Receive(OMTFrameType.Video | OMTFrameType.Audio, 200, ref frame)) continue;
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
                    enc = new VideoEncoder(encoders, hevc, frame.Width, frame.Height, srcFpsN, srcFpsD, rc.Current, intraRefresh, vbv);
                    Console.Error.WriteLine($"omtx out: {enc.Name} {frame.Width}x{frame.Height} {srcFpsN}/{srcFpsD} {rc.Current / 1000} kbps");
                    send.ConsumeKeyframeRequest();
                    aspect = frame.AspectRatio > 0 ? frame.AspectRatio : (float)frame.Width / frame.Height;
                    colorSpace = frame.ColorSpace == OMTColorSpace.BT601 ? OMTColorSpace.BT601 : OMTColorSpace.BT709;
                }

                if (rc.Update(send.GetCongestionDrops(), send.GetMaxVideoFramesInFlight(), send.ReceiverSuggestedQuality))
                {
                    enc.SetRate(rc.Current);
                    if (stats) Console.Error.WriteLine($"[out] bitrate {rc.Current / 1000} kbps");
                }

                bool force = send.ConsumeKeyframeRequest();
                int w = frame.Width, h = frame.Height;
                enc.Encode(frame.Data, frame.Stride, w, h, fmt, frame.Timestamp, force, (buf, len, key, ts) =>
                {
                    byte[] au = annexB.Process(buf, len, out bool hasKeyNal);
                    bool isKey = key || hasKeyNal;
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

                if (stats && statTimer.ElapsedMilliseconds >= 5000)
                {
                    double sec = statTimer.Elapsed.TotalSeconds;
                    Console.Error.WriteLine($"[out] {statFrames / sec:F1} fps  {statBytes * 8 / sec / 1e6:F2} Mbps  keyframes {statKeys}  receivers {send.Connections}  in-flight {send.GetMaxVideoFramesInFlight()}  drops {send.GetCongestionDrops()}  target {rc.Current / 1000} kbps");
                    statTimer.Restart(); statFrames = 0; statBytes = 0; statKeys = 0;
                }
            }
        }
        finally
        {
            enc?.Dispose();
        }
        return 0;
    }

    /// <summary>"FIMVIDEO3 (vMix - Output 1)" -> "vMix - Output 1 omtx". The machine name is added by OMT.</summary>
    static string DefaultName(string source)
    {
        int open = source.IndexOf('('), close = source.LastIndexOf(')');
        string inner = open >= 0 && close > open ? source.Substring(open + 1, close - open - 1) : source;
        return inner + " omtx";
    }
}
