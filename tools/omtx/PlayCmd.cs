using System.Diagnostics;
using System.Runtime.InteropServices;
using libomtnet;

namespace Omtx;

/// <summary>
/// omtx play: receive an omtx source and feed it to ffplay as live Matroska on stdin.
/// Same arguments as ndi-play / omt-play so the projector daemon can swap players.
/// </summary>
internal static class PlayCmd
{
    public static int Run(Args a)
    {
        if (a.Positional.Count < 1) return Program.Fail("omtx play: source missing");
        string address = Program.NormaliseAddress(a.Positional[0]);
        OMTQuality quality = (a.Positional.Count > 1 ? a.Positional[1] : "high").ToLowerInvariant() switch
        {
            "low" => OMTQuality.Low,
            "medium" => OMTQuality.Medium,
            _ => OMTQuality.High,
        };
        // Video only by default: the lowest latency, see VideoPts below. --audio plays sound with
        // ffplay's audio clock as master, which keeps whatever delay builds up at startup.
        bool noAudio = !a.Has("--audio");
        bool stats = a.Has("--stats");
        string window = a.Get("--window");
        string ffplay = a.Get("--ffplay", "ffplay");

        // Video only: decode here and draw with SDL2 the moment a picture is ready. ffplay stays
        // for --audio, for --ffplay, and for machines without SDL2 or libavcodec.
        if (noAudio && !a.Has("--ffplay") && SdlWindow.Load())
        {
            bool av = true;
            try { Av.Load(a.Get("--ffmpeg")); } catch (Exception ex) { av = false; Console.Error.WriteLine("omtx play: " + ex.Message + "; using ffplay"); }
            if (av) return RunSdl(a, address, quality, window, stats);
        }

        var types = noAudio ? OMTFrameType.Video : OMTFrameType.Video | OMTFrameType.Audio;
        using var recv = new OMTReceive(address, types, OMTPreferredVideoFormat.UYVY, OMTReceiveFlags.None);
        recv.SetSuggestedQuality(quality);

        Process player = null;
        MkvWriter mkv = null;
        bool hevc = false;
        int width = 0, height = 0;
        int audioRate = 0, audioChannels = 0;     // format declared to the running player
        int seenRate = 0, seenChannels = 0;       // latest format seen on the wire
        long t0 = 0;
        var arrival = new Stopwatch();
        byte[] video = new byte[1 << 20];
        float[] planar = new float[0];
        byte[] interleaved = new byte[0];
        var frame = new OMTMediaFrame();
        var lastKeyRequest = DateTime.MinValue;
        var statTimer = Stopwatch.StartNew();
        long statFrames = 0, statBytes = 0;
        bool warnedStock = false;

        void Stop()
        {
            if (player == null) return;
            try { player.StandardInput.Close(); } catch { }
            if (!player.WaitForExit(2000)) { try { player.Kill(); } catch { } }
            player.Dispose();
            player = null;
            mkv = null;
        }

        try
        {
            while (Program.Running)
            {
                if (player != null && player.HasExited)
                {
                    int code = player.ExitCode;
                    player.Dispose(); player = null;
                    return code == 0 ? 0 : 3;
                }
                if (!recv.Receive(types, 200, ref frame)) continue;

                if (frame.Type == OMTFrameType.Audio)
                {
                    seenRate = frame.SampleRate; seenChannels = frame.Channels;
                    if (mkv == null || audioRate == 0) continue;
                    if (frame.SampleRate != audioRate || frame.Channels != audioChannels)
                    {
                        Stop(); recv.RequestKeyframe(); continue; // restart with the new audio format on the next keyframe
                    }
                    int samples = frame.SamplesPerChannel, ch = frame.Channels, n = samples * ch;
                    if (planar.Length < n) { planar = new float[n]; interleaved = new byte[n * 4]; }
                    Marshal.Copy(frame.Data, planar, 0, Math.Min(n, frame.DataLength / 4));
                    var dst = MemoryMarshal.Cast<byte, float>(interleaved.AsSpan(0, n * 4));
                    for (int s = 0; s < samples; s++)
                        for (int c = 0; c < ch; c++)
                            dst[s * ch + c] = planar[c * samples + s];
                    Write(() => mkv.WriteAudio((frame.Timestamp - t0) / 10000, interleaved, n * 4));
                    continue;
                }

                if (frame.Type != OMTFrameType.Video) continue;
                bool inter = frame.Codec == (int)OMTCodec.H264 || frame.Codec == (int)OMTCodec.HEVC;
                if (!inter)
                {
                    if (!warnedStock) { Console.Error.WriteLine("omtx play: stock OMT (VMX) plays without --audio and --ffplay only"); warnedStock = true; }
                    continue;
                }
                bool key = frame.Flags.HasFlag(OMTVideoFlags.Keyframe);
                bool thisHevc = frame.Codec == (int)OMTCodec.HEVC;

                // Video format change: restart the player on the next keyframe
                if (player != null && (thisHevc != hevc || frame.Width != width || frame.Height != height)) Stop();
                // Audio appeared after a video-only start: restart once so the player gets an audio track
                if (player != null && audioRate == 0 && seenRate > 0 && !noAudio) Stop();

                if (player == null)
                {
                    if (!key)
                    {
                        if (DateTime.UtcNow - lastKeyRequest > TimeSpan.FromSeconds(1)) { recv.RequestKeyframe(); lastKeyRequest = DateTime.UtcNow; }
                        continue;
                    }
                    hevc = thisHevc; width = frame.Width; height = frame.Height;
                    audioRate = noAudio ? 0 : seenRate; audioChannels = noAudio ? 0 : seenChannels;
                    player = StartPlayer(ffplay, window, audioRate > 0, $"omtx {a.Positional[0]}");
                    mkv = new MkvWriter(player.StandardInput.BaseStream, hevc, width, height, audioRate, audioChannels);
                    t0 = frame.Timestamp;
                    arrival.Restart();
                }

                int len = frame.DataLength;
                if (video.Length < len) video = new byte[len * 2];
                Marshal.Copy(frame.Data, video, 0, len);
                long ms = noAudio ? VideoPts(arrival) : (frame.Timestamp - t0) / 10000;
                Write(() => mkv.WriteVideo(ms, video, len, key));
                statFrames++; statBytes += len;

                if (stats && statTimer.ElapsedMilliseconds >= 5000)
                {
                    double sec = statTimer.Elapsed.TotalSeconds;
                    var st = recv.GetVideoStatistics();
                    Console.Error.WriteLine($"[play] {statFrames / sec:F1} fps  {statBytes * 8 / sec / 1e6:F2} Mbps  dropped {st.FramesDropped}");
                    statTimer.Restart(); statFrames = 0; statBytes = 0;
                }
            }
        }
        finally
        {
            Stop();
        }
        return 0;

        void Write(Action w)
        {
            try { w(); }
            catch (IOException) { /* player went away; the loop picks up its exit code */ }
        }
    }

