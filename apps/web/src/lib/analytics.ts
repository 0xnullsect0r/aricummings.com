// First-party analytics beacon client. Started once per page load from the
// public layout (the public site uses plain <a> links, so every page is a full
// load and this script runs once per view). Buffers view/active/scroll/click/
// outbound/conversion events and sends one beacon per flush: every 15 seconds,
// when hidden, and on pagehide. No third-party script, no fingerprinting.

type BeaconEvent =
  | { t: "view"; path: string; title: string; referrer: string; campaign: string }
  | { t: "active"; ms: number }
  | { t: "scroll"; pct: number }
  | { t: "click"; label: string; target: string; x: number; y: number; section: string }
  | { t: "outbound"; label: string; target: string }
  | { t: "conversion"; label: string };

interface BeaconBody {
  v: 1;
  visitor: string;
  journey: string;
  sw: number;
  sh: number;
  lang: string;
  tz: string;
  events: BeaconEvent[];
}

const ENDPOINT = "/api/analytics/beacon";
const MAX_EVENTS = 40;
const VISITOR_COOKIE = "ari_v";
const JOURNEY_COOKIE = "ari_j";
const ID_PATTERN = /^[A-Za-z0-9_-]{8,64}$/;
const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";

let started = false;

function readCookie(name: string): string | null {
  for (const part of document.cookie.split(";")) {
    const eq = part.indexOf("=");
    if (eq < 0) continue;
    if (part.slice(0, eq).trim() === name) return part.slice(eq + 1).trim();
  }
  return null;
}

function randomId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(24));
  let out = "";
  for (const byte of bytes) out += ALPHABET[byte % ALPHABET.length];
  return out;
}

/** Existing value if it still looks valid, else a fresh one; always re-set. */
function ensureCookie(name: string, maxAgeSeconds: number): string {
  const existing = readCookie(name);
  const value = existing && ID_PATTERN.test(existing) ? existing : randomId();
  document.cookie = `${name}=${value}; max-age=${maxAgeSeconds}; path=/; samesite=lax`;
  return value;
}

function sectionOf(element: Element): string {
  const tracked = element.closest<HTMLElement>("[data-track]");
  if (tracked?.dataset.track) return tracked.dataset.track.slice(0, 80);
  const section = element.closest("section");
  const heading = section?.querySelector("h1, h2, h3");
  return (heading?.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, 80);
}

function cleanHref(href: string): string {
  return href.split("?")[0].slice(0, 120);
}

