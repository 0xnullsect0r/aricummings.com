#!/bin/sh
# Container smoke test: deploy/smoke-test.sh <api-image> <web-image>
# Boots both images the way production does and checks what only shows in
# containers. Exits non-zero on the first failure.
set -u

API_IMAGE="${1:?usage: smoke-test.sh <api-image> <web-image>}"
WEB_IMAGE="${2:?usage: smoke-test.sh <api-image> <web-image>}"
# ARI_SMOKE_NET=host runs both containers on the host network. Use it where the
# host firewall blocks the docker bridge's published ports (firewalld/Garuda).
# Bridge mode is the real one (CI); host mode cannot verify port isolation.
HOST_MODE="${ARI_SMOKE_NET:-bridge}"
NET="ari-smoke-$$"
if [ "$HOST_MODE" = "host" ]; then
  # Off the standard ports: this machine may already run the old site on 8080.
  PUB=18080; ADM=18081; INT=18082; WEBPORT=13000
else
  # POSIX sh has no $RANDOM; derive the scratch port from time and pid.
  PUB=$(( 9000 + ( $(date +%s) + $$ ) % 500 )); ADM=$((PUB + 1)); INT=8082; WEBPORT=3000
fi
WORK="$(mktemp -d)"
DATA="$WORK/data"
mkdir -p "$DATA/media" "$WORK/backups"
cleanup() {
  docker rm -f ari-smoke-api ari-smoke-web >/dev/null 2>&1
  [ "$HOST_MODE" = "host" ] || docker network rm "$NET" >/dev/null 2>&1
  rm -rf "$WORK"
}
trap cleanup EXIT INT TERM

fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }

if [ "$HOST_MODE" = "host" ]; then
  # Containers listen directly on the host stack: their listen ports ARE the
  # host ports, chosen off 8080 (the old site may still hold it locally).
  WEB_NET="--network host"
  WEB_INTERNAL="http://127.0.0.1:$INT"
  WEB_PORT_ENV="-e PORT=$WEBPORT"
  API_NET="--network host"
  API_WEB="http://127.0.0.1:$WEBPORT"
  API_PORTS=""
  API_PUB_PORT=$PUB; API_ADM_PORT=$ADM; API_INT_PORT=$INT
else
  # Containers always listen on their fixed internal ports; -p maps them out.
  WEB_NET="--network $NET"
  WEB_INTERNAL="http://ari-smoke-api:8082"
  WEB_PORT_ENV=""
  API_NET="--network $NET"
  API_WEB="http://ari-smoke-web:3000"
  API_PORTS="-p 127.0.0.1:$PUB:8080 -p 127.0.0.1:$ADM:8081"
  API_PUB_PORT=8080; API_ADM_PORT=8081; API_INT_PORT=8082
  docker network create --internal "$NET" >/dev/null || fail "network"
fi
# shellcheck disable=SC2086
# No --rm: the web-down and SIGTERM checks stop and restart this container.
docker run -d --name ari-smoke-web \
  $WEB_NET \
  --init --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  -e ARI_API_INTERNAL="$WEB_INTERNAL" $WEB_PORT_ENV \
  "$WEB_IMAGE" >"$WORK/web-run.log" 2>&1 || { cat "$WORK/web-run.log"; fail "web did not start"; }

echo "ari-smoke-key:$(openssl rand -base64 32)" >"$WORK/key"

# shellcheck disable=SC2086
# No --rm: a boot crash must leave the container inspectable for its logs.
docker run -d --name ari-smoke-api \
  $API_NET $API_PORTS \
  --init --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  -e ARI_ENV=production -e ARI_DATA_DIR=/data -e ARI_BACKUP_DIR=/backups \
  -e ARI_PUBLIC_PORT=$API_PUB_PORT -e ARI_INTERNAL_PORT=$API_INT_PORT \
  -e ADMIN_PORT=$API_ADM_PORT \
  -e ARI_WEB_ORIGIN="$API_WEB" \
  -e PUBLIC_ORIGIN=http://localhost:$PUB -e ADMIN_ORIGIN=http://localhost:$ADM \
  -e APP_KEY_FILE=/run/secrets/key \
  --mount type=bind,src="$DATA",dst=/data \
  --mount type=bind,src="$WORK/backups",dst=/backups \
  -v "$WORK/key":/run/secrets/key:ro \
  "$API_IMAGE" >"$WORK/api-run.log" 2>&1 || { cat "$WORK/api-run.log"; fail "api did not start"; }
if ! docker ps --format '{{.Names}}' | grep -qx ari-smoke-api; then
  docker logs ari-smoke-api >"$WORK/api-logs.txt" 2>&1 || true
  tail -20 "$WORK/api-run.log" "$WORK/api-logs.txt" 2>/dev/null
  fail "api container is not running"
fi

B="http://127.0.0.1:$PUB"
A="http://127.0.0.1:$ADM"

wait_up() {
  i=0
  while [ $i -lt 40 ]; do
    curl -fsS --max-time 5 "$B/api/health" >/dev/null 2>&1 && return 0
    i=$((i + 1)); sleep 3
  done
  return 1
}
wait_up || { docker logs ari-smoke-api 2>&1 | tail -30; fail "api never became healthy"; }
ok "api healthy"