    /// <summary>
    /// Video-only timestamps: arrival time, running 10% fast. ffplay (video clock master) shows a
    /// frame once its timestamp is due, so frames that queue up while ffplay starts, or that arrive
    /// in a burst after a Wi-Fi stall, are already due and go out at once; steady frames are shown
    /// on arrival. With the sender's own timestamps that queue would play at normal speed for ever
    /// and every stall would add to the delay for good.
    /// </summary>
    static long VideoPts(Stopwatch arrival) => (long)(arrival.Elapsed.TotalMilliseconds * 0.9);

    static unsafe int RunSdl(Args a, string address, OMTQuality quality, string window, bool stats)
    {
        using var recv = new OMTReceive(address, OMTFrameType.Video, OMTPreferredVideoFormat.UYVY, OMTReceiveFlags.None);
        recv.SetSuggestedQuality(quality);
        using var win = new SdlWindow($"omtx {a.Positional[0]}", window, !a.Has("--no-vsync"));
        int i420 = Av.PixFmt("yuv420p"), j420 = Av.PixFmt("yuvj420p");
        VideoDecoder dec = null;
        int decCodec = 0;
        bool waitKey = true;
        IntPtr sws = IntPtr.Zero; int swsFmt = -1, swsW = 0, swsH = 0;
        IntPtr conv = IntPtr.Zero;
        var frame = new OMTMediaFrame();
        var statTimer = Stopwatch.StartNew();
        long statFrames = 0, statBytes = 0;
        double decMs = 0, showMs = 0;
        var t = new Stopwatch();
        bool warnedStock = false;
        bool pending = false; // a picture is in the texture and not yet presented
        try
        {
            while (Program.Running && win.Pump())
            {
                // Block only when nothing waits to be shown. Frames that queued up (after a Wi-Fi
                // stall, or while vsync held the last present) are all decoded but only the newest
                // is presented, so a burst costs one refresh instead of one refresh per frame.
                if (!recv.Receive(OMTFrameType.Video, pending ? 0 : 20, ref frame) || frame.Type != OMTFrameType.Video)
                {
                    if (pending) { t.Restart(); win.Present(); showMs += t.Elapsed.TotalMilliseconds; pending = false; }
                    continue;
                }
                if (frame.Codec == (int)OMTCodec.UYVY || frame.Codec == (int)OMTCodec.UYVA)
                {
                    // Stock OMT: libomtnet has already decoded VMX inside Receive
                    if (!warnedStock) { Console.Error.WriteLine($"omtx play: OMT (VMX) {frame.Width}x{frame.Height}"); warnedStock = true; }
                    statFrames++; statBytes += frame.CompressedLength > 0 ? frame.CompressedLength : frame.DataLength;
                    t.Restart();
                    win.UploadUyvy(frame.Data, frame.Stride, frame.Width, frame.Height);
                    pending = true;
                    decMs += t.Elapsed.TotalMilliseconds;
                    continue;
                }
                if (frame.Codec != (int)OMTCodec.H264 && frame.Codec != (int)OMTCodec.HEVC) continue;
                bool key = frame.Flags.HasFlag(OMTVideoFlags.Keyframe);
                if (dec == null || decCodec != frame.Codec)
                {
                    dec?.Dispose();
                    dec = new VideoDecoder(a.Has("--decoder") ? a.List("--decoder", "") : VideoDecoder.Defaults(frame.Codec == (int)OMTCodec.HEVC));
                    decCodec = frame.Codec; waitKey = true;
                    Console.Error.WriteLine($"omtx play: {StreamStats.CodecName(frame.Codec)} {frame.Width}x{frame.Height} decoder {dec.Name}");
                }
                if (waitKey && !key) { recv.RequestKeyframe(); continue; }
                waitKey = false;
                statFrames++; statBytes += frame.DataLength;
                t.Restart();
                bool ok = dec.DecodeFrames(frame.Data, frame.DataLength, frame.Timestamp, f =>
                {
                    decMs += t.Elapsed.TotalMilliseconds; t.Restart();
                    int w = Av.FrameWidth(f), h = Av.FrameHeight(f), fmt = Av.FrameFormat(f);
                    byte** d = Av.FrameData(f); int* l = Av.FrameLinesize(f);
                    if (fmt != i420 && fmt != j420)
                    {
                        // e.g. 10-bit or 4:2:2 from a camera: convert to I420 for the texture
                        if (sws == IntPtr.Zero || fmt != swsFmt || w != swsW || h != swsH)
                        {
                            Av.SwsFree(sws); if (conv != IntPtr.Zero) Av.FrameFree(ref conv);
                            sws = Av.SwsGet(w, h, fmt, w, h, i420); swsFmt = fmt; swsW = w; swsH = h;
                            conv = Av.FrameAlloc();
                            Av.FrameWidth(conv) = w; Av.FrameHeight(conv) = h; Av.FrameFormat(conv) = i420;
                            Av.FrameGetBuffer(conv);
                        }
                        Av.SwsScale(sws, d, l, h, Av.FrameData(conv), Av.FrameLinesize(conv));
                        d = Av.FrameData(conv); l = Av.FrameLinesize(conv);
                    }
                    win.Upload(d[0], l[0], d[1], l[1], d[2], l[2], w, h);
                    pending = true;
                });
                if (!ok) { recv.RequestKeyframe(); waitKey = true; }

                if (stats && statTimer.ElapsedMilliseconds >= 5000)
                {
                    double sec = statTimer.Elapsed.TotalSeconds;
                    var st = recv.GetVideoStatistics();
                    Console.Error.WriteLine($"[play] {statFrames / sec:F1} fps  {statBytes * 8 / sec / 1e6:F2} Mbps  dropped {st.FramesDropped}  decode {decMs / Math.Max(1, statFrames):F1} ms  draw {showMs / Math.Max(1, statFrames):F1} ms");
                    statTimer.Restart(); statFrames = 0; statBytes = 0; decMs = showMs = 0;
                }
            }
        }
        finally
        {
            dec?.Dispose();
            Av.SwsFree(sws);
            if (conv != IntPtr.Zero) Av.FrameFree(ref conv);
        }
        return 0;
    }

