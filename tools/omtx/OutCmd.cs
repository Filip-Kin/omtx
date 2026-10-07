using libomtnet;

namespace Omtx;

/// <summary>
/// omtx out: stock OMT source (e.g. vMix output over loopback) -> encoder -> omtx sender.
/// The work is in OutBridge (shared with omtx ui); this prints its stats.
/// </summary>
internal static class OutCmd
{
    public static int Run(Args a)
    {
        Av.Load(a.Get("--ffmpeg"));
        bool hevc = a.Get("--codec", "h264").ToLowerInvariant() is "hevc" or "h265";
        OutOptions Options(string source, string name) => new OutOptions
        {
            Source = source,
            Name = name,
            Hevc = hevc,
            Encoders = a.Has("--encoder") ? a.List("--encoder", "") : null,
            CeilingBps = a.Int("--bitrate", 10000) * 1000L,
            IntraRefresh = a.Has("--intra-refresh"),
            Bgra = a.Get("--pixfmt", "nv12").ToLowerInvariant() == "bgra",
            VbvFrames = a.Dbl("--vbv", 1.0),
            EncoderOptions = a.List("--enc-opts", "").Select(kv => kv.Split('=', 2)).Where(x => x.Length == 2)
                              .Select(x => (x[0].Trim(), x[1].Trim())).ToList(),
            FloorBps = Math.Min(a.Int("--bitrate", 10000), a.Int("--min", 3000)) * 1000L,
        };
        // No source: every OMT source on this PC (each encodes only while watched)
        Bridge bridge = a.Positional.Count > 0 ? new OutBridge(Options(a.Positional[0], a.Get("--name")))
                                               : AllBridge.Out(src => Options(src, null));
        bridge.Start();
        bool stats = a.Has("--stats");
        int tick = 0;
        while (Program.Running && !bridge.Finished)
        {
            Thread.Sleep(200);
            if (stats && ++tick % 25 == 0)
                foreach (var (src, published, st) in bridge.Streams())
                    if (st.Receivers > 0) Console.Error.WriteLine($"[out {published}] " + st.Line());
        }
        bridge.Stop();
        return bridge.State == "error" ? 1 : 0;
    }

    /// <summary>"FIMVIDEO3 (vMix - Output 1)" -> "vMix - Output 1 omtx". The machine name is added by OMT.</summary>
    internal static string DefaultName(string source)
    {
        int open = source.IndexOf('('), close = source.LastIndexOf(')');
        if (open < 0 || close < open) return "omtx"; // a URL has no name to keep
        return source.Substring(open + 1, close - open - 1) + " omtx";
    }
}