# 1. No key means refuse to start.
docker rm -f ari-nokey >/dev/null 2>&1
# shellcheck disable=SC2086
# No --rm here: the container must stay inspectable after it refuses.
docker run -d --name ari-nokey $API_NET \
  -e ARI_ENV=production -e ARI_DATA_DIR=/tmp \
  -e ARI_PUBLIC_PORT=28080 -e ARI_INTERNAL_PORT=28082 \
  -e PUBLIC_ORIGIN=http://x -e ADMIN_ORIGIN=http://y \
  "$API_IMAGE" >/dev/null
sleep 8
docker inspect -f '{{.State.Status}}' ari-nokey 2>/dev/null | grep -qx exited \
  || fail "api started without a key"
docker rm -f ari-nokey >/dev/null 2>&1
ok "no key refuses to boot"

# 2. Port discipline: web publishes nothing. (Bridge mode only: host mode shares
#    the host stack by design, so isolation is proven in CI instead.)
if [ "$HOST_MODE" != "host" ]; then
  web_ports=$(docker inspect -f '{{json .NetworkSettings.Ports}}' ari-smoke-web)
  [ "$web_ports" = "{}" ] || fail "web published ports: $web_ports"
  ok "web publishes no ports"
else
  ok "web port isolation: skipped in host mode (CI runs bridge mode)"
fi

# 3. Internal paths on every published port.
[ "$(curl -s -o /dev/null -w '%{http_code}' "$B/internal/content")" = "404" ] || fail "internal on public"
[ "$(curl -s -o /dev/null -w '%{http_code}' "$A/internal/content")" = "404" ] || fail "internal on admin"
ok "internal paths refused"

# 4. healthz spellings.
[ "$(curl -s -o /dev/null -w '%{http_code}' "$B/healthz")" = "200" ] || fail "healthz"
for path in "/healthz/" "/healthz?x=1" "/HEALTHZ"; do
  [ "$(curl -s -o /dev/null -w '%{http_code}' "$B$path")" = "404" ] || fail "healthz spelling $path"
done
[ "$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "$B/%68ealthz")" = "404" ] || fail "encoded healthz"
ok "healthz exact only"

# 5. Pages: known 200, unknown 404.
[ "$(curl -s -o /dev/null -w '%{http_code}' "$B/")" = "200" ] || fail "home not 200"
[ "$(curl -s -o /dev/null -w '%{http_code}' "$B/no-such-page")" = "404" ] || fail "unknown not 404"
[ "$(curl -s -o /dev/null -w '%{http_code}' "$B/web.config")" = "404" ] || fail "web.config not 404"
ok "pages"

# 6. Security headers + CSP on a page, and on a malformed request's 400.
curl -fsS "$B/" | grep -q "Content-Security-Policy" && ok "CSP present (header check below)"
headers=$(curl -sI "$B/")
echo "$headers" | grep -qi "content-security-policy" || fail "no CSP header"
echo "$headers" | grep -qi "x-content-type-options" || fail "no nosniff"
printf 'GET /%%FF HTTP/1.1\r\nHost: x\r\n\r\n' | timeout 5 nc 127.0.0.1 "$PUB" 2>/dev/null | grep -qi "x-content-type-options" \
  || fail "malformed-request 400 lacks headers"
ok "headers incl. malformed request"

# 7. Admin surface.
got=$(curl -s -o /dev/null -w '%{http_code}' "$A/")
[ "$got" = "200" ] || { docker logs ari-smoke-api 2>&1 | tail -5; fail "admin shell (got $got)"; }
[ "$(curl -sI "$A/" | grep -ci 'x-robots-tag: noindex')" = "1" ] || fail "admin noindex"
got=$(curl -s -o /dev/null -w '%{http_code}' "$A/api/admin/users")
[ "$got" = "401" ] || fail "admin API not guarded (got $got)"
got=$(curl -s -o /dev/null -w '%{http_code}' "$A/api/contact")
[ "$got" = "404" ] || fail "public API on admin (got $got)"
got=$(curl -s -o /dev/null -w '%{http_code}' "$A/api/analytics/beacon")
[ "$got" = "404" ] || fail "public beacon API on admin (got $got)"
ok "admin surface"

# 8. Public port refuses admin spellings.
for path in /admin /admin/x /ADMIN /%61dmin //admin /x/../admin /api/admin/users /api/auth/me; do
  [ "$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "$B$path")" = "404" ] || fail "public accepted $path"
done
ok "public refuses admin spellings"

# 9. web down => 502 fast, api healthy.
docker stop ari-smoke-web >/dev/null
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$B/")
[ "$code" = "502" ] || fail "web-down gave $code, not 502"
curl -fsS "$B/api/health" >/dev/null || fail "api unhealthy while web down"
docker start ari-smoke-web >/dev/null
ok "web-down 502, api stays healthy"

# 10. SIGTERM under 10 s, exit 0.
start=$(date +%s)
docker stop -t 30 ari-smoke-web >/dev/null
elapsed=$(( $(date +%s) - start ))
[ "$elapsed" -lt 10 ] || fail "web took ${elapsed}s to stop"
ok "web SIGTERM fast"

echo "SMOKE OK"
