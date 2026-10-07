import { useState } from "react";
import { startBridge, stopBridge, type Bridge, type ServerState, type Stats } from "../api";
import * as f from "../format";

const STATE: Record<string, string> = {
  starting: "Starting",
  idle: "Idle",
  waiting: "Waiting",
  running: "Running",
  error: "Error",
  stopped: "Stopped",
};

type Row = { key: string; source: string; publishedAs: string; state: string; error: string | null; stats: Stats | null };

/** One row per published stream: a "*" bridge has one per source, a single bridge has one. */
function rowsOf(list: Bridge[]): Row[] {
  const rows: Row[] = [];
  for (const b of list) {
    const streams = b.streams ?? [];
    if (b.source === "*" || streams.length > 1) {
      for (const s of streams) {
        rows.push({
          key: `${b.id}/${s.publishedAs || s.source}`,
          source: s.source,
          publishedAs: s.publishedAs,
          state: s.stats?.state ?? b.state,
          error: s.stats?.error ?? null,
          stats: s.stats ?? null,
        });
      }
      if (b.state === "error" && b.error) rows.push({ key: b.id, source: "", publishedAs: "", state: "error", error: b.error, stats: null });
    } else {
      rows.push({
        key: b.id,
        source: b.source,
        publishedAs: b.publishedAs?.[0] ?? streams[0]?.publishedAs ?? "",
        state: b.state,
        error: b.error ?? null,
        stats: b.stats ?? streams[0]?.stats ?? null,
      });
    }
  }
  return rows.sort((a, b) => a.source.localeCompare(b.source));
}

/** Work per frame, without the time spent waiting for the next frame to arrive. */
function frameTime(s: Stats | null): string | null {
  if (!s) return null;
  const parts = [s.msConvert, s.msEncode].filter(f.has);
  return parts.length ? f.ms(parts.reduce((a, b) => a + b, 0)) : null;
}

function Section({ kind, state }: { kind: "out" | "in"; state: ServerState }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const list = state.bridges.filter((b) => b.kind === kind);
  const auto = list.find((b) => b.source === "*" && b.state !== "stopped");
  const rows = rowsOf(list);

  const toggle = async () => {
    setBusy(true);
    setError(null);
    try {
      if (auto) await stopBridge(auto.id);
      else await startBridge(kind === "out" ? { kind, source: "*", codec: "h264" } : { kind, source: "*" });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const title = kind === "out" ? "OMT → omtx" : "omtx → OMT";
  const empty = !auto ? "Off" : kind === "out" ? "No OMT sources on this PC" : "No omtx sources";

  return (
    <section className="section" aria-labelledby={`h-${kind}`}>
      <div className="section-head">
        <h2 id={`h-${kind}`}>{title}</h2>
        <button className="switch" role="switch" aria-checked={!!auto} onClick={toggle} disabled={busy}>
          Automatic
        </button>
      </div>
      {error ? (
        <div className="error" role="alert">
          {error}
        </div>
      ) : null}
      {rows.length === 0 ? (
        <div className="empty">{empty}</div>
      ) : (
        <table className="table">
          <thead>
            <tr>
              <th>Source</th>
              <th>Published as</th>
              <th>State</th>
              <th className="n">Frame rate</th>
              <th className="n">Bitrate</th>
              <th>Resolution</th>
              <th>Codec</th>
              <th>{kind === "out" ? "Encoder" : "Decoder"}</th>
              <th className="n">Drops</th>
              <th className="n">Frame time</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => {
              const s = r.stats;
              const live = r.state === "running";
              return (
                <tr key={r.key} className={r.state === "error" ? "row-error" : live ? "" : "row-idle"}>
                  <td data-k="Source" className="name">{r.source}</td>
                  <td data-k="Published as" className="name">{r.publishedAs}</td>
                  <td data-k="State" className={`state ${r.state}`}>
                    {STATE[r.state] ?? r.state}
                    {r.error ? <div className="state-error">{r.error}</div> : null}
                  </td>
                  <td data-k="Frame rate" className="n">{live ? f.fps(s) : null}</td>
                  <td data-k="Bitrate" className="n">{live ? f.mbps(s) : null}</td>
                  <td data-k="Resolution">{f.resolution(s)}</td>
                  <td data-k="Codec">{f.codecName(s?.codec)}</td>
                  <td data-k={kind === "out" ? "Encoder" : "Decoder"}>{kind === "out" ? s?.encoder : s?.decoder}</td>
                  <td data-k="Drops" className={`n${(s?.drops ?? 0) > 0 ? " warn" : ""}`}>{f.count(s?.drops)}</td>
                  <td data-k="Frame time" className="n">{live ? frameTime(s) : null}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </section>
  );
}

export function Bridges({ state }: { state: ServerState | null }) {
  if (!state) return <div className="empty">Connecting…</div>;
  return (
    <div className="page">
      <h1 className="sr-only">Bridges</h1>
      <Section kind="out" state={state} />
      <Section kind="in" state={state} />
    </div>
  );
}
