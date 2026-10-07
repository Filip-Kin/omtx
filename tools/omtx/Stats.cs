using System.Diagnostics;
using System.Text.Json;

namespace Omtx;

/// <summary>
/// Live numbers for one stream (a bridge, a monitor). Written by the stream's thread, read by the
/// UI and the --stats printer. Rates are over the last two seconds; timings are smoothed.
/// </summary>
internal sealed class StreamStats
{
    private readonly object sync = new();
    private readonly Queue<(long t, int bytes)> window = new();
    private long windowBytes;

    public int Width, Height;
    public string Codec, FrameRate, Encoder, Decoder;
    public long Keyframes, Drops;
    public int Receivers;
    public long? TargetKbps;
    public double? MsReceive, MsConvert, MsEncode;
    public int AudioRate, AudioChannels;
    public volatile string State, Error; // per stream inside an "every source" bridge

    static long Now => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    public void Frame(int bytes, bool keyframe = false)
    {
        lock (sync)
        {
            long now = Now;
            window.Enqueue((now, bytes));
            windowBytes += bytes;
            if (keyframe) Keyframes++;
            Trim(now);
        }
    }

    public void Timings(double receive, double convert, double encode)
    {
        lock (sync)
        {
            MsReceive = Smooth(MsReceive, receive);
            MsConvert = Smooth(MsConvert, convert);
            MsEncode = Smooth(MsEncode, encode);
        }
    }

    /// <summary>Nothing flowing (bridge waiting for a viewer): drop the stale per-frame times.</summary>
    public void Idle() { lock (sync) { MsReceive = MsConvert = MsEncode = null; } }

    static double Smooth(double? old, double v) => old is double o ? o * 0.9 + v * 0.1 : v;

    private void Trim(long now)
    {
        while (window.Count > 0 && now - window.Peek().t > 2000) windowBytes -= window.Dequeue().bytes;
    }

    public (double fps, double mbps) Rates()
    {
        lock (sync)
        {
            long now = Now;
            Trim(now);
            if (window.Count == 0) return (0, 0);
            double span = Math.Max(1000, now - window.Peek().t) / 1000.0;
            return (window.Count / span, windowBytes * 8 / span / 1e6);
        }
    }

    public string Line()
    {
        var (fps, mbps) = Rates();
        string t = MsEncode is double e ? $"  ms/frame receive {MsReceive:F1} convert {MsConvert:F1} encode {e:F1}" : "";
        string target = TargetKbps is long k ? $"  target {k} kbps" : "";
        return $"{fps:F1} fps  {mbps:F2} Mbps  {Width}x{Height}  keyframes {Keyframes}  drops {Drops}  receivers {Receivers}{target}{t}";
    }

    public void Write(Utf8JsonWriter w)
    {
        var (fps, mbps) = Rates();
        lock (sync)
        {
            w.WriteStartObject();
            if (State != null) w.WriteString("state", State);
            if (Error != null) w.WriteString("error", Error);
            w.WriteNumber("fps", Math.Round(fps, 1));
            w.WriteNumber("mbps", Math.Round(mbps, 2));
            if (Width > 0) { w.WriteNumber("width", Width); w.WriteNumber("height", Height); }
            Str(w, "codec", Codec); Str(w, "frameRate", FrameRate);
            Str(w, "encoder", Encoder); Str(w, "decoder", Decoder);
            w.WriteNumber("keyframes", Keyframes);
            w.WriteNumber("drops", Drops);
            w.WriteNumber("receivers", Receivers);
            if (TargetKbps is long k) w.WriteNumber("targetKbps", k);
            if (MsReceive is double a) w.WriteNumber("msReceive", Math.Round(a, 1));
            if (MsConvert is double b) w.WriteNumber("msConvert", Math.Round(b, 1));
            if (MsEncode is double c) w.WriteNumber("msEncode", Math.Round(c, 1));
            if (AudioRate > 0)
            {
                w.WriteStartObject("audio");
                w.WriteNumber("rate", AudioRate); w.WriteNumber("channels", AudioChannels);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
    }

    static void Str(Utf8JsonWriter w, string k, string v) { if (v != null) w.WriteString(k, v); }

    public static string CodecName(int codec) => codec switch
    {
        (int)libomtnet.OMTCodec.H264 => "H264",
        (int)libomtnet.OMTCodec.HEVC => "HEVC",
        (int)libomtnet.OMTCodec.VMX1 => "VMX1",
        _ => null,
    };
}
