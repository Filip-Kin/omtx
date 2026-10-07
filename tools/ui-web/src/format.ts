import type { Audio, Stats } from "./api";

const has = (v: unknown): v is number => typeof v === "number" && Number.isFinite(v);

export function codecName(c?: string | null): string | null {
  if (!c) return null;
  const u = c.toUpperCase();
  if (u === "H264" || u === "AVC") return "H.264";
  if (u === "HEVC" || u === "H265") return "HEVC";
  if (u === "VMX1" || u === "VMX") return "VMX";
  return c;
}

/** "60/1" -> 60, "30000/1001" -> 29.97 */
export function nominalFps(fr?: string | null): number | null {
  if (!fr) return null;
  const [n, d] = fr.split("/").map(Number);
  if (!has(n)) return null;
  const v = has(d) && d > 0 ? n / d : n;
  return v > 0 ? v : null;
}

export function num(v: number, digits = 2): string {
  return v.toFixed(digits).replace(/\.?0+$/, "");
}

export function resolution(s?: Stats | null): string | null {
  if (!s || !has(s.width) || !has(s.height)) return null;
  return `${s.width}×${s.height}`;
}

export function fps(s?: Stats | null): string | null {
  if (!s) return null;
  const nominal = nominalFps(s.frameRate);
  if (has(s.fps)) {
    return nominal ? `${s.fps.toFixed(1)} / ${num(nominal)} fps` : `${s.fps.toFixed(1)} fps`;
  }
  return nominal ? `${num(nominal)} fps` : null;
}

export function mbps(s?: Stats | null): string | null {
  if (!s || !has(s.mbps)) return null;
  return `${s.mbps.toFixed(2)} Mbps`;
}

export function target(s?: Stats | null): string | null {
  if (!s || !has(s.targetKbps)) return null;
  return `${num(s.targetKbps / 1000, 1)} Mbps`;
}

export function audio(a?: Audio | null): string | null {
  if (!a) return null;
  const parts: string[] = [];
  if (has(a.rate)) parts.push(`${num(a.rate / 1000, 1)} kHz`);
  if (has(a.channels)) {
    parts.push(a.channels === 1 ? "mono" : a.channels === 2 ? "stereo" : `${a.channels} ch`);
  }
  return parts.length ? parts.join(" ") : null;
}

export function count(v?: number | null): string | null {
  return has(v) ? String(v) : null;
}

export function ms(v?: number | null): string | null {
  return has(v) ? `${v.toFixed(1)} ms` : null;
}

export { has };
