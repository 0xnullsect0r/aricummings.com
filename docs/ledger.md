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

- Nothing has run on the real server. Local runs only, once they exist.
- SMTP delivery untested until credentials exist.
- Passkeys: software authenticator tests only until tried on a real phone.
- Auth area (wt/auth): options/challenge lifecycle, clone-counter rules and DB wiring
  are unit-tested, but no real authenticator has completed a WebAuthn ceremony against
  these endpoints. RP ID confirmation still open above.
- Session 30-day max / 7-day idle expiry and the 30s rotation-grace-then-theft path are
  proven by manipulating stored timestamps, not by waiting on wall clock.
- Lockout doubling is proven at the 5-failure crossing; longer lockout ladders untested.
- Owner bootstrap CLI (`ari owner <email>`) shares the tested invite path but its own
  argv parsing has not been run from a real command line.
- Outbox-mail and prune jobs are registered with the JobHost on their crons; a job tick
  firing in production has not been observed (test servers live under a minute).
- Platform defects found while building (worked around locally in Features/Auth:
  `StaffTokens`, `SealedValues`, `RateGate`; fold back once Platform is fixed):
  - `Platform/Tokens/TokenIssuer.Issue` ignores its `claims` argument — tokens carry
    no sub/sid. Fix: set `descriptor.Claims`.
  - `Platform/Crypto/Sealed` binds to the wrong AesGcm overload order for its argument
    lists and throws `Plaintext and ciphertext must have the same length` on every
    Encrypt/Decrypt. Fix: pass byte[]-overload order or explicit spans.
  - `Platform/Api/RateLimiter.TryConsume` clamps the bucket at zero
    (`Math.Max(0, tokens - 1)`), destroying the deficit, so it always returns true.
    Fix: keep the negative value and test `>= 0`.

## Deployment contract

- Releases from `v*` tags only. Updater checks every minute, rolls back on failed health, backups private.
- Containers: read-only rootfs, tmpfs /tmp, cap_drop ALL, no-new-privileges, non-root, init true, mem/pid limits, DOTNET_EnableDiagnostics=0.
- Proxy: existing nginx on the host, TLS LE. X-Forwarded-For overwritten with $remote_addr (never append). TRUSTED_PROXY_CIDRS set to the docker bridge + host.
