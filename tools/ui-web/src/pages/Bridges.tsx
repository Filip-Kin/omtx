import { useEffect, useState } from "react";
import { startBridge, stopBridge, type Bridge, type ServerState, type Stats } from "../api";
import * as f from "../format";

const STATE_LABEL: Record<Bridge["state"], string> = {
  starting: "Starting",
  waiting: "Waiting",
  running: "Running",
  error: "Error",
};

/** Per-frame time as a bar against the frame interval: a full bar means no headroom left. */
function Timing({ label, value, budget }: { label: string; value?: number | null; budget: number | null }) {
  if (!f.has(value)) return null;
  const pct = budget ? Math.min(100, (value / budget) * 100) : 0;
  const level = pct >= 90 ? "hot" : pct >= 60 ? "warm" : "";
  return (
    <div className="timing">
      <span className="timing-k">{label}</span>
      <span className="timing-bar" aria-hidden="true">
        <span className={`timing-fill ${level}`} style={{ width: `${budget ? pct : 0}%` }} />
      </span>
      <span className="timing-v">{f.ms(value)}</span>
    </div>
  );
}

function StatLine({ stats }: { stats?: Stats | null }) {
  if (!stats) return null;
  const budget = (() => {
    const n = f.nominalFps(stats.frameRate) ?? (f.has(stats.fps) && stats.fps > 0 ? stats.fps : null);
    return n ? 1000 / n : null;
  })();
  const bitrate = f.mbps(stats);
  const tgt = f.target(stats);
  const cells: [string, string | null][] = [
    ["Frame rate", f.fps(stats)],
    ["Bitrate", bitrate && tgt ? `${bitrate.replace(" Mbps", "")} / ${tgt}` : bitrate ?? (tgt ? `${tgt} target` : null)],
    ["Resolution", f.resolution(stats)],
    ["Codec", f.codecName(stats.codec)],
    ["Encoder", stats.encoder ?? null],
    ["Decoder", stats.decoder ?? null],
    ["Receivers", f.count(stats.receivers)],
    ["Keyframes", f.count(stats.keyframes)],
    ["Drops", f.count(stats.drops)],
    ["Audio", f.audio(stats.audio)],
  ];
  const shown = cells.filter(([, v]) => v);
  const timings = [stats.msReceive, stats.msConvert, stats.msEncode].some(f.has);
  return (
    <div className="statline">
      {shown.length ? (
        <dl className="cells">
          {shown.map(([k, v]) => (
            <div key={k} className={`cell${k === "Drops" && Number(v) > 0 ? " warn" : ""}`}>
              <dt>{k}</dt>
              <dd>{v}</dd>
            </div>
          ))}
        </dl>
      ) : null}
      {timings ? (
        <div className="timings">
          <Timing label="Receive" value={stats.msReceive} budget={budget} />
          <Timing label="Convert" value={stats.msConvert} budget={budget} />
          <Timing label="Encode" value={stats.msEncode} budget={budget} />
        </div>
      ) : null}
    </div>
  );
}

