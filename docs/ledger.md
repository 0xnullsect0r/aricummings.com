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

## Proven (2026-11-19, backbone commit 0a1c0d2)

- Edge rules against the real server on real sockets, twice: curl smoke and xUnit
  (`apps/api.tests/EdgeTests.cs`, 7/7): exact /healthz, admin-path refusal on the public
  surface (raw + decoded spellings), internal-path refusal, security headers on every
  response, admin noindex + robots, /api/health, 502 when web is down.
- Security headers reach Kestrel's own malformed-request rejections via the transport
  wrapper (verified with a raw malformed request).
- Local runs need the user-local .NET in `~/.dotnet` (SDK 10.0.113 + ASP.NET 10.0.13):
  run `~/.dotnet/dotnet test apps/Ari.slnx`. The system muxer lacks ASP.NET.

## Deployment contract

- Releases from `v*` tags only. Updater checks every minute, rolls back on failed health, backups private.
- Containers: read-only rootfs, tmpfs /tmp, cap_drop ALL, no-new-privileges, non-root, init true, mem/pid limits, DOTNET_EnableDiagnostics=0.
- Proxy: existing nginx on the host, TLS LE. X-Forwarded-For overwritten with $remote_addr (never append). TRUSTED_PROXY_CIDRS set to the docker bridge + host.
