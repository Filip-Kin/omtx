using libomtnet;

namespace Omtx;

internal static class Program
{
    public const string Version = "0.1.0";
    public static volatile bool Running = true;

    const string Usage = @"omtx 0.1.0: OMT with H.264/HEVC for Wi-Fi

  omtx list [--seconds N]
      Sources on the network, both stock OMT (_omt._tcp) and omtx (_omtx._tcp).

  omtx play <source> [high|medium|low] [--window WxH+X+Y] [--audio] [--ffplay PATH] [--no-vsync] [--stats]
      Receive an omtx source and play it fullscreen (window mode with --window). Video only:
      decoded here and drawn with SDL2 the moment each picture is ready. With --audio, --ffplay,
      or no SDL2/libavcodec, plays through ffplay instead (audio clock, more latency).
      <source> is a name ""HOST (Name)"", omtx://host:port or host:port.

  omtx out [<stock OMT source>] [--name NAME] [--codec h264|hevc] [--bitrate KBPS] [--min KBPS]
           [--encoder LIST] [--enc-opts K=V,...] [--pixfmt nv12|bgra] [--intra-refresh] [--vbv FRAMES] [--ffmpeg DIR] [--stats]
      Re-encode a stock OMT source (e.g. a vMix output) as omtx. With no source, every OMT source
      on this PC. Each one connects and encodes only while an omtx receiver watches it. Default
      encoders tried in order:
      h264_nvenc, h264_qsv, libx264, h264_amf. Default 10000 kbps ceiling, 3000 kbps floor.

  omtx in [<omtx source> ...] [--decoder LIST] [--ffmpeg DIR] [--stats]
      Decode omtx sources (e.g. phones) and republish each as a stock OMT source for vMix/OBS.
      With no source given, bridges every omtx source that appears on the network. Each one
      connects to its camera only while something (vMix) has the OMT source open.

  omtx ui [--port 6390] [--listen 127.0.0.1] [--no-open] [--no-auto] [--ffmpeg DIR]
      Local web page: every OMT and omtx source with previews and stats, and out/in bridges.
      Unless --no-auto, also runs both directions: ""out"" and ""in"" with no source, as above.

  omtx bars [--name NAME] [--size WxH] [--fps N] [--omtx] [--noise|--texture] [--encoder LIST]
           [--bitrate KBPS] [--intra-refresh] [--vbv FRAMES] [--ffmpeg DIR]
      Test pattern with a tone. Stock OMT (VMX) by default, omtx H.264 with --omtx. The bottom
      strip is the wall clock in ms (16 bits) for latency checks; --noise fills the top third with
      fresh noise every frame, --texture with the same noise every frame.

  omtx probe <source> [--seconds 10] [--strip X0,X1,Y] [--clock http://HOST:6390] [--ffmpeg DIR]
      Latency of a source carrying the bars clock strip, read after decode in this process (no
      display). --strip locates the strip when bars sits inside a vMix layout; --clock corrects for
      the bars machine's clock through its omtx ui.
";

    static int Main(string[] args)
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Running = false; };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Running = false;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.Write(Usage); return args.Length == 0 ? 1 : 0; }
        var a = new Args(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "list" => ListCmd.Run(a),
                "play" => PlayCmd.Run(a),
                "out" => OutCmd.Run(a),
                "in" => InCmd.Run(a),
                "bars" => BarsCmd.Run(a),
                "ui" => UiCmd.Run(a),
                "probe" => ProbeCmd.Run(a),
                "--version" or "version" => Print(Version),
                _ => Fail("Unknown command: " + args[0] + "\n\n" + Usage),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("omtx: " + ex.Message);
            return 1;
        }
    }

    static int Print(string s) { Console.WriteLine(s); return 0; }
    public static int Fail(string s) { Console.Error.WriteLine(s); return 2; }

    /// <summary>omtx://host:port and host:port become omt://host:port; a discovery name stays as is.</summary>
    public static string NormaliseAddress(string s)
    {
        if (s.StartsWith("omtx://")) return "omt://" + s.Substring(7);
        if (s.StartsWith("omt://")) return s;
        if (s.Contains('(')) return s;
        return "omt://" + s;
    }

    public static int FpsOf(OMTMediaFrame f) => f.FrameRateD > 0 ? Math.Max(1, (int)Math.Round((double)f.FrameRateN / f.FrameRateD)) : 30;

    public static int AvFormatOf(int omtCodec) => omtCodec switch
    {
        (int)OMTCodec.UYVY or (int)OMTCodec.UYVA => Av.PixFmt("uyvy422"),
        (int)OMTCodec.BGRA => Av.PixFmt("bgra"),
        (int)OMTCodec.YUY2 => Av.PixFmt("yuyv422"),
        (int)OMTCodec.NV12 => Av.PixFmt("nv12"),
        _ => -1,
    };

    public static void Sleep(int ms) { if (Running) Thread.Sleep(ms); }
}

internal sealed class Args
{
    public readonly List<string> Positional = new();
    private readonly Dictionary<string, string> named = new();
    private static readonly HashSet<string> Flags = new() { "--stats", "--no-audio", "--omtx", "--intra-refresh", "--noise", "--audio", "--no-open", "--no-vsync", "--texture", "--no-auto" };

    public Args(string[] a)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--"))
            {
                if (Flags.Contains(a[i]) || i + 1 >= a.Length) named[a[i]] = "1";
                else named[a[i]] = a[++i];
            }
            else Positional.Add(a[i]);
        }
    }
    public bool Has(string k) => named.ContainsKey(k);
    public string Get(string k, string def = null) => named.TryGetValue(k, out var v) ? v : def;
    public int Int(string k, int def) => int.TryParse(Get(k), out var v) ? v : def;
    public double Dbl(string k, double def) => double.TryParse(Get(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
    public string[] List(string k, string def) => (Get(k, def) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class ListCmd
{
    public static int Run(Args a)
    {
        var d = OMTDiscovery.GetInstance();
        int secs = a.Int("--seconds", 3);
        for (int i = 0; i < secs * 10 && Program.Running; i++) Thread.Sleep(100);
        foreach (var s in d.GetSources().OrderBy(x => x.ServiceType).ThenBy(x => x.ToString()))
        {
            string kind = s.ServiceType == OMTAddress.SERVICE_TYPE_OMTX ? "omtx" : "omt ";
            string ips = string.Join(",", s.Addresses.Select(x => x.ToString()));
            Console.WriteLine($"{kind}  {s}  {ips}:{s.Port}");
        }
        return 0;
    }
}
