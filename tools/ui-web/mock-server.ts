// Development mock of the omtx ui backend. Serves dist/ and the /api endpoints with fake data.
//
//   bun mock-server.ts [--port 5180] [--sources 4]
//
// Fake video comes from ffmpeg lavfi test sources rendered once at startup into JPEG frames.
// Dev-only controls (used by screenshots.ts):
//   POST /mock/scenario {"sources": n, "bridges": "none"|"running"|"error"|"mixed", "offline": bool}
import { spawnSync } from "bun";
import { join } from "path";

const args = process.argv.slice(2);
const arg = (k: string, d: string) => {
  const i = args.indexOf(k);
  return i >= 0 && args[i + 1] ? args[i + 1] : d;
};
const PORT = Number(arg("--port", process.env.PORT ?? "5180"));
const DIST = join(import.meta.dir, "dist");

// ---------- fake frames ----------

const PATTERNS = [
  "testsrc2=size=WxH:rate=10",
  "smptehdbars=size=WxH:rate=10",
  "mandelbrot=size=WxH:rate=10",
  "life=size=WxH:rate=10:mold=10:ratio=0.2:death_color=#101820:life_color=#4fd1a5",
  "rgbtestsrc=size=WxH:rate=10",
  "cellauto=size=WxH:rate=10:rule=110",
];

function splitJpegs(buf: Uint8Array): Uint8Array[] {
  const out: Uint8Array[] = [];
  let start = -1;
  for (let i = 0; i < buf.length - 1; i++) {
    if (buf[i] === 0xff && buf[i + 1] === 0xd8 && start < 0) start = i;
    else if (buf[i] === 0xff && buf[i + 1] === 0xd9 && start >= 0) {
      out.push(buf.slice(start, i + 2));
      start = -1;
      i++;
    }
  }
  return out;
}

const frameCache = new Map<string, Uint8Array[]>();
function frames(pattern: number, w: number): Uint8Array[] {
  w = Math.max(160, Math.min(1920, Math.round(w / 16) * 16));
  const h = Math.round((w * 9) / 16 / 2) * 2;
  const key = `${pattern}:${w}`;
  const hit = frameCache.get(key);
  if (hit) return hit;
  const src = PATTERNS[pattern % PATTERNS.length].replace("WxH", `${w}x${h}`);
  const r = spawnSync([
    "ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", src,
    "-frames:v", "30", "-q:v", "6", "-f", "image2pipe", "-vcodec", "mjpeg", "-",
  ]);
  const list = r.success ? splitJpegs(r.stdout) : [];
  if (!list.length) console.error("ffmpeg frame render failed:", r.stderr?.toString());
  frameCache.set(key, list);
  return list;
}

// ---------- fake state ----------

type Source = {
  id: string; name: string; machine: string; label: string; type: "omt" | "omtx";
  addresses: string[]; port: number; local: boolean;
};
type Bridge = {
  id: string; kind: "out" | "in"; source: string; publishedAs: string[];
  state: "starting" | "waiting" | "running" | "error"; error: string | null;
  stats: any; streams: { source: string; publishedAs: string; stats: any }[];
  bitrateKbps?: number; codec?: string; started: number;
};

