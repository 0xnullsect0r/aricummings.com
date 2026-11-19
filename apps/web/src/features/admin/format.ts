/** Small formatting helpers. Epochs are milliseconds everywhere. */

export function fmtDate(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return "—";
  const d = new Date(ms);
  const pad = (n: number): string => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function fmtRelative(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return "never";
  const delta = Date.now() - ms;
  if (delta < 0) return fmtDate(ms);
  const minute = 60_000;
  if (delta < minute) return "just now";
  if (delta < 60 * minute) return `${Math.floor(delta / minute)} min ago`;
  if (delta < 24 * 60 * minute) return `${Math.floor(delta / (60 * minute))} h ago`;
  if (delta < 7 * 24 * 60 * minute) return `${Math.floor(delta / (24 * 60 * minute))} d ago`;
  return fmtDate(ms);
}

export function fmtNum(n: number | null | undefined): string {
  if (n === null || n === undefined) return "—";
  return n.toLocaleString("en-US");
}

export function fmtBytes(n: number | null | undefined): string {
  if (n === null || n === undefined) return "—";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

export function fmtDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return "—";
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  const h = Math.floor(m / 60);
  return `${h}h ${m % 60}m`;
}

export function fmtPct(n: number | null | undefined): string {
  if (n === null || n === undefined) return "—";
  return `${Math.round(n)}%`;
}