    static Process StartPlayer(string ffplay, string window, bool audio, string title)
    {
        var args = new List<string> {
            // No "-fflags nobuffer": it discards the packets read while probing, and the first one is
            // the only keyframe (omtx sends keyframes on request), so the picture would stay black.
            "-hide_banner", "-loglevel", "error", "-nostats",
            "-flags", "low_delay", "-framedrop",
            "-probesize", "32", "-analyzeduration", "0",
            "-sync", audio ? "audio" : "video",
            "-window_title", title,
        };
        if (!audio) args.Add("-an");
        if (window != null && TryGeom(window, out int w, out int h, out int x, out int y))
        {
            args.AddRange(new[] { "-x", w.ToString(), "-y", h.ToString(), "-left", x.ToString(), "-top", y.ToString(), "-noborder", "-alwaysontop" });
        }
        else
        {
            args.Add("-fs");
        }
        args.AddRange(new[] { "-f", "matroska", "-i", "pipe:0" });

        var psi = new ProcessStartInfo(ffplay) { UseShellExecute = false, RedirectStandardInput = true };
        foreach (var s in args) psi.ArgumentList.Add(s);
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start " + ffplay);
    }

    internal static bool TryGeom(string s, out int w, out int h, out int x, out int y)
    {
        w = h = x = y = 0;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+)x(\d+)\+(-?\d+)\+(-?\d+)$");
        if (!m.Success) return false;
        w = int.Parse(m.Groups[1].Value); h = int.Parse(m.Groups[2].Value);
        x = int.Parse(m.Groups[3].Value); y = int.Parse(m.Groups[4].Value);
        return w > 0 && h > 0;
    }
}
