using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using libomtnet;

namespace Omtx;

/// <summary>
/// omtx ui: a local web page (embedded, built from tools/ui-web) with every OMT/omtx source, live
/// previews and stats, and out/in bridges started from the page. Plain HTTP/1.1 on a TcpListener:
/// no web framework, works under NativeAOT. Listens on 127.0.0.1 unless --listen says otherwise,
/// because the page can start and stop bridges.
/// </summary>
internal static class UiCmd
{
    static readonly object bridgesLock = new();
    static readonly List<Bridge> bridges = new();
    static int nextBridge = 1;
    static readonly object monitorsLock = new();
    static readonly Dictionary<(string, bool), SourceMonitor> monitors = new();
    static string decoderList;
    // Watch pages streaming omtx frames straight to the browser (WebCodecs): stats per source
    static readonly object relaysLock = new();
    static readonly List<(string source, StreamStats stats)> relays = new();
    static OMTDiscovery discovery;

    public static int Run(Args a)
    {
        Av.Load(a.Get("--ffmpeg"));
        decoderList = a.Get("--decoder");
        discovery = OMTDiscovery.GetInstance();
        string listen = a.Get("--listen", "127.0.0.1");
        int port = a.Int("--port", 6390);
        var listener = new TcpListener(IPAddress.Parse(listen), port);
        listener.Start();
        string url = $"http://{(listen == "0.0.0.0" ? "localhost" : listen)}:{port}/";
        Console.Error.WriteLine($"omtx ui: {url}" + (listen == "0.0.0.0" ? $" (also on this PC's addresses, port {port})" : ""));
        if (!a.Has("--no-open")) OpenBrowser(url);

        new Thread(Janitor) { IsBackground = true, Name = "monitor janitor" }.Start();
        while (Program.Running)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { break; }
            new Thread(() => Serve(c)) { IsBackground = true }.Start();
        }
        lock (bridgesLock) foreach (var b in bridges) b.Stop();
        return 0;
    }

    static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch { /* headless: the URL is printed */ }
    }

    // Monitors nobody asked for in 15 s are stopped (thumbnails re-request every few seconds).
    static void Janitor()
    {
        while (Program.Running)
        {
            Thread.Sleep(2000);
            List<SourceMonitor> dead = new();
            lock (monitorsLock)
            {
                foreach (var kv in monitors.ToList())
                    if (kv.Value.Idle(15000) || !kv.Value.Alive) { dead.Add(kv.Value); monitors.Remove(kv.Key); }
            }
            foreach (var m in dead) m.Stop();
        }
    }

    static SourceMonitor Monitor(string name, bool omtx, bool full)
    {
        lock (monitorsLock)
        {
            if (!monitors.TryGetValue((name, full), out var m) || !m.Alive)
            {
                m = new SourceMonitor(name, omtx, full);
                monitors[(name, full)] = m;
                m.Start();
            }
            m.Touch();
            return m;
        }
    }

    // ---- sources ----

    static string IdOf(OMTAddress s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ServiceType + "|" + s.ToString())))[..10].ToLowerInvariant();

    static OMTAddress[] Sources() => discovery.GetSources()
        .GroupBy(s => IdOf(s)).Select(g => g.First())
        .OrderBy(s => s.ServiceType).ThenBy(s => s.ToString()).ToArray();

    static OMTAddress FindSource(string id) => Sources().FirstOrDefault(s => IdOf(s) == id);

    static string Label(OMTAddress s)
    {
        string n = s.ToString();
        int open = n.IndexOf('('), close = n.LastIndexOf(')');
        return open >= 0 && close > open ? n.Substring(open + 1, close - open - 1) : n;
    }

    // ---- HTTP ----

    sealed class Request
    {
        public string Method, Path, Query = "";
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();
        public string Q(string key)
        {
            foreach (var part in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv[0] == key) return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            }
            return null;
        }
    }

    static void Serve(TcpClient c)
    {
        using (c)
        {
            c.NoDelay = true;
            var s = c.GetStream();
            try
            {
                var req = ReadRequest(s);
                if (req == null) return;
                Route(req, s);
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                try { Send(s, 500, "application/json", Json(w => w.WriteString("error", ex.Message))); } catch { }
            }
        }
    }

    static Request ReadRequest(NetworkStream s)
    {
        var head = new MemoryStream();
        uint last4 = 0;
        int b;
        while ((b = s.ReadByte()) >= 0)
        {
            head.WriteByte((byte)b);
            last4 = (last4 << 8) | (uint)b;
            if (last4 == 0x0D0A0D0A) break;          // \r\n\r\n ends the headers
            if (head.Length > 32768) return null;
        }
        if (b < 0) return null;
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var req = new Request { Method = first[0] };
        string target = first[1];
        int q = target.IndexOf('?');
        req.Path = Uri.UnescapeDataString(q >= 0 ? target[..q] : target);
        if (q >= 0) req.Query = target[(q + 1)..];
        foreach (var l in lines.Skip(1))
        {
            int i = l.IndexOf(':');
            if (i > 0) req.Headers[l[..i].Trim()] = l[(i + 1)..].Trim();
        }
        if (req.Headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out int n) && n > 0 && n < 1 << 20)
        {
            req.Body = new byte[n];
            int read = 0;
            while (read < n) { int r = s.Read(req.Body, read, n - read); if (r <= 0) break; read += r; }
        }
        return req;
    }

    static void Send(Stream s, int status, string type, byte[] body, string extra = "")
    {
        string reason = status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", _ => "Error" };
        var h = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n{extra}\r\n");
        s.Write(h); s.Write(body); s.Flush();
    }

    static byte[] Json(Action<Utf8JsonWriter> body)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) { w.WriteStartObject(); body(w); w.WriteEndObject(); }
        return ms.ToArray();
    }

    static void Route(Request req, NetworkStream s)
    {
        string p = req.Path;
        if (req.Method is "POST" or "DELETE")
        {
            // Writes only from this page: a cross-site form or script cannot start bridges.
            if (req.Headers.TryGetValue("Origin", out var origin) && req.Headers.TryGetValue("Host", out var host)
                && !string.Equals(new Uri(origin).Authority, host, StringComparison.OrdinalIgnoreCase))
            { Send(s, 403, "application/json", Json(w => w.WriteString("error", "Origin"))); return; }
        }

        if (req.Method == "GET" && p == "/api/events") { Events(s); return; }
        if (req.Method == "GET" && p == "/api/state") { Send(s, 200, "application/json", State()); return; }
        if (req.Method == "GET" && p.StartsWith("/api/preview/") && p.EndsWith(".mjpg")) { Preview(req, s, p[13..^5]); return; }
        if (req.Method == "GET" && p.StartsWith("/api/snapshot/") && p.EndsWith(".jpg")) { Snapshot(req, s, p[14..^4]); return; }
        if (req.Method == "GET" && p.StartsWith("/api/stream/")) { Stream(s, p[12..]); return; }
        if (req.Method == "POST" && p == "/api/bridges") { CreateBridge(req, s); return; }
        if (req.Method == "DELETE" && p.StartsWith("/api/bridges/")) { DeleteBridge(s, p[13..]); return; }
        if (req.Method == "GET") { Static(s, p); return; }
        Send(s, 404, "application/json", Json(w => w.WriteString("error", "Not found")));
    }

    // ---- static page (embedded tools/ui-web/dist) ----

    static readonly Assembly asm = typeof(UiCmd).Assembly;

    static void Static(Stream s, string path)
    {
        string rel = path.TrimStart('/');
        if (rel == "") rel = "index.html";
        string res = "ui/" + rel.Replace('\\', '/');
        using var st = asm.GetManifestResourceStream(res) ?? (rel.Contains('.') ? null : asm.GetManifestResourceStream("ui/index.html"));
        if (st == null)
        {
            if (rel == "index.html")
            { Send(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes("<!doctype html><title>omtx</title><p>omtx ui</p>")); return; }
            Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
            return;
        }
        var ms = new MemoryStream(); st.CopyTo(ms);
        string ext = Path.GetExtension(rel).ToLowerInvariant();
        string type = ext switch
        {
            ".html" or "" => "text/html; charset=utf-8", ".js" or ".mjs" => "text/javascript", ".css" => "text/css",
            ".svg" => "image/svg+xml", ".png" => "image/png", ".ico" => "image/x-icon", ".json" => "application/json",
            ".woff2" => "font/woff2", _ => "application/octet-stream",
        };
        Send(s, 200, type, ms.ToArray());
    }

    // ---- state ----

    static byte[] State() => Json(w =>
    {
        w.WriteString("host", OMTAddress.SanitizeName(Environment.MachineName));
        w.WriteString("version", Program.Version);
        w.WriteNumber("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); // lets a client measure clock offset
        string me = OMTAddress.SanitizeName(Environment.MachineName);
        w.WriteStartArray("sources");
        foreach (var src in Sources())
        {
            w.WriteStartObject();
            w.WriteString("id", IdOf(src));
            w.WriteString("name", src.ToString());
            w.WriteString("machine", src.MachineName);
            w.WriteString("label", Label(src));
            w.WriteString("type", src.ServiceType == OMTAddress.SERVICE_TYPE_OMTX ? "omtx" : "omt");
            w.WriteStartArray("addresses");
            foreach (var ip in src.Addresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork || x.IsIPv4MappedToIPv6)
                                 .Select(x => x.IsIPv4MappedToIPv6 ? x.MapToIPv4() : x).Distinct())
                w.WriteStringValue(ip.ToString());
            w.WriteEndArray();
            w.WriteNumber("port", src.Port);
            w.WriteBoolean("local", string.Equals(src.MachineName, me, StringComparison.OrdinalIgnoreCase));
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("bridges");
        lock (bridgesLock)
        {
            foreach (var b in bridges)
            {
                var streams = b.Streams().ToList();
                w.WriteStartObject();
                w.WriteString("id", b.Id);
                w.WriteString("kind", b.Kind);
                w.WriteString("source", b.Source);
                w.WriteStartArray("publishedAs");
                foreach (var st in streams) w.WriteStringValue(st.publishedAs);
                w.WriteEndArray();
                w.WriteString("state", b.State);
                if (b.Error != null) w.WriteString("error", b.Error); else w.WriteNull("error");
                if (b is InAllBridge)
                {
                    w.WriteNull("stats");
                }
                else
                {
                    w.WritePropertyName("stats");
                    streams[0].stats.Write(w);
                }
                w.WriteStartArray("streams");
                foreach (var st in streams)
                {
                    w.WriteStartObject();
                    w.WriteString("source", st.source);
                    w.WriteString("publishedAs", st.publishedAs);
                    w.WritePropertyName("stats"); st.stats.Write(w);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();

        w.WriteStartArray("monitors");
        lock (relaysLock)
        {
            foreach (var (name, st) in relays)
            {
                var src = Sources().FirstOrDefault(x => x.ToString() == name);
                if (src == null) continue;
                w.WriteStartObject();
                w.WriteString("sourceId", IdOf(src));
                w.WriteBoolean("full", true);
                w.WritePropertyName("stats"); st.Write(w);
                w.WriteEndObject();
            }
        }
        lock (monitorsLock)
        {
            foreach (var kv in monitors.Where(kv => kv.Value.Alive))
            {
                var src = Sources().FirstOrDefault(x => x.ToString() == kv.Key.Item1);
                if (src == null) continue;
                w.WriteStartObject();
                w.WriteString("sourceId", IdOf(src));
                w.WriteBoolean("full", kv.Key.Item2);
                w.WritePropertyName("stats"); kv.Value.Stats.Write(w);
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
    });

    static void Events(NetworkStream s)
    {
        var h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: keep-alive\r\nX-Accel-Buffering: no\r\n\r\n");
        s.Write(h);
        s.Write(Encoding.ASCII.GetBytes("retry: 2000\n\n"));
        s.Flush();
        while (Program.Running)
        {
            var body = State();
            s.Write(Encoding.ASCII.GetBytes("event: state\ndata: "));
            s.Write(body);
            s.Write(Encoding.ASCII.GetBytes("\n\n"));
            s.Flush();
            Thread.Sleep(1000);
        }
    }

    // ---- pictures ----

    static void Snapshot(Request req, Stream s, string id)
    {
        var src = FindSource(id);
        if (src == null) { Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return; }
        bool omtx = src.ServiceType == OMTAddress.SERVICE_TYPE_OMTX;
        // a Watch page already receiving in full can serve the thumbnail too
        SourceMonitor m;
        lock (monitorsLock) monitors.TryGetValue((src.ToString(), true), out m);
        if (m == null || !m.Alive) m = Monitor(src.ToString(), omtx, false); else m.Touch();
        int w = int.TryParse(req.Q("w"), out int ww) ? ww : 320;
        var jpeg = m.Jpeg(w, out _);
        if (jpeg == null) { Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("No picture yet")); return; }
        Send(s, 200, "image/jpeg", jpeg);
    }

    static void Preview(Request req, NetworkStream s, string id)
    {
        var src = FindSource(id);
        if (src == null) { Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return; }
        bool omtx = src.ServiceType == OMTAddress.SERVICE_TYPE_OMTX;
        int width = int.TryParse(req.Q("w"), out int ww) ? ww : 960;
        var m = Monitor(src.ToString(), omtx, true);
        const string boundary = "omtxframe";
        s.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary={boundary}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
        long last = -1;
        var lastNew = Stopwatch.StartNew();
        while (Program.Running)
        {
            // source gone or silent: end the stream so the page shows No signal and reconnects
            if (lastNew.ElapsedMilliseconds > 10000) return;
            m.Touch();
            var jpeg = m.Jpeg(width, out long seq);
            if (jpeg != null && seq != last)
            {
                last = seq;
                lastNew.Restart();
                s.Write(Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n"));
                s.Write(jpeg);
                s.Write(Encoding.ASCII.GetBytes("\r\n"));
                s.Flush();
            }
            if (!m.Alive) m = Monitor(src.ToString(), omtx, true);
            Thread.Sleep(5); // as fast as pictures arrive (each JPEG is sent once)
        }
    }

    /// <summary>
    /// The omtx source's H.264/HEVC access units, untouched, for the browser to decode (WebCodecs)
    /// at the source's full frame rate. Body until close; each frame is
    /// [u32 length][u8 codec 1=H264 2=HEVC][u8 flags bit0=keyframe][u16 0][i32 width][i32 height]
    /// [i32 fpsN][i32 fpsD][i64 timestamp 100ns][access unit], all little-endian, length = 28 + AU.
    /// </summary>
    static void Stream(NetworkStream s, string id)
    {
        var src = FindSource(id);
        if (src == null || src.ServiceType != OMTAddress.SERVICE_TYPE_OMTX)
        { Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return; }
        s.Write(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
        s.Flush();
        var stats = new StreamStats();
        var entry = (src.ToString(), stats);
        lock (relaysLock) relays.Add(entry);
        try
        {
            using var recv = new OMTReceive(Program.NormaliseAddress(src.ToString()), OMTFrameType.Video | OMTFrameType.Audio, OMTPreferredVideoFormat.UYVY, OMTReceiveFlags.None);
            var frame = new OMTMediaFrame();
            byte[] buf = new byte[1 << 20];
            var silent = Stopwatch.StartNew();
            while (Program.Running)
            {
                if (!recv.Receive(OMTFrameType.Video | OMTFrameType.Audio, 200, ref frame))
                {
                    if (silent.ElapsedMilliseconds > 10000) return; // source gone: the page reconnects
                    continue;
                }
                if (frame.Type == OMTFrameType.Audio) { stats.AudioRate = frame.SampleRate; stats.AudioChannels = frame.Channels; continue; }
                if (frame.Type != OMTFrameType.Video) continue;
                if (frame.Codec != (int)OMTCodec.H264 && frame.Codec != (int)OMTCodec.HEVC) continue;
                silent.Restart();
                bool key = frame.Flags.HasFlag(OMTVideoFlags.Keyframe);
                stats.Codec = StreamStats.CodecName(frame.Codec);
                stats.Width = frame.Width; stats.Height = frame.Height;
                stats.FrameRate = $"{frame.FrameRateN}/{frame.FrameRateD}";
                stats.Drops = recv.GetVideoStatistics().FramesDropped;
                stats.Frame(frame.DataLength, key);
                int n = 32 + frame.DataLength;
                if (buf.Length < n) buf = new byte[n * 2];
                var span = buf.AsSpan();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, 28 + frame.DataLength);
                span[4] = (byte)(frame.Codec == (int)OMTCodec.HEVC ? 2 : 1);
                span[5] = (byte)(key ? 1 : 0);
                span[6] = 0; span[7] = 0;
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[8..], frame.Width);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[12..], frame.Height);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[16..], frame.FrameRateN);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[20..], frame.FrameRateD);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(span[24..], frame.Timestamp);
                System.Runtime.InteropServices.Marshal.Copy(frame.Data, buf, 32, frame.DataLength);
                s.Write(buf, 0, n);
                s.Flush();
            }
        }
        finally
        {
            lock (relaysLock) relays.Remove(entry);
        }
    }

    // ---- bridges ----

    static void CreateBridge(Request req, Stream s)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(req.Body); }
        catch { Send(s, 400, "application/json", Json(w => w.WriteString("error", "JSON"))); return; }
        var root = doc.RootElement;
        string kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
        string source = root.TryGetProperty("source", out var so) ? so.GetString() : null;
        // sourceId (preferred by the page) wins over a name: two machines can announce the same name
        if (root.TryGetProperty("sourceId", out var sid) && sid.GetString() is string idv && FindSource(idv) is OMTAddress found)
            source = found.ToString();
        if (string.IsNullOrEmpty(source) || (kind != "out" && kind != "in"))
        { Send(s, 400, "application/json", Json(w => w.WriteString("error", "Kind and source"))); return; }

        Bridge b;
        lock (bridgesLock)
        {
            if (bridges.Any(x => x.Kind == kind && x.Source == source && x.State != "error" && x.State != "stopped"))
            { Send(s, 409, "application/json", Json(w => w.WriteString("error", "Already running"))); return; }
            if (kind == "out")
            {
                var o = new OutOptions { Source = source };
                if (root.TryGetProperty("bitrateKbps", out var br) && br.TryGetInt32(out int kbps) && kbps >= 500) o.CeilingBps = kbps * 1000L;
                o.FloorBps = Math.Min(o.FloorBps, o.CeilingBps);
                if (root.TryGetProperty("codec", out var cd) && cd.GetString() is "hevc" or "h265") o.Hevc = true;
                b = new OutBridge(o);
            }
            else
            {
                b = source == "*" ? new InAllBridge(decoderList) : new InBridge(source, decoderList);
            }
            b.Id = "b" + nextBridge++;
            bridges.Add(b);
        }
        b.Start();
        string id = b.Id;
        Send(s, 200, "application/json", Json(w => w.WriteString("id", id)));
    }

    static void DeleteBridge(Stream s, string id)
    {
        Bridge b;
        lock (bridgesLock)
        {
            b = bridges.FirstOrDefault(x => x.Id == id);
            if (b != null) bridges.Remove(b);
        }
        if (b == null) { Send(s, 404, "application/json", Json(w => w.WriteString("error", "Not found"))); return; }
        b.Stop();
        Send(s, 204, "application/json", Array.Empty<byte>());
    }
}
