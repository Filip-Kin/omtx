import { useState } from "react";
import type { ServerState, Source } from "../api";
import { Thumb } from "../Thumb";

type Filter = "all" | "omt" | "omtx";

const FILTERS: { key: Filter; label: string }[] = [
  { key: "all", label: "All" },
  { key: "omt", label: "OMT" },
  { key: "omtx", label: "omtx" },
];

export const typeName = (t: Source["type"]) => (t === "omt" ? "OMT" : "omtx");

function Tile({ s }: { s: Source }) {
  return (
    <li>
      <a className="tile" href={`#/watch/${encodeURIComponent(s.id)}`}>
        <Thumb id={s.id} />
        <span className="tile-label" title={s.name}>
          {s.label || s.name}
        </span>
        <span className="tile-meta">
          {s.machine} · {typeName(s.type)}
        </span>
      </a>
    </li>
  );
}

export function Sources({ state }: { state: ServerState | null }) {
  const [filter, setFilter] = useState<Filter>("all");

  if (!state) return <div className="empty">Connecting…</div>;

  const shown = state.sources
    .filter((s) => filter === "all" || s.type === filter)
    .slice()
    .sort((a, b) => a.machine.localeCompare(b.machine) || a.label.localeCompare(b.label));

  return (
    <div className="page">
      <div className="page-head">
        <h1 className="sr-only">Sources</h1>
        <div className="tabs" role="group" aria-label="Filter">
          {FILTERS.map((f) => (
            <button
              key={f.key}
              className={filter === f.key ? "on" : undefined}
              aria-pressed={filter === f.key}
              onClick={() => setFilter(f.key)}
            >
              {f.label}
            </button>
          ))}
        </div>
      </div>
      {shown.length === 0 ? (
        <div className="empty">{filter === "all" ? "No sources" : filter === "omt" ? "No OMT sources" : "No omtx sources"}</div>
      ) : (
        <ul className="grid">
          {shown.map((s) => (
            <Tile key={s.id} s={s} />
          ))}
        </ul>
      )}
    </div>
  );
}
