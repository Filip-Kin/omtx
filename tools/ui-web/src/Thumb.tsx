import { useEffect, useRef, useState } from "react";
import { api } from "./api";

const REFRESH_MS = 2000;

/**
 * Snapshot thumbnail. Fetches only while the tile is on screen and the tab is visible, and
 * swaps frames only after the next one has loaded, so there is no flicker. A 404 (no frame
 * yet) or any other failure keeps the last frame, or the neutral placeholder if there is none.
 */
export function Thumb({ id, width = 320 }: { id: string; width?: number }) {
  const box = useRef<HTMLDivElement>(null);
  const [src, setSrc] = useState<string | null>(null);
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const io = new IntersectionObserver(([e]) => setVisible(e.isIntersecting), { rootMargin: "100px" });
    io.observe(el);
    return () => io.disconnect();
  }, []);

  useEffect(() => {
    if (!visible) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    let img: HTMLImageElement | null = null;

    const tick = () => {
      if (stopped) return;
      if (document.visibilityState !== "visible") {
        timer = setTimeout(tick, REFRESH_MS);
        return;
      }
      const url = api.snapshot(id, width, Date.now());
      img = new Image();
      img.decoding = "async";
      img.onload = () => {
        if (!stopped) setSrc(url);
        timer = setTimeout(tick, REFRESH_MS);
      };
      img.onerror = () => {
        timer = setTimeout(tick, REFRESH_MS);
      };
      img.src = url;
    };
    tick();
    return () => {
      stopped = true;
      if (timer) clearTimeout(timer);
      if (img) {
        img.onload = img.onerror = null;
        img.src = "";
      }
    };
  }, [visible, id, width]);

  return (
    <div className="thumb" ref={box}>
      {src ? <img src={src} alt="" /> : <div className="thumb-empty" aria-hidden="true" />}
    </div>
  );
}