function BridgeCard({ b }: { b: Bridge }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const stop = async () => {
    setBusy(true);
    setError(null);
    try {
      await stopBridge(b.id);
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  };
  const from = b.source === "*" ? "Every omtx source" : b.source;
  const streams = b.streams ?? [];
  const to =
    b.source === "*"
      ? streams.length || b.publishedAs?.length
        ? `${streams.length || b.publishedAs!.length} ${(streams.length || b.publishedAs!.length) === 1 ? "source" : "sources"}`
        : b.state === "error" ? null : "No sources"
      : b.publishedAs?.length ? b.publishedAs.join(", ") : null;
  // A "*" bridge reports per-camera streams; a single-source bridge reports `stats` directly.
  const showStreams = streams.length > 0 && (b.source === "*" || !b.stats);

  return (
    <li className={`panel bridge state-${b.state}`}>
      <div className="bridge-head">
        <span className="badge">{b.kind === "out" ? "Out" : "In"}</span>
        <span className={`pill ${b.state}`}>{STATE_LABEL[b.state] ?? b.state}</span>
        <span className="spacer" />
        <button className="btn danger" onClick={stop} disabled={busy}>
          {busy ? "Stopping…" : "Stop"}
        </button>
      </div>
      <div className="route">
        <span className="route-from">{from}</span>
        <span className="route-arrow" aria-hidden="true">
          →
        </span>
        <span className="route-to">{to}</span>
      </div>
      {b.state === "error" && b.error ? (
        <div className="bridge-error" role="alert">
          {b.error}
        </div>
      ) : null}
      {error ? (
        <div className="bridge-error" role="alert">
          {error}
        </div>
      ) : null}
      {!showStreams ? <StatLine stats={b.stats} /> : null}
      {showStreams ? (
        <ul className="streams">
          {streams.map((s) => (
            <li key={s.publishedAs || s.source} className="stream">
              <div className="route small">
                <span className="route-from">{s.source}</span>
                <span className="route-arrow" aria-hidden="true">
                  →
                </span>
                <span className="route-to">{s.publishedAs}</span>
              </div>
              <StatLine stats={s.stats} />
            </li>
          ))}
        </ul>
      ) : null}
    </li>
  );
}

function NewBridge({ state }: { state: ServerState }) {
  const [kind, setKind] = useState<"out" | "in">("out");
  const [source, setSource] = useState("");
  const [mbps, setMbps] = useState("10");
  const [codec, setCodec] = useState<"h264" | "hevc">("h264");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const options = state.sources
    .filter((s) => s.type === (kind === "out" ? "omt" : "omtx"))
    .sort((a, b) => a.name.localeCompare(b.name));
  const values = [...(kind === "in" ? ["*"] : []), ...options.map((s) => s.name)];

  // Keep the selection valid as sources come and go or the kind changes.
  useEffect(() => {
    if (!values.includes(source)) setSource(values[0] ?? "");
  }, [values.join("\n"), source]);

  const rate = Number(mbps);
  const rateOk = Number.isFinite(rate) && rate >= 0.5 && rate <= 200;
  const ready = !!source && (kind === "in" || rateOk) && !busy;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!ready) return;
    setBusy(true);
    setError(null);
    try {
      await startBridge(
        kind === "out"
          ? { kind, source, bitrateKbps: Math.round(rate * 1000), codec }
          : { kind, source },
      );
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="panel form" onSubmit={submit} aria-labelledby="new-bridge">
      <h2 id="new-bridge">New bridge</h2>
      <fieldset className="seg">
        <legend className="sr-only">Direction</legend>
        <label className={kind === "out" ? "on" : ""}>
          <input type="radio" name="kind" value="out" checked={kind === "out"} onChange={() => setKind("out")} />
          Out: OMT → omtx
        </label>
        <label className={kind === "in" ? "on" : ""}>
          <input type="radio" name="kind" value="in" checked={kind === "in"} onChange={() => setKind("in")} />
          In: omtx → OMT
        </label>
      </fieldset>
      <label className="field">
        <span>Source</span>
        <select value={source} onChange={(e) => setSource(e.target.value)} disabled={!values.length}>
          {!values.length ? <option value="">{kind === "out" ? "No OMT sources" : "No omtx sources"}</option> : null}
          {kind === "in" ? <option value="*">Every omtx source</option> : null}
          {options.map((s) => (
            <option key={s.id} value={s.name}>
              {s.name}
            </option>
          ))}
        </select>
      </label>
      {kind === "out" ? (
        <div className="field-row">
          <label className="field">
            <span>Bitrate (Mbps)</span>
            <input
              type="number"
              inputMode="decimal"
              min={0.5}
              max={200}
              step={0.5}
              value={mbps}
              onChange={(e) => setMbps(e.target.value)}
              aria-invalid={!rateOk}
            />
          </label>
          <label className="field">
            <span>Codec</span>
            <select value={codec} onChange={(e) => setCodec(e.target.value as "h264" | "hevc")}>
              <option value="h264">H.264</option>
              <option value="hevc">HEVC</option>
            </select>
          </label>
        </div>
      ) : null}
      {error ? (
        <div className="form-error" role="alert">
          {error}
        </div>
      ) : null}
      <button className="btn primary" type="submit" disabled={!ready}>
        {busy ? "Starting…" : "Start"}
      </button>
    </form>
  );
}

export function Bridges({ state }: { state: ServerState | null }) {
  if (!state) return <div className="empty">Connecting…</div>;
  return (
    <section className="page">
      <div className="page-head">
        <h1>Bridges</h1>
      </div>
      <div className="bridges-layout">
        <div className="bridges-list">
          {state.bridges.length === 0 ? (
            <div className="empty panel">No bridges</div>
          ) : (
            <ul className="bridge-list">
              {state.bridges.map((b) => (
                <BridgeCard key={b.id} b={b} />
              ))}
            </ul>
          )}
        </div>
        <NewBridge state={state} />
      </div>
    </section>
  );
}
