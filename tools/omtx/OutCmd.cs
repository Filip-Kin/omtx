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
        string sourceName = a.Positional.Count > 0 ? a.Positional[0] : PickLocalSource();
        if (sourceName == null) return 0;
        bool hevc = a.Get("--codec", "h264").ToLowerInvariant() is "hevc" or "h265";
        var opts = new OutOptions
        {
            Source = sourceName,
            Name = a.Get("--name"),
            Hevc = hevc,
            Encoders = a.Has("--encoder") ? a.List("--encoder", "") : null,
            CeilingBps = a.Int("--bitrate", 10000) * 1000L,
            IntraRefresh = a.Has("--intra-refresh"),
            Bgra = a.Get("--pixfmt", "nv12").ToLowerInvariant() == "bgra",
            VbvFrames = a.Dbl("--vbv", 1.0),
            EncoderOptions = a.List("--enc-opts", "").Select(kv => kv.Split('=', 2)).Where(x => x.Length == 2)
                              .Select(x => (x[0].Trim(), x[1].Trim())).ToList(),
        };
        opts.FloorBps = Math.Min(opts.CeilingBps, a.Int("--min", 3000) * 1000L);
        var bridge = new OutBridge(opts);
        bridge.Start();
        bool stats = a.Has("--stats");
        int tick = 0;
        while (Program.Running && !bridge.Finished)
        {
            Thread.Sleep(200);
            if (stats && ++tick % 25 == 0 && bridge.State == "running")
                Console.Error.WriteLine("[out] " + bridge.Stats.Line());
        }
        bridge.Stop();
        return bridge.State == "error" ? 1 : 0;
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
    internal static string DefaultName(string source)
    {
        int open = source.IndexOf('('), close = source.LastIndexOf(')');
        if (open < 0 || close < open) return "omtx"; // a URL has no name to keep
        return source.Substring(open + 1, close - open - 1) + " omtx";
    }
}
