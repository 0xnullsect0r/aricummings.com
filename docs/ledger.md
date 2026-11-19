# Ledger — aricummings.com rebuild

## Fixed decisions

- **Stack**: Next.js App Router + TypeScript (apps/web), ASP.NET Core .NET 10 (apps/api), SQLite (app.db + analytics.db), two Docker containers + compose.
- **Branching**: all rebuild work on `rebuild`; `main` still serves the old Vite site through the pull-loop until cutover. Never push `rebuild` work to `main` before cutover.
- **Site type**: personal portfolio. Main action: contact. No customer accounts, booking, cart, payments.
- **Theme**: dark-only. No light palette, no toggle, anywhere (public, admin, emails excepted).
- **Design direction**: brutalist mono catalog. Space Mono (identity, headings, labels, terminal chrome) + IBM Plex Sans (body). Self-hosted via @fontsource. Sharp corners (0 radius), 1px visible borders, instant transitions, visible grid, uppercase mono labels with tracking.
- **Origins**: `PUBLIC_ORIGIN=https://www.aricummings.com`, `ADMIN_ORIGIN=https://admin.aricummings.com`. Separate-port mode (`ADMIN_PORT` set): public 8080, admin 8081, internal 8082 (unpublished), web 3000 (internal network only).
- **Project prefix**: `ari`. Cookies `__Host-ari_at` (path /), `__Secure-ari_rt` (path /api/auth). Surface header `X-Site-Surface`.
- **Domains at cutover**: bare domain 301 → www; arigreenecummings.com → www (proxy-level, outside compose); nullsect0r.dev untouched, linked only.
- **Encryption**: `v1.<kid>.<iv>.<tag>.<body>`, AES-256-GCM, HKDF info `site:encrypt` / `site:jwt`, contexts `setting:<key>`, `totp:<userId>`. JWT audiences: `staff`, `staff-2fa`, `preview`, `staff-marker`. HS256, iss `ari`.
- **Emails**: contact form → contact@aricummings.com via outbox. SMTP from encrypted settings, owner-only screen.
- **Analytics**: first-party; everything except fingerprint and identity modules. Conversions: contact submit, outbound GitHub/email clicks. Geo from local mmdb (MaxMind GeoLite2; DB file is a deploy-time item).
- **GitHub data**: hybrid. Curated work documents in the CMS + `github-sync` job (every 30 min) fetching profile + repos server-side into `github_cache` table, merged into the content snapshot as derived data. Optional `GITHUB_TOKEN` encrypted setting. Unauthenticated fallback. Fails soft to last good snapshot.
- **License**: site code AGPL-3.0 (repo LICENSE). Footer states AGPL-3.0.
- **Keylogger/session-stealer repos are never mentioned on the site.**
- **Content framing**: leads with the work (Enclave, RustOS, MultiForge, eDEX), Delphian School / McMinnville OR / CS-in-college as context. MultiForge framing: present as GPL-3.0 free software per its README unless Ari says otherwise (open question below).

## Unresolved, do not decide

- MultiForge: README says GPL-3.0 free software; Ari has also described it as closed-source/license-gated. Which framing ships? (Currently drafted as GPL-3.0 open source, installers public.)
- SMTP provider + credentials (person).
- DNS records for admin.aricummings.com + redirects (person).
- Proxmox allocation: which LXC/VM, IPs (person).
- Final copy review of drafted Enclave / RustOS / eDEX / Artistech text (Ari).
- Passkey RP ID is `aricummings.com` — confirm that's wanted (changing it later wipes all passkeys).

## Not proven yet

- Nothing has run on the real server. Local runs only.
- SMTP delivery untested until credentials exist.
- Passkeys: software authenticator tests only until tried on a real phone.
- Content/media/GitHub areas (wt/content): built and tested on real sockets locally
  (41 tests, worktree ports 8110-8119, /tmp/content-wt). Not proven: the github-sync job
  against the live API (tests disable it via ARI_TEST_DISABLE_GITHUB_SYNC=1); media
  processing in the production container (needs libvips in the runtime image — present on
  the dev host, libvips 8.18.7); real-photo color/rendering quality (tests use noise
  images); the 40 MB refusal path above the shared 1 MiB API body limit is only proven up
  to ~2 MB uploads.
