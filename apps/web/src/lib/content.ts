// Content snapshot client. Reads GET /internal/content on the internal API
// listener with If-None-Match and a 5s timeout, keeping the last good copy on
// globalThis so pages survive backend restarts. The proxy (edge of the web
// container) and the pages (node runtime) are bundled apart, so each holds its
// own copy - that is expected.

export interface SiteSeo {
  url?: string;
  title?: string;
  description?: string;
}

export interface SiteDoc {
  name?: string;
  tagline?: string;
  contactEmail?: string;
  githubHandle?: string;
  location?: string;
  footerLicense?: string;
  seo?: SiteSeo;
}

export interface HomeHero {
  kicker?: string;
  lines?: string[];
  intro?: string;
  roles?: string[];
}

export interface HomeFact {
  label?: string;
  value?: string;
}

export interface HomeAbout {
  paras?: string[];
  facts?: (string | HomeFact)[];
}

export interface AlsoBuildingItem {
  name?: string;
  url?: string;
  note?: string;
}

export interface StackGroup {
  label?: string;
  items?: string[];
}

export interface HomeDoc {
  hero?: HomeHero;
  about?: HomeAbout;
  clientIntro?: string;
  clientTag?: string;
  alsoBuilding?: AlsoBuildingItem[];
  stack?: StackGroup[];
  workOrder?: string[];
}

export interface WorkSection {
  type?: string;
  heading?: string;
  html?: string;
  items?: { image?: string; alt?: string; caption?: string }[];
  label?: string;
  url?: string;
}

export interface WorkDoc {
  title?: string;
  status?: string;
  category?: string;
  year?: string | number;
  summary?: string;
  links?: { label?: string; url?: string }[];
  tech?: string[];
  repo?: { url?: string; license?: string };
  sections?: WorkSection[];
}

export interface MediaAsset {
  w?: number;
  h?: number;
  widths?: number[];
  og?: boolean;
}

export interface GithubProfile {
  login?: string;
  created_at?: string;
  created?: number;
  public_repos?: number;
  [key: string]: unknown;
}

export interface GithubRepo {
  name?: string;
  language?: string | null;
  [key: string]: unknown;
}

export interface GithubData {
  profile?: GithubProfile | null;
  repos?: GithubRepo[] | null;
  fetchedAt?: number;
}

export interface Redirect {
  from?: string;
  to?: string;
}

export interface Snapshot {
  version: number;
  documents: Record<string, unknown>;
  redirects?: Redirect[];
  media?: Record<string, MediaAsset>;
  github?: GithubData | null;
}

const INTERNAL_ORIGIN = process.env.ARI_API_INTERNAL ?? "http://127.0.0.1:8082";

interface SnapshotCache {
  etag?: string;
  snapshot?: Snapshot;
}

const cacheHolder = globalThis as typeof globalThis & { __ariContentCache?: SnapshotCache };
const cache: SnapshotCache = (cacheHolder.__ariContentCache ??= {});

export class SnapshotUnavailableError extends Error {
  constructor() {
    super("content snapshot unavailable");
    this.name = "SnapshotUnavailableError";
  }
}

export class PreviewExpiredError extends Error {
  constructor() {
    super("preview link expired");
    this.name = "PreviewExpiredError";
  }
}

interface InternalResponse {
  status: number;
  etag?: string;
  snapshot?: Snapshot;
}

async function fetchInternal(pathAndQuery: string, etag?: string): Promise<InternalResponse> {
  const res = await fetch(`${INTERNAL_ORIGIN}${pathAndQuery}`, {
    headers: etag ? { "if-none-match": etag } : undefined,
    signal: AbortSignal.timeout(5_000),
    cache: "no-store",
  });
  if (res.status === 304) return { status: 304 };
  const responseEtag = res.headers.get("etag") ?? undefined;
  if (res.status === 200) {
    const snapshot = (await res.json()) as Snapshot;
    return { status: 200, etag: responseEtag, snapshot };
  }
  return { status: res.status };
}

