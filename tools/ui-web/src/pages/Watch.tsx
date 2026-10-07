import { useCallback, useEffect, useRef, useState } from "react";
import { api, type ServerState } from "../api";
import * as f from "../format";
import { TypeBadge } from "./Sources";

/** Preview width requested from the backend, picked once per mount from the screen size. */
function previewWidth(): number {
  const px = Math.min(screen.width, innerWidth * 1.5) * (devicePixelRatio || 1);
  if (px <= 700) return 640;
  if (px <= 1100) return 960;
  if (px <= 1500) return 1280;
  return 1920;
}

function useFullscreen(el: React.RefObject<HTMLElement | null>) {
  const [on, setOn] = useState(false);
  useEffect(() => {
    const ch = () => setOn(document.fullscreenElement === el.current && !!el.current);
    document.addEventListener("fullscreenchange", ch);
    return () => document.removeEventListener("fullscreenchange", ch);
  }, [el]);
  const toggle = () => {
    if (document.fullscreenElement) document.exitFullscreen();
    else el.current?.requestFullscreen?.();
  };
  return { on, toggle, supported: typeof document !== "undefined" && !!document.fullscreenEnabled };
}

/**
 * MJPEG preview. On a broken stream (backend restart, source gone) the img is reattached
 * after 2 s with a fresh query so the browser opens a new request.
 */
function Preview({ id }: { id: string }) {
  const [w] = useState(previewWidth);
  const [attempt, setAttempt] = useState(0);
  const [status, setStatus] = useState<"loading" | "live" | "lost">("loading");

  useEffect(() => {
    setStatus("loading");
    setAttempt(0);
  }, [id]);

  useEffect(() => {
    if (status !== "lost") return;
    const t = setTimeout(() => {
      setStatus("loading");
      setAttempt((a) => a + 1);
    }, 2000);
    return () => clearTimeout(t);
  }, [status]);

  const src = `${api.preview(id, w)}${attempt ? `&r=${attempt}` : ""}`;
  return (
    <>
      <img
        key={src}
        className="preview"
        src={src}
        alt=""
        onLoad={() => setStatus("live")}
        onError={() => setStatus("lost")}
      />
      {status !== "live" ? (
        <div className="stage-status" role="status">
          {status === "loading" ? "Connecting…" : "No signal"}
        </div>
      ) : null}
    </>
  );
}

/** RFC 6381 codec strings for an Annex B keyframe: H.264 from its SPS, HEVC as a list to probe. */
function codecCandidates(codec: number, au: Uint8Array): string[] {
  if (codec === 1) {
    for (let i = 0; i + 7 < au.length; i++) {
      if (au[i] === 0 && au[i + 1] === 0 && au[i + 2] === 1 && (au[i + 3] & 0x1f) === 7) {
        const h = (b: number) => b.toString(16).padStart(2, "0");
        return [`avc1.${h(au[i + 4])}${h(au[i + 5])}${h(au[i + 6])}`, "avc1.640033", "avc1.42e01f"];
      }
    }
    return ["avc1.640033", "avc1.4d4033", "avc1.42e01f"];
  }
  return ["hev1.1.6.L153.B0", "hvc1.1.6.L153.B0", "hev1.1.6.L123.B0", "hvc1.1.6.L123.B0", "hev1.2.4.L153.B0", "hvc1.2.4.L153.B0"];
}

/**
 * Full frame rate preview for omtx sources: the backend relays the H.264/HEVC access units
 * (api/stream/{id}) and the browser decodes them with WebCodecs onto a canvas. Falls back to the
 * MJPEG preview when the browser cannot decode the codec.
 */
function StreamPreview({ id, onFallback }: { id: string; onFallback: () => void }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [status, setStatus] = useState<"loading" | "live" | "lost">("loading");

  useEffect(() => {
    let stop = false;
    const ac = new AbortController();
    let dec: VideoDecoder | null = null;
    let decCodec = 0;
    let needKey = true;
    const draw = (frame: VideoFrame) => {
      const c = canvas.current;
      if (c) {
        if (c.width !== frame.displayWidth || c.height !== frame.displayHeight) {
          c.width = frame.displayWidth;
          c.height = frame.displayHeight;
        }
        c.getContext("2d")?.drawImage(frame, 0, 0);
        setStatus("live");
      }
      frame.close();
    };
    const configure = async (codec: number, au: Uint8Array): Promise<boolean> => {
      for (const c of codecCandidates(codec, au)) {
        const config: VideoDecoderConfig = { codec: c, optimizeForLatency: true };
        try {
          if ((await VideoDecoder.isConfigSupported(config)).supported) {
            dec?.close();
            dec = new VideoDecoder({ output: draw, error: () => { dec = null; needKey = true; } });
            dec.configure(config);
            decCodec = codec;
            return true;
          }
        } catch { /* try the next string */ }
      }
      return false;
    };
    const handle = async (fr: Uint8Array) => {
      const v = new DataView(fr.buffer, fr.byteOffset, fr.byteLength);
      const codec = fr[0];
      const key = (fr[1] & 1) === 1;
      const ts = Number(v.getBigInt64(20, true)) / 10; // 100 ns -> µs
      const au = fr.subarray(28);
      if (!dec || dec.state === "closed" || codec !== decCodec) {
        if (!key) return;
        if (!(await configure(codec, au))) { stop = true; ac.abort(); onFallback(); return; }
        needKey = false;
      }
      if (needKey && !key) return;
      // Falling behind: drop to the next keyframe instead of building a delay.
      if (dec!.decodeQueueSize > 6 && !key) { needKey = true; return; }
      needKey = false;
      dec!.decode(new EncodedVideoChunk({ type: key ? "key" : "delta", timestamp: ts, data: au }));
    };
    const run = async () => {
      while (!stop) {
        setStatus("loading");
        try {
          const res = await fetch(`api/stream/${encodeURIComponent(id)}`, { signal: ac.signal, cache: "no-store" });
          if (!res.ok || !res.body) throw new Error(String(res.status));
          const reader = res.body.getReader();
          let buf = new Uint8Array(0);
          for (;;) {
            const { value, done } = await reader.read();
            if (done) break;
            const next = new Uint8Array(buf.length + value.length);
            next.set(buf); next.set(value, buf.length);
            buf = next;
            let off = 0;
            while (buf.length - off >= 4) {
              const len = new DataView(buf.buffer, buf.byteOffset + off, 4).getUint32(0, true);
              if (buf.length - off - 4 < len) break;
              await handle(buf.subarray(off + 4, off + 4 + len));
              if (stop) return;
              off += 4 + len;
            }
            buf = buf.slice(off);
          }
        } catch {
          if (stop) return;
        }
        setStatus("lost");
        needKey = true;
        await new Promise((r) => setTimeout(r, 2000));
      }
    };
    run();
    return () => { stop = true; ac.abort(); try { dec?.close(); } catch { /* closed */ } };
  }, [id, onFallback]);

  return (
    <>
      <canvas ref={canvas} className="preview" />
      {status !== "live" ? (
        <div className="stage-status" role="status">
          {status === "loading" ? "Connecting…" : "No signal"}
        </div>
      ) : null}
    </>
  );
}

