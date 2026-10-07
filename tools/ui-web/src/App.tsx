import { useEffect, useRef, useState } from "react";
import { useLive } from "./api";
import { Sources } from "./pages/Sources";
import { Watch } from "./pages/Watch";
import { Bridges } from "./pages/Bridges";

export type Route =
  | { page: "sources" }
  | { page: "watch"; id: string | null }
  | { page: "bridges" };

function parse(hash: string): Route {
  const parts = hash.replace(/^#\/?/, "").split("/").filter(Boolean);
  if (parts[0] === "watch") return { page: "watch", id: parts[1] ? decodeURIComponent(parts[1]) : null };
  if (parts[0] === "bridges") return { page: "bridges" };
  return { page: "sources" };
}

function useRoute(): Route {
  const [route, setRoute] = useState(() => parse(location.hash));
  useEffect(() => {
    const on = () => setRoute(parse(location.hash));
    addEventListener("hashchange", on);
    return () => removeEventListener("hashchange", on);
  }, []);
  return route;
}

function useMedia(q: string): boolean {
  const [m, setM] = useState(() => matchMedia(q).matches);
  useEffect(() => {
    const mq = matchMedia(q);
    const on = () => setM(mq.matches);
    mq.addEventListener("change", on);
    return () => mq.removeEventListener("change", on);
  }, [q]);
  return m;
}

const NAV: { page: Route["page"]; href: string; label: string }[] = [
  { page: "sources", href: "#/sources", label: "Sources" },
  { page: "watch", href: "#/watch", label: "Watch" },
  { page: "bridges", href: "#/bridges", label: "Bridges" },
];

export function App() {
  const route = useRoute();
  const { state, online } = useLive();
  const narrow = useMedia("(max-width: 860px)");
  const [open, setOpen] = useState(false);
  const menuBtn = useRef<HTMLButtonElement>(null);
  const nav = useRef<HTMLElement>(null);

  // Drawer closes on route change, on Escape, on scrim tap, and when the window grows past the breakpoint.
  useEffect(() => setOpen(false), [route]);
  useEffect(() => {
    if (!narrow) setOpen(false);
  }, [narrow]);
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        setOpen(false);
        menuBtn.current?.focus();
      }
    };
    addEventListener("keydown", onKey);
    nav.current?.querySelector<HTMLElement>("a")?.focus();
    return () => removeEventListener("keydown", onKey);
  }, [open]);

  const bridgeCount = state?.bridges.length ?? 0;
  const drawerHidden = narrow && !open;

  return (
    <div className={`app${open ? " drawer-open" : ""}`}>
      <header className="bar">
        <button
          ref={menuBtn}
          className="menu-btn"
          aria-label="Menu"
          aria-expanded={open}
          aria-controls="nav"
          onClick={() => setOpen((o) => !o)}
        >
          <span aria-hidden="true" className="menu-icon" />
        </button>
        <a className="brand" href="#/sources">
          omtx
        </a>
        <span className={`host-chip${online ? "" : " offline"}`} role="status">
          {state?.host ? <span className="host-name">{state.host}</span> : null}
          {!online ? <span className="offline-tag">Offline</span> : null}
        </span>
        <nav
          id="nav"
          ref={nav}
          className="nav"
          aria-label="Main"
          inert={drawerHidden}
          aria-hidden={drawerHidden || undefined}
        >
          {NAV.map((n) => (
            <a
              key={n.page}
              href={n.href}
              className={route.page === n.page ? "active" : undefined}
              aria-current={route.page === n.page ? "page" : undefined}
              onClick={() => setOpen(false)}
            >
              {n.label}
              {n.page === "bridges" && bridgeCount > 0 ? <span className="count">{bridgeCount}</span> : null}
            </a>
          ))}
          {state?.version ? <span className="version">v{state.version}</span> : null}
        </nav>
        <div className="scrim" hidden={!open} onClick={() => setOpen(false)} />
      </header>
      <main className={online ? "" : "stale"}>
        {route.page === "sources" && <Sources state={state} />}
        {route.page === "watch" && <Watch state={state} id={route.id} />}
        {route.page === "bridges" && <Bridges state={state} />}
      </main>
    </div>
  );
}
