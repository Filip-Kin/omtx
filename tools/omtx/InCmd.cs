using System.Diagnostics;
using libomtnet;

namespace Omtx;

/// <summary>
/// omtx in: omtx sources (phones) -> decoder -> stock OMT senders that vMix and OBS can add.
/// Tally from vMix goes back to the phone.
/// </summary>
internal static class InCmd
{
    public static int Run(Args a)
    {
        Av.Load(a.Get("--ffmpeg"));
        bool stats = a.Has("--stats");
        string decoderList = a.Get("--decoder");
        var bridges = new Dictionary<string, Thread>();

        void Start(string source)
        {
            lock (bridges)
            {
                if (bridges.ContainsKey(source)) return;
                var t = new Thread(() =>
                {
                    Bridge(source, decoderList, stats);
                    lock (bridges) bridges.Remove(source); // a failed bridge is retried on the next discovery pass
                }) { IsBackground = true, Name = source };
                bridges[source] = t;
                t.Start();
            }
        }

        if (a.Positional.Count > 0)
        {
            foreach (var s in a.Positional) Start(s);
            while (Program.Running) Thread.Sleep(200);
            return 0;
        }

        // No sources named: bridge every omtx source that shows up
        var discovery = OMTDiscovery.GetInstance();
        Console.Error.WriteLine("omtx in: bridging every omtx source on the network");
        while (Program.Running)
        {
            foreach (var s in discovery.GetSources())
            {
                if (s.ServiceType == OMTAddress.SERVICE_TYPE_OMTX && !IsOwnMachine(s)) Start(s.ToString());
            }
            for (int i = 0; i < 20 && Program.Running; i++) Thread.Sleep(100);
        }
        return 0;
    }

    // omtx out on the same PC is an omtx source too; bridging it back into vMix makes a loop
    static bool IsOwnMachine(OMTAddress s) =>
        string.Equals(s.MachineName, OMTAddress.SanitizeName(Environment.MachineName), StringComparison.OrdinalIgnoreCase);

    static void Bridge(string source, string decoderList, bool stats)
    {
        // "PIXEL-9 (Camera)" is republished as "<this PC> (PIXEL-9 Camera)"
        string name = "omtx camera";
        int open = source.IndexOf('(');
        if (open > 0 && source.EndsWith(")"))
            name = source.Substring(0, open).Trim() + " " + source.Substring(open + 1, source.Length - open - 2);

        Console.Error.WriteLine($"omtx in: {source} -> \"{name}\"");
        using var recv = new OMTReceive(Program.NormaliseAddress(source), OMTFrameType.Video | OMTFrameType.Audio, OMTPreferredVideoFormat.UYVY, OMTReceiveFlags.None);
        using var send = new OMTSend(name, OMTQuality.Default);
        send.SetSenderInformation(new OMTSenderInfo("omtx in", "omtx", Program.Version));

        VideoDecoder dec = null;
        int decCodec = 0;
        var frame = new OMTMediaFrame();
        var outFrame = new OMTMediaFrame();
        var tally = new OMTTally();
        var lastTally = new OMTTally(-1, -1);
        var lastKeyRequest = DateTime.MinValue;
        var statTimer = Stopwatch.StartNew();
        long statFrames = 0, statBytes = 0;

        try
        {
            while (Program.Running)
            {
                send.GetTally(0, ref tally);
                if (tally.Preview != lastTally.Preview || tally.Program != lastTally.Program)
                {
                    recv.SetTally(tally); // vMix tally back to the phone
                    lastTally = tally;
                }
                if (!recv.Receive(OMTFrameType.Video | OMTFrameType.Audio, 100, ref frame)) continue;

                if (frame.Type == OMTFrameType.Audio)
                {
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
                    Console.Error.WriteLine($"omtx in: {source}: decoder {dec.Name}");
                }

                var src = frame;
                bool ok = dec.Decode(frame.Data, frame.DataLength, frame.Timestamp, (ptr, stride, w, h, pts) =>
                {
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
                    statFrames++;
                });
                statBytes += frame.DataLength;
                if (!ok && DateTime.UtcNow - lastKeyRequest > TimeSpan.FromSeconds(1))
                {
                    recv.RequestKeyframe();
                    lastKeyRequest = DateTime.UtcNow;
                }

                if (stats && statTimer.ElapsedMilliseconds >= 5000)
                {
                    double sec = statTimer.Elapsed.TotalSeconds;
                    Console.Error.WriteLine($"[in {name}] {statFrames / sec:F1} fps  {statBytes * 8 / sec / 1e6:F2} Mbps in  receivers {send.Connections}");
                    statTimer.Restart(); statFrames = 0; statBytes = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"omtx in: {source}: {ex.Message}");
        }
        finally
        {
            dec?.Dispose();
        }
    }
}