const HOST = "FILIPS-LAPTOP";
const CATALOG: Omit<Source, "id" | "addresses" | "port">[] = [
  { name: "FILIPS-LAPTOP (vMix - Output 1)", machine: "FILIPS-LAPTOP", label: "vMix - Output 1", type: "omt", local: true },
  { name: "FIMVIDEO3 (vMix - Output 1 omtx)", machine: "FIMVIDEO3", label: "vMix - Output 1 omtx", type: "omtx", local: false },
  { name: "PIXEL-8 (Pixel 8 Camera)", machine: "PIXEL-8", label: "Pixel 8 Camera", type: "omtx", local: false },
  { name: "FIMVIDEO3 (vMix - Output 2)", machine: "FIMVIDEO3", label: "vMix - Output 2", type: "omt", local: false },
  { name: "FILIPS-LAPTOP (vMix - Output 2)", machine: "FILIPS-LAPTOP", label: "vMix - Output 2", type: "omt", local: true },
  { name: "GALAXY-S23 (Field Cam Red)", machine: "GALAXY-S23", label: "Field Cam Red", type: "omtx", local: false },
  { name: "GALAXY-A54 (Field Cam Blue)", machine: "GALAXY-A54", label: "Field Cam Blue", type: "omtx", local: false },
  { name: "FMS-AUDIENCE (Audience Display)", machine: "FMS-AUDIENCE", label: "Audience Display", type: "omt", local: false },
  { name: "PIT-PC (OBS Program)", machine: "PIT-PC", label: "OBS Program", type: "omt", local: false },
  { name: "IPHONE-15 (Judges Cam)", machine: "IPHONE-15", label: "Judges Cam", type: "omtx", local: false },
  { name: "FIMVIDEO2 (vMix - Multiview with a long name for overflow checks)", machine: "FIMVIDEO2", label: "vMix - Multiview with a long name for overflow checks", type: "omt", local: false },
  { name: "PIXEL-7A (Queue Cam)", machine: "PIXEL-7A", label: "Queue Cam", type: "omtx", local: false },
];

let sources: Source[] = [];
let bridges: Bridge[] = [];
let offline = false;
let nextBridge = 1;
const watchers = new Map<string, number>(); // sourceId -> open MJPEG streams

function setSources(n: number) {
  sources = CATALOG.slice(0, Math.max(0, Math.min(n, CATALOG.length))).map((c, i) => ({
    ...c, id: `s${i + 1}`, addresses: [`192.168.0.${4 + i}`], port: 6400 + i, local: c.local,
  }));
}

const jitter = (v: number, p = 0.03) => +(v * (1 + (Math.random() * 2 - 1) * p)).toFixed(2);

function statsFor(kind: "monitor" | "out" | "in", opts: { kbps?: number; codec?: string } = {}) {
  const base = {
    fps: jitter(29.97, 0.01), width: 1920, height: 1080, frameRate: "30000/1001",
    keyframes: 1 + Math.floor(Math.random() * 2), drops: Math.random() < 0.15 ? 1 : 0,
    audio: { rate: 48000, channels: 2 },
  };
  if (kind === "monitor") {
    return { ...base, mbps: jitter(9.95), codec: "H264", receivers: 2, decoder: "h264", msReceive: jitter(3.1, 0.2), msConvert: jitter(5, 0.2) };
  }
  if (kind === "out") {
    const t = opts.kbps ?? 10000;
    return {
      ...base, mbps: jitter(t / 1000 * 0.98), codec: opts.codec === "hevc" ? "HEVC" : "H264", targetKbps: t,
      receivers: 1, encoder: opts.codec === "hevc" ? "hevc_nvenc" : "h264_amf",
      msReceive: jitter(3.1, 0.2), msConvert: jitter(5, 0.2), msEncode: jitter(27.4, 0.1),
    };
  }
  return { ...base, mbps: jitter(6.2), codec: "VMX1", receivers: 1, decoder: "h264", msReceive: jitter(2.2, 0.2), msConvert: jitter(4.1, 0.2), msEncode: jitter(11.3, 0.2) };
}

function makeBridge(kind: "out" | "in", source: string, opts: { kbps?: number; codec?: string } = {}): Bridge {
  const short = source.match(/\((.*)\)$/)?.[1] ?? source;
  return {
    id: `b${nextBridge++}`, kind, source,
    publishedAs: kind === "out" ? [`${short} omtx`] : source === "*" ? [] : [`${short}`],
    state: "starting", error: null, stats: null, streams: [],
    bitrateKbps: opts.kbps, codec: opts.codec, started: Date.now(),
  };
}

