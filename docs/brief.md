# Shared brief — aricummings.com rebuild

Every builder reads this plus `docs/ledger.md` (fixed decisions, open questions) and their
area's section below. Skill references live in `~/.zcode/skills/web-design/references/` —
read the ones named for your area before writing code. Work only inside your worktree, only
in your area's folders. Don't add dependencies beyond the ones listed in
`stack-and-architecture.md` plus your area's note. `TreatWarningsAsErrors` and `Nullable`
are on. Migrations are additive-only. Never stub anything; unimplemented = not written.

## Repo layout (final)

```
apps/api/               ASP.NET Core, project name AriApi, root namespace Ari
  Platform/             ApiHost, Env, Crypto, Tokens, Database, Edge/, Api/, Jobs/, Media/, Outbox/
  Features/Auth/        sign-in, 2FA, passkeys, sessions, invites, resets
  Features/Content/     documents, revisions, redirects, snapshot, preview, seeding
  Features/Media/       upload pipeline, media serving
  Features/Messages/    contact form + message inbox
  Features/Settings/    settings incl. SMTP
  Features/Users/       staff users (owner-only, step-up)
  Features/Audit/       audit log read
  Features/Analytics/   beacon, analytics API
  Features/GitHub/      github-sync job + cache table
  Features/Admin/       admin overview (dashboard) endpoints
  Migrations/           NNNN_name.sql, embedded
  Seed/                 seed-content.json, media manifest
apps/api.tests/         xUnit (AriApi.Tests), builds the real server on real sockets
apps/web/               Next.js App Router, TS strict, Tailwind 4
  src/proxy.ts          nonce, redirects, surface check, 404 rewrite
  src/app/(public)/     home, work/[slug], 404 route
  src/lib/              csp.ts, content client (internal :8082, ETag, last-good copy on globalThis)
  src/features/admin/   client-only admin SPA, own pushState router
deploy/                 compose.yml, update.sh, systemd units, smoke test, updater test,
                        proxy example, web-image/Dockerfile, api Dockerfile
.github/workflows/build.yml
docs/                   ledger, brief, design-system, deploy README, launch checklist
tests/playwright/       Playwright phone+desktop against the real containers
```

## Contracts between areas (ports)

- **Content snapshot** (Content owns): `GET /internal/content` (+`If-None-Match`), `/internal/content/version`. Immutable `ContentSnapshot`: documents by key, redirects, media index, github derived data. Web reads only this.
- **Health** (Platform owns): `/api/health` reports `contentVersion`, `githubSyncedAt`, db ok.
- **GitHub cache** (GitHub owns): table `github_cache(id text pk, fetched_at long, data text)`. Content's snapshot builder calls `IGitHubCache.Read()`; absent → snapshot omits the strip, never fails.
- **Outbox** (Platform owns; all features enqueue): `IOutbox.Enqueue(kind, payload, recipients)`. Jobs drain every minute. Mail via ISmtpSender from Settings.
- **Media** (Media owns): `IMediaStore` for Content's in-use checks; `/media/<id>/<n>.<ext>` serving rules per skill.
- **Analytics events** (Analytics owns): edge calls `IPageHitCounter.Count(path)`; beacon posts to `/api/analytics/beacon` (rate 120/60). Contact submit and outbound-link click arrive as beacon events with labels — conversions, always counted.
- **Audit** (Platform owns): `AuditLog.Record(...)` used by every write path.
- **Auth guards** (Auth owns, Platform applies): `RequireStaff`, `RequirePermission("...")`, `RequireOwner`, `RequireStepUp` route filters.

## Permissions (this site)

`content.edit`, `content.publish`, `media.manage`, `messages.read`, `messages.manage`,
`analytics.read`, `analytics.export`, `settings.manage`, `audit.read`, `users.manage`
(owner-only in practice). Owners hold everything implicitly.

## Admin navigation

Dashboard, Statistics, Content, Media, SEO, Redirects, Messages, Settings, Users, Audit log,
Account. No business screens. Grouped: OVERVIEW (Dashboard, Statistics), SITE (Content, Media,
SEO, Redirects), INBOX (Messages), SYSTEM (Settings, Users, Audit log), plus Account.

## Content documents (kinds + keys)

- `site` — name, tagline, contactEmail, githubHandle, location, footerLicense, seo defaults
- `page:home` — hero (kicker, lines[], intro), about (paras[], facts[]), clientIntro,
  alsoBuilding[{name,url,note}], stack groups[{label, items[]}], workOrder[slugs]
- `work:<slug>` — title, status, category (`oss`|`client`), year, summary, links[{label,url}],
  tech[], repo{url,license}, sections[] (flexible blocks: text|photos|link per skill rules)

Reserved slugs: `admin`, `api`, `media`, `account`, `checkout`, `cart`. `work` index pages
are derived from published work documents in `workOrder` order, then remainder by updated.

Work slugs at seed: `enclave`, `rustos`, `multiforge`, `edex` (covers DE+OS+the RustOS port),
`red-wagon-rentals`, `connected-riding`. Home `alsoBuilding`: Quark, fleetboard,
the-perimeter, PointsSlip, MileageTracker, Tailscale-Ubiquiti, WatchArcade.

## Ports, origins, env (dev defaults)