- Web (apps/web): full local runs against a stubbed internal content server (dev +
  production build on :8130, stub on :8131). Full visual QA of the public site happens at
  integration (HTML/CSS verified locally only).
- Platform gaps found while building content are FIXED in Platform as of 30b4846 (edge
  serves the internal surface; TokenIssuer issues claims; CLI modes receive args). The
  InternalContentMiddleware/PreviewTokens workarounds in Features/Content still work and
  can be simplified later.

## Proven (2026-11-19, backbone commit 0a1c0d2)

- Edge rules against the real server on real sockets, twice: curl smoke and xUnit
  (`apps/api.tests/EdgeTests.cs`, 7/7): exact /healthz, admin-path refusal on the public
  surface (raw + decoded spellings), internal-path refusal, security headers on every
  response, admin noindex + robots, /api/health, 502 when web is down.
- Security headers reach Kestrel's own malformed-request rejections via the transport
  wrapper (verified with a raw malformed request).
- Local runs need the user-local .NET in `~/.dotnet` (SDK 10.0.113 + ASP.NET 10.0.13):
  run `~/.dotnet/dotnet test apps/Ari.slnx`. The system muxer lacks ASP.NET.

## Web area notes (web-public builder, 2026-11-19)

- **Avatar placeholder**: `src/app/icon.svg` is a locally generated mono glyph (terminal chevron
  + accent cursor). The real GitHub avatar lands via the media pipeline at deploy; swap the
  icon route then. No network fetch at build time.
- **Preview contract**: web sends `GET /internal/content?preview=<jwt>` and gets the draft
  snapshot (200), 403 for expired/invalid links. `proxy.ts` validates the token up front
  (403 "Preview expired" page), forwards it as the `x-preview-token` request header, and
  sets `X-Robots-Tag: noindex, nofollow` + `Cache-Control: no-store`. Preview snapshots
  are never written to the last-good cache. Content's implementation matches this.
- **Admin loader**: `src/app/admin/[[...slug]]/page.tsx` is the fixed snippet from the brief;
  `src/features/admin` (merged from wt/adminspa) is what it loads. The placeholder alias in
  `next.config.ts` switches off automatically when that folder exists.
- **Optional keys web reads beyond the pinned shapes**: `site.seo.{url,title,description}`,
  `hero.roles[]` (falls back to `site.tagline` for the roles line), `page:home.clientTag`
  (fallback "Artistech Endeavors" for the client-work row tag). Missing optional keys render
  as empty, per CMS rules.
- **Analytics click x/y are fractions 0–1** (matches ingest validation), not pixels; sections
  come from `data-track` attributes. Beacon cookies `ari_v` (1 yr) / `ari_j` (30 min) are set
  client-side. Conversion is dispatched as `CustomEvent("ari:track", {t:"conversion"})` from
  `/contact/sent`.
- **Env**: `ARI_API_INTERNAL` (default `http://127.0.0.1:8082`), `PUBLIC_ORIGIN` (canonical
  fallback; snapshot `site.seo.url` wins). Dev ports: web 8130 (host 0.0.0.0), stub 8131.
- **Standalone output**: `.next/standalone` needs `.next/static` + `public/` copied in by the
  deploy image (per skill convention), and `HOSTNAME=0.0.0.0` at runtime, not a loopback.

## Deployment contract

- Releases from `v*` tags only. Updater checks every minute, rolls back on failed health, backups private.
- Containers: read-only rootfs, tmpfs /tmp, cap_drop ALL, no-new-privileges, non-root, init true, mem/pid limits, DOTNET_EnableDiagnostics=0.
- Proxy: existing nginx on the host, TLS LE. X-Forwarded-For overwritten with $remote_addr (never append). TRUSTED_PROXY_CIDRS set to the docker bridge + host.