function tickBridges() {
  for (const b of bridges) {
    if (b.state === "error") continue;
    const age = Date.now() - b.started;
    if (b.state === "starting" && age > 1500) b.state = b.kind === "out" ? "running" : "waiting";
    if (b.kind === "out" && b.state === "running") b.stats = statsFor("out", { kbps: b.bitrateKbps, codec: b.codec });
    if (b.kind === "in") {
      const cams = sources.filter((s) => s.type === "omtx" && (b.source === "*" || s.name === b.source));
      if (cams.length && age > 1500) b.state = "running";
      if (b.source === "*") {
        b.streams = cams.map((c) => ({ source: c.name, publishedAs: `${HOST} (${c.label})`, stats: statsFor("in") }));
        b.publishedAs = b.streams.map((s) => s.publishedAs);
        b.stats = null;
      } else if (b.state === "running") {
        b.stats = statsFor("in");
        b.streams = [{ source: b.source, publishedAs: b.publishedAs[0], stats: b.stats }];
      }
    }
  }
}

function seedBridges(mode: string) {
  bridges = [];
  const omt = sources.filter((s) => s.type === "omt");
  if (mode === "running" || mode === "mixed") {
    if (omt[0]) bridges.push({ ...makeBridge("out", omt[0].name, { kbps: 10000, codec: "h264" }), started: 0 });
    bridges.push({ ...makeBridge("in", "*"), started: 0 });
  }
  if (mode === "error" || mode === "mixed") {
    const b = makeBridge("out", omt[1]?.name ?? "FIMVIDEO3 (vMix - Output 2)", { kbps: 8000, codec: "hevc" });
    b.state = "error";
    b.error = "No HEVC encoder on this PC (tried hevc_nvenc, hevc_qsv, hevc_amf)";
    bridges.push(b);
  }
  tickBridges();
}

function snapshot() {
  tickBridges();
  return {
    host: HOST, version: "0.1.0",
    sources, bridges: bridges.map(({ bitrateKbps, codec, started, ...b }) => b),
    monitors: [...watchers].filter(([, n]) => n > 0).map(([sourceId]) => ({ sourceId, stats: statsFor("monitor") })),
  };
}

setSources(Number(arg("--sources", "4")));
seedBridges(arg("--bridges", "none"));

// ---------- streams ----------

const sseClients = new Set<ReadableStreamDefaultController>();
const enc = new TextEncoder();

setInterval(() => {
  if (offline || !sseClients.size) return;
  const msg = enc.encode(`event: state\ndata: ${JSON.stringify(snapshot())}\n\n`);
  for (const c of sseClients) {
    try { c.enqueue(msg); } catch { sseClients.delete(c); }
  }
}, 1000);

function sse(req: Request): Response {
  if (offline) return new Response("offline", { status: 503 });
  let ctl: ReadableStreamDefaultController;
  const stream = new ReadableStream({
    start(c) {
      ctl = c;
      sseClients.add(c);
      c.enqueue(enc.encode(`retry: 1000\nevent: state\ndata: ${JSON.stringify(snapshot())}\n\n`));
    },
    cancel() { sseClients.delete(ctl); },
  });
  req.signal.addEventListener("abort", () => sseClients.delete(ctl));
  return new Response(stream, {
    headers: { "content-type": "text/event-stream", "cache-control": "no-cache", connection: "keep-alive" },
  });
}

