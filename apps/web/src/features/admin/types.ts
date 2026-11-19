/**
 * Shared client-side types for the admin API surface.
 *
 * Every shape below matches the live API exactly (captured 2026-11-19 against
 * the merged server): /api/auth/me is flat, wrappers are {documents|items},
 * timestamps are epoch ms.
 */

// ---------------------------------------------------------------- auth

/** GET /api/auth/me — flat */
export interface Me {
  id: number;
  email: string;
  name: string;
  role: "owner" | "staff";
  permissions: string[];
  hasPassword: boolean;
  totp: { enabled: boolean; pending: boolean };
  passkeys: number;
  stepUpFresh: boolean;
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
  messagesUnread: number;
  messagesUnreadWeek: number;
  lastPublishAt: number | null;
  analytics: { visits7d: number };
  jobs: OverviewJob[];
  generatedAt: number;
}

// ---------------------------------------------------------------- analytics

export interface AnalyticsSummary {
  days: number;
  from: string;
  to: string;
  views: number;
  visitors: number;
  newVisitors: number;
  journeys: number;
  convertedJourneys: number;
  contactConversions: number;
  outboundConversions: number;
  avgActiveMs: number;
  avgScrollPct: number;
  botHits: number;
  viewsPerDay: { day: string; views: number; visitors: number }[];
  hitsPerDay: { day: string; hits: number }[];
  topEntryPages: { path: string; count: number }[];
  topExitPages: { path: string; count: number }[];
}

export interface AnalyticsPages {
  days: number;
  pages: { path: string; views: number; visitors: number; avgActiveMs: number; avgScrollPct: number; entries: number; exits: number; convertedJourneys: number }[];
  topNextPages: { from: string; to: string; count: number }[];
}

export interface AnalyticsReferrers {
  days: number;
  referrers: { source: string; visits: number; visitors: number }[];
  campaigns: { campaign: string; visits: number }[];
}

export interface AnalyticsClicks {
  days: number;
  clicks: { label: string; target: string | null; kind: "click" | "outbound" | "conversion"; count: number; journeys: number; lastAt: number }[];
  sections: { section: string; count: number }[];
}

export interface AnalyticsGeo {
  days: number;
  geo: boolean;
  attribution: string | null;
  places: { country: string | null; region: string | null; city: string | null; network: string | null; views: number; visitors: number }[];
}

export interface AnalyticsFunnel {
  days: number;
  steps: { step: string; label: string; count: number }[];
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

/** GET /api/admin/content → { documents: [...] } */
export interface ContentList {
  documents: ContentListItem[];
}

export interface ContentListItem {
  key: string;
  version: number;
  updatedAt: number;
  hasDraft: boolean;
  hasPublished: boolean;
  draftDiffers: boolean;
  /** published JSON (what a visitor sees), or null when unpublished */
  published: ContentValue | null;
}

export interface DraftResponse {
  key: string;
  version: number;
  draft: ContentValue;
}

export interface RevisionsResponse {
  revisions: { version: number; note: string | null; author: number | null; publishedAt: number }[];
}

// ---------------------------------------------------------------- media

export interface MediaList {
  items: MediaItem[];
}

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

export interface MessagesResponse {
  items: Message[];
  unread: number;
}

export interface Message {
  id: number;
  name: string;
  email: string;
  body: string;
  createdAt: number;
  read: boolean;
  archived: boolean;
}

// ---------------------------------------------------------------- settings

export interface SettingsResponse {
  items: SettingRow[];
}

export interface SettingRow {
  key: string;
  secret: boolean;
  /** present for non-secrets; secrets expose hasValue only */
  value: string | null;
  hasValue: boolean;
  updatedAt: number | null;
  updatedBy: number | null;
}

// ---------------------------------------------------------------- users

export interface UsersResponse {
  items: StaffUser[];
  permissions: { key: string; description: string }[];
}

export interface StaffUser {
  id: number;
  email: string;
  name: string;
  role: "owner" | "staff";
  permissions: string[];
  active: boolean;
  hasPassword: boolean;
  hasTotp: boolean;
  createdAt: number;
}

export interface InviteResult {
  inviteUrl: string;
  inviteExpiresAt: number;
}

export interface ResetResult {
  resetUrl: string;
  resetExpiresAt: number;
}

// ---------------------------------------------------------------- audit

export interface AuditPage {
  items: AuditEntry[];
  /** id to use as ?before= for the next page, or null at the end */
  next: number | null;
}

export interface AuditEntry {
  id: number;
  at: number;
  actor: number | null;
  actorEmail: string | null;
  action: string;
  target: string | null;
  detail: string | null;
  ip: string | null;
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
  id: number; // credential rowid
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
