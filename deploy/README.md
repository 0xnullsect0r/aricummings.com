# Deploying aricummings.com

The stack runs as two Docker containers (api + web) behind the host nginx. The
api is the only container with ports; web sits on an internal network. Updates
arrive as `v*` tags; a systemd timer runs `update.sh` every minute.

## First setup, in order

1. **Pull token.** Create a classic GitHub token with only `read:packages`
   (fine-grained tokens are not accepted by the registry), ideally on a bot
   account. The server pulls with it.
2. **Copy the deploy files** to `/opt/aricummings/deploy` on the server. This
   folder never holds source code.
3. **Log in** as root: `docker login ghcr.io` with the pull token. The updater
   timer runs as root; a login under another user pulls nothing, quietly.
4. **Create the key**:
   ```sh
   echo "1:$(openssl rand -base64 32)" > secrets/app_key
   chown 1000 secrets/app_key && chmod 400 secrets/app_key
   ```
   Keep a copy somewhere safe and separate from the backups. Without it, saved
   credentials and encrypted values cannot be read.
5. **Fill in `.env`** (see `.env.example`). `TRUSTED_PROXY_CIDRS` is the address
   nginx connects from as the container sees it — with a proxy on the same host
   that's the Docker network gateway, which only exists after the first start:
   start once, read `docker network inspect`, set it, start again. Left empty,
   every visitor appears as the proxy and per-IP limits and geo stop working.
6. `docker compose up -d --wait`
7. **Configure nginx** from `proxy-example.conf`: `X-Forwarded-For $remote_addr`
   (never append), HSTS, `client_max_body_size 41m`, the `/api/` rate zone, TLS
   via certbot for `www.aricummings.com`, `aricummings.com`,
   `arigreenecummings.com`, `admin.aricummings.com`.
8. **Install the units and tools**:
   ```sh
   pacman -S sqlite curl   # or apt equivalents
   cp systemd/ari-update.service systemd/ari-update.timer /etc/systemd/system/
   systemctl daemon-reload && systemctl enable --now ari-update.timer
   ```
9. **Create the first owner** (prints a one-time link; there is no default
   password):
   ```sh
   docker compose exec api dotnet Ari.dll owner
   ```
   A locked-out owner resets with `docker compose exec api dotnet Ari.dll owner --reset <email>`.

## Operations

- **Release:** push a `v*` tag. CI builds, smoke-tests, updater-tests, then
  pushes both images. The timer installs within a minute and rolls back on a
  failed health check. Read the CI run yourself (`gh run watch`) before calling
  it released.
- **Pin a version:** set `API_IMAGE`/`WEB_IMAGE` in `.env` to a tag; the updater
  leaves pinned images alone.
- **Retry a failed release:** delete its line in `deploy/failed-releases`.
- **Backups:** before each update into `deploy/update-backups` (latest 5), and
  nightly at 02:45 UTC inside the app into `backups/nightly` in the `backups`
  volume (databases + media, latest 14 days). The nightly job prunes only its
  own files. Neither covers the key. Both are on this machine until an off-box
  copy is set up, so copy them and the key off it and do a restore drill.
- **Restore:** stop the timer and the stack, remove `-wal`/`-shm` files first,
  copy the database files in, start. Use the same key the backup was made with.
- **Changing the domain:** set the origins in `.env`, update nginx. Keep
  `WEBAUTHN_RP_ID` unless the parent domain changes: changing it deletes every
  passkey.

- **Shared host:** RWR-Web runs several sites. Compose names a project after its
  folder, so every stack must set a unique `COMPOSE_PROJECT_NAME`. A second folder
  called `deploy` shares this project's containers and volumes: that is how the
  aricummings launch overwrote Red Wagon's database on 2026-11-20.
- **Proxy:** Nginx Proxy Manager at 10.83.6.250 appends to `X-Forwarded-For`; the
  API trusts only the last entry, from `TRUSTED_PROXY_CIDRS=10.83.6.250/32`. NPM gets
  the real client from its stream listener by PROXY protocol; do not publish NPM's
  internal 443 directly, because it now requires that header.

## Updating this folder

`update.sh` is the most delicate file in the project. Run
`deploy/test-updater.sh` before changing it.
