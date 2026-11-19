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