export function startAnalytics(): void {
  if (started || typeof window === "undefined") return;
  started = true;

  const visitor = ensureCookie(VISITOR_COOKIE, 365 * 24 * 60 * 60);
  const journey = ensureCookie(JOURNEY_COOKIE, 30 * 60);

  const events: BeaconEvent[] = [];
  let activeMs = 0;
  let activeEmitted = 0;
  let scrollMax = 0;
  let scrollEmitted = 0;
  let lastInput = Date.now();
  let flushTimer: number | undefined;

  const sw = window.screen?.width ?? 0;
  const sh = window.screen?.height ?? 0;
  const lang = navigator.language ?? "";
  let tz = "";
  try {
    tz = Intl.DateTimeFormat().resolvedOptions().timeZone ?? "";
  } catch {
    tz = "";
  }

  let referrerHost = "";
  if (document.referrer) {
    try {
      const url = new URL(document.referrer);
      if (url.host !== location.host) referrerHost = url.host;
    } catch {
      referrerHost = "";
    }
  }
  let campaign = "";
  try {
    campaign = new URLSearchParams(location.search).get("utm_campaign")?.slice(0, 80) ?? "";
  } catch {
    campaign = "";
  }

  events.push({
    t: "view",
    path: location.pathname.slice(0, 120),
    title: document.title.slice(0, 120),
    referrer: referrerHost,
    campaign,
  });

  const markInput = () => {
    lastInput = Date.now();
  };
  const onScroll = () => {
    const doc = document.documentElement;
    const scrollable = Math.max(1, doc.scrollHeight - window.innerHeight);
    const pct = Math.round(Math.min(1, Math.max(0, window.scrollY / scrollable)) * 100);
    if (pct > scrollMax) scrollMax = pct;
  };

  function trimEvents() {
    if (events.length > MAX_EVENTS) events.splice(0, events.length - MAX_EVENTS);
  }

  function onClick(event: MouseEvent) {
    const target = event.target;
    if (!(target instanceof Element)) return;
    const el = target.closest("a, button, summary");
    if (!el) return;
    const label = (el.textContent || el.getAttribute("aria-label") || "")
      .replace(/\s+/g, " ")
      .trim()
      .slice(0, 80);
    const href = el instanceof HTMLAnchorElement ? el.getAttribute("href") : null;
    const targetUrl = href ? cleanHref(href) : "";
    if (label || targetUrl) {
      events.push({
        t: "click",
        label,
        target: targetUrl,
        x: Math.round((event.clientX / Math.max(1, window.innerWidth)) * 1000) / 1000,
        y: Math.round((event.clientY / Math.max(1, window.innerHeight)) * 1000) / 1000,
        section: sectionOf(el),
      });
    }
    if (el instanceof HTMLAnchorElement && href) {
      if (/^https?:\/\//i.test(href)) {
        try {
          const url = new URL(href, location.href);
          if (url.host !== location.host) {
            events.push({ t: "outbound", label, target: targetUrl });
          }
        } catch {
          /* unparseable href: not outbound */
        }
      } else if (/^(mailto|tel|sms):/i.test(href)) {
        events.push({ t: "outbound", label, target: targetUrl });
      }
    }
    trimEvents();
  }

  function flush() {
    if (events.length === 0) return;
    const body: BeaconBody = {
      v: 1,
      visitor,
      journey,
      sw,
      sh,
      lang,
      tz,
      events: events.splice(0, events.length),
    };
    const text = JSON.stringify(body);
    try {
      if (!navigator.sendBeacon?.(ENDPOINT, text)) {
        void fetch(ENDPOINT, {
          method: "POST",
          keepalive: true,
          headers: { "content-type": "text/plain" },
          body: text,
        }).catch(() => {});
      }
    } catch {
      /* the beacon endpoint always answers 204; failures are dropped */
    }
  }

  function flushCycle() {
    if (activeMs - activeEmitted >= 1000) {
      events.push({ t: "active", ms: activeMs - activeEmitted });
      activeEmitted = activeMs;
    }
    if (scrollMax > scrollEmitted) {
      events.push({ t: "scroll", pct: scrollMax });
      scrollEmitted = scrollMax;
    }
    trimEvents();
    flush();
  }

  function scheduleFlush(ms: number) {
    if (flushTimer !== undefined) return;
    flushTimer = window.setTimeout(() => {
      flushTimer = undefined;
      flushCycle();
    }, ms);
  }

  window.addEventListener("pointerdown", markInput, { passive: true });
  window.addEventListener("keydown", markInput, { passive: true });
  window.addEventListener("scroll", onScroll, { passive: true });
  document.addEventListener("click", onClick, true);

  // Conversion events arrive as CustomEvents ("ari:track"); the contact
  // success page emits one on mount.
  window.addEventListener("ari:track", (event: Event) => {
    const detail = (event as CustomEvent<{ t?: string; label?: string }>).detail;
    if (detail?.t === "conversion") {
      events.push({ t: "conversion", label: String(detail.label ?? "contact").slice(0, 80) });
      trimEvents();
      scheduleFlush(1000);
    }
  });

  window.setInterval(() => {
    if (document.visibilityState === "visible" && Date.now() - lastInput < 30_000) {
      activeMs += 1000;
    }
  }, 1000);
  window.setInterval(flushCycle, 15_000);
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden") flushCycle();
  });
  window.addEventListener("pagehide", flushCycle);
}
