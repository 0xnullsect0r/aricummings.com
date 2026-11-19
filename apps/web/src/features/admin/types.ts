/**
 * Shared client-side types for the admin API surface.
 *
 * The response shapes here follow docs/brief.md ("Contracts, pinned"). Where the
 * brief pins only the route, the shape below is this client's assumption and is
 * listed in the area report for the integrator to diff against the API.
 */

// ---------------------------------------------------------------- auth

export interface MeUser {
  id: string;
  email: string;
  name: string;
  owner: boolean;
}

/** GET /api/auth/me */
export interface Me {
  user: MeUser;
  /** Explicit permission strings the signed-in user holds. Owners hold everything. */
  permissions: string[];
}

export type SignInResult =
  | { kind: "signed_in" }
  | { kind: "2fa"; token: string };

// ---------------------------------------------------------------- overview

export interface OverviewJob {
  name: string;
  lastRunAt: number | null;
  ok: boolean;
  detail?: string | null;
}

/** GET /api/admin/overview */
export interface Overview {
  messages: { unread: number };
  content: { lastPublishedAt: number | null; lastPublishedBy: string | null; drafts: number };
  analytics: { visitors7d: number; pageviews7d: number; conversions7d: number };
  jobs: OverviewJob[];
}

// ---------------------------------------------------------------- analytics

export interface AnalyticsSummary {
  days: number;
  visitors: number;
  newVisitors: number;
  pageviews: number;
  activeMs: number;
  conversions: number;
  funnel: { label: string; count: number }[];
  weeklyEmail: { enabled: boolean } | null;
}

export interface AnalyticsPage {
  path: string;
  views: number;
  activeMs: number;
  scrollPct: number | null;
}

export interface AnalyticsReferrer {
  host: string;
  visits: number;
}

export interface AnalyticsClick {
  label: string;
  target: string | null;
  count: number;
}

export interface AnalyticsGeoRow {
  country: string | null;
  region: string | null;
  city: string | null;
  visits: number;
}

// ---------------------------------------------------------------- content

export interface SeoDefaults {
  title?: string;
  description?: string;
  noindex?: boolean;
}

/** Document shapes from the brief. All keys optional: pages treat missing as empty. */
export interface SiteDoc {
  name?: string;
  tagline?: string;
  contactEmail?: string;
  githubHandle?: string;
  location?: string;
  footerLicense?: string;
  seo?: SeoDefaults;
}

export interface HomeHero {
  kicker?: string;
  lines?: string[];
  intro?: string;
}

export interface HomeAbout {
  paras?: string[];
  facts?: { label: string; value: string }[];
}

export interface AlsoBuilding {
  name: string;
  url: string;
  note?: string;
}

export interface StackGroup {
  label: string;
  items: string[];
}

export interface HomeDoc {
  hero?: HomeHero;
  about?: HomeAbout;
  clientIntro?: string;
  alsoBuilding?: AlsoBuilding[];
  stack?: StackGroup[];
  workOrder?: string[];
}

export interface WorkLink {
  label: string;
  url: string;
}

export interface WorkRepo {
  url?: string;
  license?: string;
}

export interface PhotoItem {
  image: string; // media id (16 hex)
  alt?: string;
  caption?: string;
}

export type SectionBlock =
  | { type: "text"; heading?: string; html?: string }
  | { type: "photos"; items?: PhotoItem[] }
  | { type: "link"; label?: string; url?: string };

export interface WorkDoc {
  title?: string;
  status?: string;
  category?: "oss" | "client";
  year?: string;
  summary?: string;
  links?: WorkLink[];
  tech?: string[];
  repo?: WorkRepo;
  sections?: SectionBlock[];
  seo?: SeoDefaults;
}

export type ContentValue = SiteDoc | HomeDoc | WorkDoc | Record<string, unknown>;

export interface ContentListItem {
  key: string;
  title: string;
  updatedAt: number;
  updatedBy: string;
  publishedAt: number | null;
  /** draft exists and differs from published */
  draftDirty: boolean;
  published: ContentValue | null;
}

export interface DraftResponse {
  key: string;
  draft: ContentValue;
  updatedAt: number;
  updatedBy: string;
}

export interface Revision {
  version: number;
  at: number;
  by: string;
  note: string | null;
}

// ---------------------------------------------------------------- media

export interface MediaItem {
  id: string; // 16 hex
  alt: string;
  w: number;
  h: number;
  widths: number[];
  bytes: number;
  createdAt: number;
  inUse?: boolean;
}

// ---------------------------------------------------------------- messages

export interface Message {
  id: string;
  name: string;
  email: string;
  body: string;
  receivedAt: number;
  read: boolean;
  archived: boolean;
}

// ---------------------------------------------------------------- settings

export interface SettingRow {
  key: string;
  value: string | null;
  /** secret rows: value never echoed, only whether one is stored */
  hasPassword: boolean;
  updatedAt: number;
  updatedBy: string;
}

// ---------------------------------------------------------------- users

export interface StaffUser {
  id: string;
  email: string;
  name: string;
  owner: boolean;
  active: boolean;
  permissions: string[];
  twoFactor: boolean;
  createdAt: number;
  lastSeenAt: number | null;
}

export interface InviteResult {
  id: string;
  inviteUrl: string;
  inviteExpiresAt: number;
}

export interface ResetResult {
  resetUrl: string;
  resetExpiresAt: number;
}

// ---------------------------------------------------------------- audit

export interface AuditEntry {
  id: string;
  at: number;
  actor: string;
  action: string;
  target: string | null;
  detail: string | null;
  ip: string | null;
}

export interface AuditPage {
  entries: AuditEntry[];
  nextBefore: number | null;
}

// ---------------------------------------------------------------- account

export interface PasswordRules {
  min: number;
  max: number;
}

export interface TotpStart {
  secret: string;
  /** plain SVG markup from the API */
  qr: string;
  otpauth: string;
}

export interface RecoveryCodes {
  codes: string[];
}

export interface PasskeyRecord {
  id: string;
  name: string;
  createdAt: number;
  lastUsedAt: number | null;
}

export interface SessionRecord {
  id: string;
  createdAt: number;
  lastSeenAt: number;
  ip: string | null;
  userAgent: string | null;
  current: boolean;
}