function Row({ k, v }: { k: string; v: string | null | undefined }) {
  if (!v) return null;
  return (
    <div className="row">
      <dt>{k}</dt>
      <dd>{v}</dd>
    </div>
  );
}

export function Watch({ state, id }: { state: ServerState | null; id: string | null }) {
  const stage = useRef<HTMLDivElement>(null);
  const fs = useFullscreen(stage);
  // omtx sources play at full rate through WebCodecs; JPEG preview when the browser cannot
  const [jpegFor, setJpegFor] = useState<string | null>(null);
  const fallback = useCallback(() => setJpegFor(id), [id]);

  // "#/watch" with no id opens the first source.
  useEffect(() => {
    if (!id && state && state.sources.length) {
      location.replace(`#/watch/${encodeURIComponent(state.sources[0].id)}`);
    }
  }, [id, state]);

  if (!state) return <div className="empty">Connecting…</div>;
  if (!state.sources.length && !id) return <div className="empty">No sources</div>;

  const src = state.sources.find((s) => s.id === id) ?? null;
  const mine = state.monitors.filter((m) => m.sourceId === id);
  const stats = (mine.find((m) => m.full) ?? mine[0])?.stats ?? null;
  const streamable = !!src && src.type === "omtx" && typeof VideoDecoder !== "undefined" && jpegFor !== src.id;
  const sorted = state.sources
    .slice()
    .sort((a, b) => a.machine.localeCompare(b.machine) || a.label.localeCompare(b.label));
  const address =
    src?.addresses?.length ? src.addresses.map((a) => (src.port ? `${a}:${src.port}` : a)).join(", ") : null;

  return (
    <section className="page watch">
      <div className="page-head">
        <label className="picker">
          <span className="sr-only">Source</span>
          <select
            value={id ?? ""}
            onChange={(e) => location.assign(`#/watch/${encodeURIComponent(e.target.value)}`)}
          >
            {!src && id ? <option value={id}>Source offline</option> : null}
            {sorted.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
        </label>
        {fs.supported && src ? (
          <button className="btn" onClick={fs.toggle} aria-pressed={fs.on}>
            {fs.on ? "Exit fullscreen" : "Fullscreen"}
          </button>
        ) : null}
      </div>
      <div className="watch-body">
        <div className={`stage${fs.on ? " fs" : ""}`} ref={stage} onDoubleClick={src ? fs.toggle : undefined}>
          {src ? (
            streamable ? <StreamPreview id={src.id} onFallback={fallback} /> : <Preview id={src.id} />
          ) : (
            <div className="stage-status">Source offline</div>
          )}
        </div>
        {src ? (
          <aside className="panel stats" aria-label="Stats">
            <div className="stats-head">
              <h1 className="stats-title">{src.label || src.name}</h1>
              <div className="stats-sub">
                <TypeBadge type={src.type} />
                {src.local ? <span className="badge">Local</span> : null}
                <span>{src.machine}</span>
              </div>
            </div>
            <dl>
              <Row k="Resolution" v={f.resolution(stats)} />
              <Row k="Frame rate" v={f.fps(stats)} />
              <Row k="Bitrate" v={f.mbps(stats)} />
              <Row k="Codec" v={f.codecName(stats?.codec)} />
              <Row k="Keyframes" v={f.count(stats?.keyframes)} />
              <Row k="Drops" v={f.count(stats?.drops)} />
              <Row k="Receivers" v={f.count(stats?.receivers)} />
              <Row k="Audio" v={f.audio(stats?.audio)} />
              <Row k="Decoder" v={stats?.decoder} />
              <Row k="Receive" v={f.ms(stats?.msReceive)} />
              <Row k="Convert" v={f.ms(stats?.msConvert)} />
              <Row k="Address" v={address} />
            </dl>
          </aside>
        ) : null}
      </div>
    </section>
  );
}