function mjpeg(req: Request, id: string, w: number): Response {
  const idx = sources.findIndex((s) => s.id === id);
  if (idx < 0) return new Response("no such source", { status: 404 });
  const list = frames(idx, w);
  const boundary = "omtxframe";
  let timer: ReturnType<typeof setInterval>;
  let i = 0;
  const stop = () => {
    clearInterval(timer);
    watchers.set(id, Math.max(0, (watchers.get(id) ?? 1) - 1));
  };
  watchers.set(id, (watchers.get(id) ?? 0) + 1);
  const stream = new ReadableStream({
    start(c) {
      const push = () => {
        if (!sources.some((s) => s.id === id)) { stop(); try { c.close(); } catch {} return; }
        const f = list[i++ % list.length];
        if (!f) return;
        try {
          c.enqueue(enc.encode(`--${boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: ${f.length}\r\n\r\n`));
          c.enqueue(f);
          c.enqueue(enc.encode("\r\n"));
        } catch { stop(); }
      };
      push();
      timer = setInterval(push, 100);
    },
    cancel: stop,
  });
  req.signal.addEventListener("abort", stop, { once: true });
  return new Response(stream, {
    headers: { "content-type": `multipart/x-mixed-replace; boundary=${boundary}`, "cache-control": "no-cache" },
  });
}

// ---------- server ----------

const json = (v: unknown, status = 200) =>
  new Response(JSON.stringify(v), { status, headers: { "content-type": "application/json" } });

Bun.serve({
  port: PORT,
  idleTimeout: 0,
  async fetch(req) {
    const url = new URL(req.url);
    const p = url.pathname;

    if (p === "/api/events") return sse(req);

    let m = p.match(/^\/api\/preview\/([^/]+)\.mjpg$/);
    if (m) return mjpeg(req, decodeURIComponent(m[1]), Number(url.searchParams.get("w") ?? 960));

    m = p.match(/^\/api\/snapshot\/([^/]+)\.jpg$/);
    if (m) {
      const id = decodeURIComponent(m[1]);
      const idx = sources.findIndex((s) => s.id === id);
      // Every fourth source has no frame yet, to exercise the placeholder.
      if (idx < 0 || idx % 4 === 3) return new Response("no frame", { status: 404 });
      const list = frames(idx, Number(url.searchParams.get("w") ?? 320));
      const f = list[Math.floor(Date.now() / 2000) % Math.max(1, list.length)];
      return f ? new Response(f as unknown as BodyInit, { headers: { "content-type": "image/jpeg", "cache-control": "no-store" } }) : new Response("no frame", { status: 404 });
    }

    if (p === "/api/bridges" && req.method === "POST") {
      const body = await req.json().catch(() => null);
      if (!body || (body.kind !== "out" && body.kind !== "in") || typeof body.source !== "string") {
        return json({ error: "Bad request" }, 400);
      }
      if (body.source !== "*" && !sources.some((s) => s.name === body.source)) return json({ error: "Source not found" }, 404);
      const dup = bridges.find((b) => b.kind === body.kind && b.source === body.source);
      if (dup) return json({ error: "Bridge already running" }, 409);
      const b = makeBridge(body.kind, body.source, { kbps: body.bitrateKbps, codec: body.codec });
      bridges.push(b);
      return json({ id: b.id });
    }

    m = p.match(/^\/api\/bridges\/([^/]+)$/);
    if (m && req.method === "DELETE") {
      bridges = bridges.filter((b) => b.id !== decodeURIComponent(m![1]));
      return new Response(null, { status: 204 });
    }

    if (p === "/mock/scenario" && req.method === "POST") {
      const body = await req.json().catch(() => ({}));
      if (typeof body.sources === "number") setSources(body.sources);
      if (typeof body.bridges === "string") seedBridges(body.bridges);
      if (typeof body.offline === "boolean") {
        offline = body.offline;
        if (offline) for (const c of sseClients) { try { c.close(); } catch {} sseClients.delete(c); }
      }
      return json({ ok: true, sources: sources.length, bridges: bridges.length, offline });
    }

    // static
    const file = Bun.file(join(DIST, p === "/" ? "index.html" : p.replace(/\.\.+/g, "")));
    if (await file.exists()) return new Response(file);
    return new Response("not found", { status: 404 });
  },
});

console.log(`omtx ui mock on http://localhost:${PORT}/  (${sources.length} sources)`);
