import { useEffect, useState } from "react";

export type SourceType = "omt" | "omtx";

export interface Audio {
  rate?: number | null;
  channels?: number | null;
}

export interface Stats {
  /** per stream: "idle" while nothing watches it */
  state?: BridgeState | null;
  error?: string | null;
  fps?: number | null;
  mbps?: number | null;
  width?: number | null;
  height?: number | null;
  codec?: string | null;
  frameRate?: string | null;
  keyframes?: number | null;
  drops?: number | null;
  receivers?: number | null;
  targetKbps?: number | null;
  encoder?: string | null;
  decoder?: string | null;
  msReceive?: number | null;
  msConvert?: number | null;
  msEncode?: number | null;
  audio?: Audio | null;
}

export interface Source {
  id: string;
  name: string;
  machine: string;
  label: string;
  type: SourceType;
  addresses?: string[];
  port?: number;
  local?: boolean;
}

export interface BridgeStream {
  source: string;
  publishedAs: string;
  stats?: Stats | null;
}

export type BridgeState = "starting" | "idle" | "waiting" | "running" | "error" | "stopped";

export interface Bridge {
  id: string;
  kind: "out" | "in";
  source: string;
  publishedAs?: string[];
  state: BridgeState;
  error?: string | null;
  stats?: Stats | null;
  streams?: BridgeStream[];
}

export interface Monitor {
  sourceId: string;
  /** true: the Watch page's full-rate receive; false: the thumbnail's */
  full?: boolean;
  stats?: Stats | null;
}

export interface Settings {
  /** highest frame rate an out bridge encodes; 0 = the source's own */
  outMaxFps: number;
}

export interface ServerState {
  host: string;
  version: string;
  settings?: Settings;
  sources: Source[];
  bridges: Bridge[];
  monitors: Monitor[];
}

// All URLs are relative so the bundle works from any path the backend serves it under.
export const api = {
  events: "api/events",
  preview: (id: string, w: number) => `api/preview/${encodeURIComponent(id)}.mjpg?w=${w}`,
  snapshot: (id: string, w: number, t: number) =>
    `api/snapshot/${encodeURIComponent(id)}.jpg?w=${w}&t=${t}`,
};

export type NewBridge =
  | { kind: "out"; source: string; sourceId?: string; codec: "h264" | "hevc" }
  | { kind: "in"; source: string; sourceId?: string };

async function errorText(res: Response): Promise<string> {
  try {
    const body = await res.json();
    if (body && typeof body.error === "string" && body.error) return body.error;
  } catch {
    /* not JSON */
  }
  return `HTTP ${res.status}`;
}

export async function startBridge(b: NewBridge): Promise<string> {
  let res: Response;
  try {
    res = await fetch("api/bridges", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(b),
    });
  } catch {
    throw new Error("No connection");
  }
  if (!res.ok) throw new Error(await errorText(res));
  const body = await res.json().catch(() => ({}));
  return body.id ?? "";
}

export async function saveSettings(s: Partial<Settings>): Promise<void> {
  let res: Response;
  try {
    res = await fetch("api/settings", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(s),
    });
  } catch {
    throw new Error("No connection");
  }
  if (!res.ok) throw new Error(await errorText(res));
}

export async function stopBridge(id: string): Promise<void> {
  let res: Response;
  try {
    res = await fetch(`api/bridges/${encodeURIComponent(id)}`, { method: "DELETE" });
  } catch {
    throw new Error("No connection");
  }
  if (!res.ok && res.status !== 404) throw new Error(await errorText(res));
}

export interface Live {
  state: ServerState | null;
  online: boolean;
}

/**
 * Subscribes to /api/events. EventSource reconnects by itself on a dropped stream, but gives up
 * for good when the server answers with a non-stream response (503, proxy error page). A
 * watchdog also treats 5 s without a `state` event as offline, since a half-open TCP
 * connection never fires `error`. Either way the source is rebuilt with backoff up to 5 s.
 */
export function useLive(): Live {
  const [state, setState] = useState<ServerState | null>(null);
  const [online, setOnline] = useState(false);

  useEffect(() => {
    let es: EventSource | null = null;
    let lastEvent = 0;
    let retry: ReturnType<typeof setTimeout> | null = null;
    let delay = 1000;
    let disposed = false;
    let build: string | null = null;

    const connect = () => {
      if (disposed) return;
      es?.close();
      es = new EventSource(api.events);
      lastEvent = Date.now();
      es.addEventListener("state", (ev) => {
        lastEvent = Date.now();
        delay = 1000;
        try {
          const s = JSON.parse((ev as MessageEvent).data) as ServerState & { build?: string };
          // omtx was updated and restarted under this open page: load the new page
          if (s.build) {
            if (build && build !== s.build) { location.reload(); return; }
            build = s.build;
          }
          setState({
            host: s.host ?? "",
            version: s.version ?? "",
            settings: s.settings,
            sources: s.sources ?? [],
            bridges: s.bridges ?? [],
            monitors: s.monitors ?? [],
          });
          setOnline(true);
        } catch {
          /* ignore a malformed event, keep the last good state */
        }
      });
      es.onerror = () => {
        setOnline(false);
        if (es && es.readyState === EventSource.CLOSED) schedule();
      };
    };

    const schedule = () => {
      if (retry || disposed) return;
      es?.close();
      retry = setTimeout(() => {
        retry = null;
        connect();
      }, delay);
      delay = Math.min(delay * 2, 5000);
    };

    const watchdog = setInterval(() => {
      if (Date.now() - lastEvent > 5000) {
        setOnline(false);
        schedule();
      }
    }, 1000);

    connect();
    return () => {
      disposed = true;
      clearInterval(watchdog);
      if (retry) clearTimeout(retry);
      es?.close();
    };
  }, []);

  return { state, online };
}