/**
 * The published snapshot. With a preview token, asks the backend to build that
 * request's own snapshot (draft in place of published); preview results are
 * never written to the last-good cache.
 */
export async function getSnapshot(previewToken?: string): Promise<Snapshot> {
  if (previewToken) {
    let res: InternalResponse;
    try {
      res = await fetchInternal(`/internal/content?preview=${encodeURIComponent(previewToken)}`);
    } catch {
      throw new SnapshotUnavailableError();
    }
    if (res.status === 401 || res.status === 403) throw new PreviewExpiredError();
    if (res.snapshot) return res.snapshot;
    throw new SnapshotUnavailableError();
  }
  let res: InternalResponse;
  try {
    res = await fetchInternal("/internal/content", cache.etag);
  } catch {
    // Backend unreachable or timed out: serve the last good copy.
    if (cache.snapshot) return cache.snapshot;
    throw new SnapshotUnavailableError();
  }
  if (res.status === 304 && cache.snapshot) return cache.snapshot;
  if (res.snapshot) {
    cache.etag = res.etag;
    cache.snapshot = res.snapshot;
    return res.snapshot;
  }
  if (cache.snapshot) return cache.snapshot;
  throw new SnapshotUnavailableError();
}

export function readDoc<T>(snapshot: Snapshot, key: string): T {
  const value = snapshot.documents?.[key];
  return (value && typeof value === "object" ? value : {}) as T;
}

export interface WorkEntry {
  slug: string;
  doc: WorkDoc;
}

export function listWork(snapshot: Snapshot): WorkEntry[] {
  const documents = snapshot.documents ?? {};
  return Object.keys(documents)
    .filter((key) => key.startsWith("work:") && key.length > 5)
    .map((key) => ({
      slug: key.slice("work:".length),
      doc: readDoc<WorkDoc>(snapshot, key),
    }))
    .filter((entry) => entry.slug.length > 0 && Boolean(entry.doc.title));
}

/** workOrder first, then any remaining published work docs. */
export function orderedWork(snapshot: Snapshot): WorkEntry[] {
  const all = listWork(snapshot);
  const order = readDoc<HomeDoc>(snapshot, "page:home").workOrder ?? [];
  const ordered: WorkEntry[] = [];
  for (const slug of order) {
    if (typeof slug !== "string") continue;
    const hit = all.find((entry) => entry.slug === slug);
    if (hit) ordered.push(hit);
  }
  for (const entry of all) {
    if (!ordered.includes(entry)) ordered.push(entry);
  }
  return ordered;
}

export type PathResolution =
  | { kind: "page" }
  | { kind: "redirect"; to: string }
  | { kind: "missing" };

/**
 * What a public GET path means, however it was spelled: a real page, a saved
 * redirect, or missing. A real page always wins over a redirect. Used by
 * src/proxy.ts; fixed paths (/, /not-found, /contact/sent, /admin, /_next, ...)
 * are resolved by the proxy itself and never reach here.
 */
export function resolvePublicPath(snapshot: Snapshot, pathname: string): PathResolution {
  if (pathname === "/") return { kind: "page" };
  const match = /^\/work\/([a-z0-9-]+)$/i.exec(pathname);
  if (match) {
    const key = `work:${match[1].toLowerCase()}`;
    const doc = snapshot.documents?.[key];
    if (doc && typeof doc === "object") {
      const titled = (doc as WorkDoc).title;
      if (typeof titled === "string" && titled.length > 0) return { kind: "page" };
    }
  }
  for (const redirect of snapshot.redirects ?? []) {
    if (
      typeof redirect?.from === "string" &&
      redirect.from === pathname &&
      typeof redirect.to === "string" &&
      redirect.to.startsWith("/") &&
      !redirect.to.startsWith("//")
    ) {
      return { kind: "redirect", to: redirect.to };
    }
  }
  return { kind: "missing" };
}
