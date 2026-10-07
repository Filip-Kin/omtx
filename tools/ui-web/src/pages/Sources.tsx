import { useState } from "react";
import { startBridge, type ServerState, type Source } from "../api";
import { Thumb } from "../Thumb";

type Filter = "all" | "omt" | "omtx";

const FILTERS: { key: Filter; label: string }[] = [
  { key: "all", label: "All" },
  { key: "omt", label: "OMT" },
  { key: "omtx", label: "omtx" },
];

export function TypeBadge({ type }: { type: Source["type"] }) {
  return <span className={`badge type-${type}`}>{type === "omt" ? "OMT" : "omtx"}</span>;
}

/** True when a bridge already carries this source, so the tile shows "Bridged" instead of the action. */
function isBridged(s: Source, state: ServerState): boolean {
  return state.bridges.some((b) =>
    s.type === "omt"
      ? b.kind === "out" && (b.source === s.name || (b.source === "*" && !!s.local))
      : b.kind === "in" && (b.source === s.name || b.source === "*"),
  );
}

function Tile({ s, bridged }: { s: Source; bridged: boolean }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const bridge = async () => {
    setBusy(true);
    setError(null);
    try {
      await startBridge(
        s.type === "omt"
          ? { kind: "out", source: s.name, sourceId: s.id, codec: "h264" }
          : { kind: "in", source: s.name, sourceId: s.id },
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const watch = `#/watch/${encodeURIComponent(s.id)}`;
  return (
    <li className="tile">
      <a className="tile-media" href={watch} tabIndex={-1} aria-hidden="true">
        <Thumb id={s.id} />
        <span className="tile-badges">
          <TypeBadge type={s.type} />
          {s.local ? <span className="badge">Local</span> : null}
        </span>
        {bridged ? <span className="badge bridged">Bridged</span> : null}
      </a>
      <div className="tile-body">
        <div className="tile-label" title={s.name}>
          {s.label || s.name}
        </div>
        <div className="tile-machine">{s.machine}</div>
        {error ? (
          <div className="tile-error" role="alert">
            {error}
          </div>
        ) : null}
      </div>
      <div className="tile-actions">
        <a className="btn primary" href={watch}>
          Watch
        </a>
        {!bridged ? (
          <button className="btn" onClick={bridge} disabled={busy}>
            {busy ? "Starting…" : s.type === "omt" ? "Send as omtx" : "Bridge to OMT"}
          </button>
        ) : null}
      </div>
    </li>
  );
}

export function Sources({ state }: { state: ServerState | null }) {
  const [filter, setFilter] = useState<Filter>("all");

  if (!state) return <div className="empty">Connecting…</div>;

  const counts = {
    all: state.sources.length,
    omt: state.sources.filter((s) => s.type === "omt").length,
    omtx: state.sources.filter((s) => s.type === "omtx").length,
  };
  const shown = state.sources
    .filter((s) => filter === "all" || s.type === filter)
    .slice()
    .sort((a, b) => a.machine.localeCompare(b.machine) || a.label.localeCompare(b.label));

  return (
    <section className="page">
      <div className="page-head">
        <h1>Sources</h1>
        <div className="chips" role="group" aria-label="Filter">
          {FILTERS.map((f) => (
            <button
              key={f.key}
              className={`chip${filter === f.key ? " on" : ""}`}
              aria-pressed={filter === f.key}
              onClick={() => setFilter(f.key)}
            >
              {f.label}
              <span className="count">{counts[f.key]}</span>
            </button>
          ))}
        </div>
      </div>
      {shown.length === 0 ? (
        <div className="empty">
          {filter === "all" ? "No sources" : filter === "omt" ? "No OMT sources" : "No omtx sources"}
        </div>
      ) : (
        <ul className="grid">
          {shown.map((s) => (
            <Tile key={s.id} s={s} bridged={isBridged(s, state)} />
          ))}
        </ul>
      )}
    </section>
  );
}
