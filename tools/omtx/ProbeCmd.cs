using System.Text.Json;
using libomtnet;

namespace Omtx;

/// <summary>
/// omtx probe: receives a source carrying the omtx bars clock strip, decodes it in-process and
/// prints arrival latency (frame on the wire, then decoded) against this machine's clock. No
/// display in the loop, so subtracting two probes isolates one hop (e.g. a vMix OMT output
/// against the omtx out bridge fed by it).
/// </summary>
internal static class ProbeCmd
{
    public static unsafe int Run(Args a)
    {
        if (a.Positional.Count < 1) return Program.Fail("probe needs a source");
        string address = Program.NormaliseAddress(a.Positional[0]);
        int seconds = a.Int("--seconds", 10);
        double offset = ClockOffset(a.Get("--clock"));
        int[] strip = a.List("--strip", "").Select(int.Parse).ToArray(); // x0,x1,y in source pixels
        Av.Load(a.Get("--ffmpeg"));

        using var recv = new OMTReceive(address, OMTFrameType.Video, OMTPreferredVideoFormat.UYVY, OMTReceiveFlags.None);
        VideoDecoder dec = null;
        var frame = new OMTMediaFrame();
        var wire = new List<int>(); var shown = new List<int>(); int bad = 0;
        string codec = "";
        var gaps = new List<int>(); long lastShown = 0;
        var decMs = new List<int>(); var avMs = new List<int>(); var xferMs = new List<int>();
        var end = DateTime.UtcNow.AddSeconds(seconds);
        int Read(IntPtr p, int stride, int w, int h)
        {
            int x0 = strip.Length == 3 ? strip[0] : 0, x1 = strip.Length == 3 ? strip[1] : w;
            int y = strip.Length == 3 ? strip[2] : h - h / 24;
            byte* row = (byte*)p + y * stride;
            int bits = 0;
            for (int b = 0; b < 16; b++)
            {
                int x = (int)(x0 + (b + 0.5) * (x1 - x0) / 16);
                bits = (bits << 1) | (row[x * 2 + 1] > 128 ? 1 : 0); // UYVY: luma on odd bytes
            }
            return bits;
        }
        int ReadY(byte* row0, int stride, int w, int h)
        {
            int x0 = strip.Length == 3 ? strip[0] : 0, x1 = strip.Length == 3 ? strip[1] : w;
            int y = strip.Length == 3 ? strip[2] : h - h / 24;
            byte* row = row0 + (long)y * stride;
            int bits = 0;
            for (int b = 0; b < 16; b++) bits = (bits << 1) | (row[(int)(x0 + (b + 0.5) * (x1 - x0) / 16)] > 128 ? 1 : 0);
            return bits;
        }
        int Lat(int bits) => (int)(((long)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + offset) - bits) & 0xFFFF);
        void Add(List<int> l, int v) { if (v < 5000) l.Add(v); else bad++; }
        void Shown()
        {
            long now = Environment.TickCount64;
            if (lastShown != 0) gaps.Add((int)(now - lastShown));
            lastShown = now;
        }

        while (Program.Running && DateTime.UtcNow < end)
        {
            if (!recv.Receive(OMTFrameType.Video, 200, ref frame)) continue;
            if (frame.Type != OMTFrameType.Video) continue;
            if (frame.Codec == (int)OMTCodec.H264 || frame.Codec == (int)OMTCodec.HEVC)
            {
                codec = StreamStats.CodecName(frame.Codec);
                long arrived = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                if (dec == null)
                {
                    dec = new VideoDecoder(a.Has("--decoder") ? a.List("--decoder", "") : VideoDecoder.Defaults(frame.Codec == (int)OMTCodec.HEVC)); 
                    Console.Error.WriteLine("omtx probe: decoder " + dec.Name);
                }
                if (!dec.DecodeFrames(frame.Data, frame.DataLength, frame.Timestamp, f =>
                    {
                        decMs.Add((int)Math.Round(t0.Elapsed.TotalMilliseconds));
                        avMs.Add((int)Math.Round(dec.LastDecodeMs)); xferMs.Add((int)Math.Round(dec.LastTransferMs));
                        // luma straight from plane 0 (yuv420p and NV12 alike): no conversion in the measurement
                        int bits = ReadY(Av.FrameData(f)[0], Av.FrameLinesize(f)[0], Av.FrameWidth(f), Av.FrameHeight(f));
                        Add(shown, Lat(bits)); Shown();
                        Add(wire, (int)((((long)(arrived + offset)) - bits) & 0xFFFF));
                    }))
                    recv.RequestKeyframe();
            }
            else if (frame.Codec == (int)OMTCodec.UYVY || frame.Codec == (int)OMTCodec.UYVA)
            {
                codec = "VMX1";
                // libomtnet has already decoded VMX inside Receive: count that as decode, not wire
                Add(shown, Lat(Read(frame.Data, frame.Stride, frame.Width, frame.Height))); Shown();
            }
        }
        dec?.Dispose();
        Console.WriteLine($"{a.Positional[0]}  {codec}  clock offset {offset:F0} ms  bad {bad}");
        Print("wire   ", wire);
        Print("decoded", shown);
        if (decMs.Count > 0) { Print("decode ", decMs); Print("  libav", avMs); Print("  gpu->ram", xferMs); }
        if (gaps.Count > 0)
            Console.WriteLine($"  freezes >100 ms {gaps.Count(g => g > 100)}  frozen {gaps.Where(g => g > 100).Sum()} ms  longest {gaps.Max()} ms  dropped {recv.GetVideoStatistics().FramesDropped}");
        return shown.Count > 0 ? 0 : 1;
    }

    static void Print(string label, List<int> l)
    {
        if (l.Count == 0) return;
        l.Sort();
        int Q(double f) => l[(int)(f * (l.Count - 1))];
        Console.WriteLine($"  {label} n {l.Count}  min {l[0]}  p50 {Q(.5)}  p90 {Q(.9)}  p99 {Q(.99)}  max {l[^1]} ms");
    }

    /// <summary>Remote clock minus local, in ms, from an omtx ui /api/state (lowest of 10 round trips).</summary>
    static double ClockOffset(string url)
    {
        if (string.IsNullOrEmpty(url)) return 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        double bestRtt = double.MaxValue, off = 0;
        for (int i = 0; i < 10; i++)
        {
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string s = http.GetStringAsync(url.TrimEnd('/') + "/api/state").Result;
            long t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long now = JsonDocument.Parse(s).RootElement.GetProperty("now").GetInt64();
            if (t1 - t0 < bestRtt) { bestRtt = t1 - t0; off = now - (t0 + t1) / 2.0; }
        }
        return off;
    }
}