`PUBLIC_ORIGIN=http://localhost:8080`, `ADMIN_ORIGIN=http://localhost:8081` (dev),
`ADMIN_PORT=8081`, internal 8082, web 3000. Prod values in deploy/. `TRUSTED_PROXY_CIDRS`
empty in dev. Dev flags (`ARI_INSECURE_COOKIES`, `ARI_DISABLE_RATE_LIMITS`, `ARI_TEST_...`)
must make production boot fail. Key file: dev key named as dev-only; prod from
`APP_KEY_FILE`/`APP_KEYS`, refuse to start without.

## Build/test commands

- api: `dotnet build apps/Ari.sln` (warnings as errors), `dotnet test apps/api.tests`
- web: `bun install`, `bun run build` in apps/web
- Each builder uses their own ports (8090+range per worktree) and `--output`/`bin/obj` paths
  local to the worktree. Never touch another builder's paths.

## Definition of done (per area)

Code complete, area tests written and passing, `docs/ledger.md` "Not proven yet" updated,
no TODOs, no stubs. Report: what was built, what's tested against what, what needs the
integrator.

## Contracts, pinned (builders implement exactly these)

### Content snapshot — `GET /internal/content` (internal surface only), `If-None-Match` → 304

```json
{
  "version": 1765560000000,
  "documents": { "site": {}, "page:home": {}, "work:<slug>": {} },
  "redirects": [{ "from": "/x", "to": "/y" }],
  "media": { "<16hex>": { "w": 1920, "h": 1080, "widths": [400, 800, 1200], "og": true } },
  "github": { "profile": {}, "repos": [], "fetchedAt": 1765560000000 }
}
```
`documents` holds published JSON only. `github` is null when never fetched. `/internal/content/version` → `{"version":n}`. Version numbers come from the clock, compared for equality only.

### Analytics beacon — `POST /api/analytics/beacon` (public surface, rate 120/60, response always 204)

```json
{ "v": 1, "visitor": "opaque-string", "journey": "opaque-string",
  "events": [ { "t": "view", "path": "/", "title": "…", "referrer": "host", "campaign": "…" },
              { "t": "active", "ms": 4200 }, { "t": "scroll", "pct": 88 },
              { "t": "click", "label": "GitHub", "target": "https://…", "x": 120, "y": 40, "section": "header" },
              { "t": "outbound", "label": "GitHub", "target": "https://…" } ] }
```
`t=conversion` is emitted by the web contact-success page. Client sends one beacon per page with all events buffered; screen size, language, timezone in fields `sw`,`sh`,`lang`,`tz` of the body root. Visitor/journey ids: first-party cookie `ari_v` (1 year) and `ari_j` (30 min), set client-side, opaque random.

### Auth + admin API surface

Cookies `__Host-ari_at` (path /, 10 min) and `__Secure-ari_rt` (path /api/auth, 7d rotation). 401 from any /api/admin route → SPA tries POST /api/auth/refresh once, then signs out.

- `POST /api/auth/signin {email,password}` → `{next:"2fa",token}` | 204+cookies | 401 `{error,code}`
- `POST /api/auth/2fa {token,code}` | recovery `{token,code}` → 204+cookies
- `POST /api/auth/signout`, `POST /api/auth/refresh`, `GET /api/auth/me`
- `POST /api/auth/passkey/options`, `POST /api/auth/passkey/verify` (sign-in), `POST /api/auth/account/passkeys/*` (register/list/delete)
- `GET|PUT /api/auth/account/password`, `POST /api/auth/account/totp/start|confirm`, `GET /api/auth/account/totp/recovery`, `GET|DELETE /api/auth/account/sessions/{id}`
- `GET /api/admin/content` · `GET|PUT /api/admin/content/{key}/draft` · `POST …/publish|unpublish|preview` · `GET …/revisions` · `POST …/rollback/{version}`
- `GET|POST|DELETE /api/admin/redirects[/{id}]`
- `GET|POST /api/admin/media` (multipart upload) · `PATCH|DELETE /api/admin/media/{id}`
- `GET /api/admin/messages` · `PATCH|DELETE /api/admin/messages/{id}`
- `GET /api/admin/settings` · `PUT /api/admin/settings/{key}` (owner, step-up for mail/keys)
- `GET|POST /api/admin/users` · `PATCH /api/admin/users/{id}` · `POST /api/admin/users/{id}/reset` (owner, step-up)
- `GET /api/admin/audit?before=&limit=`
- `GET /api/admin/analytics/summary?days=` · `…/pages` · `…/referrers` · `…/clicks` · `…/geo` · `…/export.csv`
- `GET /api/admin/overview` (dashboard: messages unread, last publish, analytics 7d, jobs state)
- `POST /api/contact {name,email,body,_gotcha}` (public, rate 5/1)

### Web/admin file ownership (no overlaps)

- web-public owns `apps/web/**` except `apps/web/src/features/admin/**`
- admin-spa owns `apps/web/src/features/admin/**` only
- The admin loader route `apps/web/src/app/admin/[[...slug]]/page.tsx` is written by web-public, fixed content:

```tsx
"use client";
export default dynamic(() => import("@/features/admin").then(m => m.default), { ssr: false });
```

### Local tooling

API: build/test with `~/.dotnet/dotnet` (user SDK has ASP.NET; system one does not). Web: `bun`. Each builder uses its own port range (auth 810x, content 811x, analytics 812x, web 813x) and its own `/tmp/<area>` data dir. Never touch another worktree.
