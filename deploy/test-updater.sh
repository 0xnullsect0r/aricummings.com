#!/bin/sh
# Updater test: deploy/test-updater.sh <api-image> <web-image>
# Runs the real update.sh against a throwaway stack with a LOCAL registry, so
# the actual pull path is proven. Scenarios per testing-and-parity.md.
set -u

API_IMAGE_SRC="${1:?usage: test-updater.sh <api-image> <web-image>}"
WEB_IMAGE_SRC="${2:?usage: test-updater.sh <api-image> <web-image>}"
WORK="$(mktemp -d)"
HOST_MODE="${ARI_SMOKE_NET:-bridge}"
if [ "$HOST_MODE" = "host" ]; then REG_PORT=5000; else REG_PORT=$((9500 + RANDOM % 400)); fi
STACK="$WORK/stack"

cleanup() {
  [ -f "$WORK/registry.pid" ] && kill "$(cat "$WORK/registry.pid")" >/dev/null 2>&1
  rm -rf "$WORK"
}
trap cleanup EXIT INT TERM

fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }

# Local registry, reached under a second name so the server's copy changes only
# by pulling (localhost: vs 127.0.0.1:). Host mode dodges hosts whose firewall
# blocks published ports (the CI run uses the normal bridge setup).
if [ "$HOST_MODE" = "host" ]; then
  docker run -d --rm --network host --name ari-test-registry-$$ registry:2 >/dev/null || fail "registry"
else
  docker run -d --rm -p "127.0.0.1:$REG_PORT:5000" --name ari-test-registry-$$ registry:2 >/dev/null || fail "registry"
fi
sleep 3
docker tag "$API_IMAGE_SRC" "127.0.0.1:$REG_PORT/ari-api:v1"
docker tag "$WEB_IMAGE_SRC" "127.0.0.1:$REG_PORT/ari-web:v1"
docker push "127.0.0.1:$REG_PORT/ari-api:v1" >/dev/null || fail "push api"
docker push "127.0.0.1:$REG_PORT/ari-web:v1" >/dev/null || fail "push web"
docker tag "$API_IMAGE_SRC" "localhost:$REG_PORT/ari-api:v1"
docker tag "$WEB_IMAGE_SRC" "localhost:$REG_PORT/ari-web:v1"

# Throwaway deploy folder with the real compose + updater.
mkdir -p "$STACK/secrets"
cp "$(dirname "$0")/compose.yml" "$STACK/"
cp "$(dirname "$0")/update.sh" "$STACK/"
chmod +x "$STACK/update.sh"
echo "ari-test-key:$(openssl rand -base64 32)" >"$STACK/secrets/app_key"

if [ "$HOST_MODE" = "host" ]; then
  # Host-network variant of the same topology: no published ports (the local
  # firewall blocks them), fixed off-8080 listener ports, same services.
  PUB_PORT=18180; ADM_PORT=18181; INT_PORT=18182; WEB_PORT=13000
  python3 - "$STACK/compose.yml" "$PUB_PORT" "$ADM_PORT" "$INT_PORT" "$WEB_PORT" <<'PY'
import sys
path, pub, adm, internal, webport = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]
text = open(path).read()
text = text.replace("""    ports:
      - "${HTTP_BIND:-127.0.0.1:8080}:8080"
      - "${ADMIN_BIND:-127.0.0.1:8081}:8081"   # omit for one-port mode
""", "")
text = text.replace("""    networks: [default, private]""", "    network_mode: host")
text = text.replace("""    networks: [private]""", "    network_mode: host")
text = text.replace("ARI_PUBLIC_PORT: 8080", f"ARI_PUBLIC_PORT: {pub}")
text = text.replace("ARI_INTERNAL_PORT: 8082", f"ARI_INTERNAL_PORT: {internal}")
text = text.replace("ADMIN_PORT: 8081", f"ADMIN_PORT: {adm}")
text = text.replace("ARI_WEB_ORIGIN: http://web:3000", f"ARI_WEB_ORIGIN: http://127.0.0.1:{webport}")
text = text.replace("""    environment:
      ARI_API_INTERNAL: http://api:8082""", f"""    environment:
      ARI_API_INTERNAL: http://127.0.0.1:{internal}
      PORT: {webport}""")
# drop the trailing networks section entirely in host mode
import re
text = re.sub(r"\nnetworks:\n  private:\n    internal: true\n", "\n", text)
open(path, "w").write(text)
PY
  cat >"$STACK/.env" <<EOF
API_IMAGE=localhost:$REG_PORT/ari-api:v1
WEB_IMAGE=localhost:$REG_PORT/ari-web:v1
PUBLIC_ORIGIN=http://localhost:$PUB_PORT
ADMIN_ORIGIN=http://localhost:$ADM_PORT
WEBAUTHN_RP_ID=localhost
TRUSTED_PROXY_CIDRS=
EOF
  PUBLIC_URL="http://localhost:$PUB_PORT"
else
  cat >"$STACK/.env" <<EOF
API_IMAGE=localhost:$REG_PORT/ari-api:v1
WEB_IMAGE=localhost:$REG_PORT/ari-web:v1
HTTP_BIND=127.0.0.1:$((REG_PORT + 100))
ADMIN_BIND=127.0.0.1:$((REG_PORT + 101))
PUBLIC_ORIGIN=http://localhost:$((REG_PORT + 100))
ADMIN_ORIGIN=http://localhost:$((REG_PORT + 101))
WEBAUTHN_RP_ID=localhost
TRUSTED_PROXY_CIDRS=
EOF
  PUBLIC_URL="http://localhost:$((REG_PORT + 100))"
fi

cd "$STACK" || fail "cd"

start_stack() {
  env PUBLIC_URL="$PUBLIC_URL" docker compose up -d --wait --wait-timeout 180 >/dev/null 2>&1 \
    || { docker compose logs 2>&1 | tail -30; fail "stack did not start"; }
}

# Scenario 2 setup: start from v1.
start_stack
ok "stack up from v1"

# 1. No change does nothing, no backup.
before=$(docker compose images --format '{{.ID}}' | sort)
out=$(env PUBLIC_URL="$PUBLIC_URL" sh "$STACK/update.sh" 2>&1)
after=$(docker compose images --format '{{.ID}}' | sort)
[ "$before" = "$after" ] || fail "no-change run changed images"
[ -z "$(ls "$STACK/update-backups" 2>/dev/null)" ] || fail "no-change run made a backup"
ok "1. no change does nothing"

# 2. A good release installs, backup 600, success email queued.
docker tag "$API_IMAGE_SRC" "localhost:$REG_PORT/ari-api:v2"
docker tag "$WEB_IMAGE_SRC" "localhost:$REG_PORT/ari-web:v2"
docker push "localhost:$REG_PORT/ari-api:v2" >/dev/null || fail "push v2 api"
docker push "localhost:$REG_PORT/ari-web:v2" >/dev/null || fail "push v2 web"
sed -i 's/:v1/:v2/' .env
env PUBLIC_URL="$PUBLIC_URL" sh "$STACK/update.sh" >/dev/null 2>&1 || fail "good release failed to install"
curl -fsS "$PUBLIC_URL/" >/dev/null || fail "site down after good release"
backup=$(ls "$STACK/update-backups"/*.db 2>/dev/null | head -1)
[ -n "$backup" ] || fail "no backup after update"
[ "$(stat -c '%a' "$backup")" = "600" ] || fail "backup not 600"
outbox_rows=$(docker compose exec -T api sqlite3 /data/app.db "SELECT count(*) FROM outbox WHERE payload LIKE '%Updated to release%';" 2>/dev/null)
[ "${outbox_rows:-0}" -ge 1 ] || fail "success email not queued"
ok "2. good release installs, backup 600, email queued"

# 3. One image published, not the other: nothing changes. The v3 api carries a
# different release label (rebuilt with one) so the label check can see the pair.
docker build -q -t "localhost:$REG_PORT/ari-api:v3" -f - "$WORK" <<DOCKERFILE >/dev/null
FROM $API_IMAGE_SRC
LABEL site.release=test-v3
DOCKERFILE
docker push "localhost:$REG_PORT/ari-api:v3" >/dev/null
sed -i 's/ari-api:v2/ari-api:v3/' .env
before=$(docker compose images --format '{{.ID}}' | sort)
out=$(env PUBLIC_URL="$PUBLIC_URL" sh "$STACK/update.sh" 2>&1)
after=$(docker compose images --format '{{.ID}}' | sort)
[ "$before" = "$after" ] || fail "half-published pair changed the stack"
ok "3. half-published pair waits"

# Complete the pair, then a bad release (FORCE_FAIL).
docker tag "$WEB_IMAGE_SRC" "localhost:$REG_PORT/ari-web:v3"
docker push "localhost:$REG_PORT/ari-web:v3" >/dev/null
sed -i 's/ari-web:v2/ari-web:v3/' .env
env PUBLIC_URL="$PUBLIC_URL" sh "$STACK/update.sh" >/dev/null 2>&1 || fail "v3 install failed"
curl -fsS "$PUBLIC_URL/" >/dev/null || fail "site down after v3"

# 4. Bad release rolls back, reports failure, rollback email queued.
out=$(env PUBLIC_URL="$PUBLIC_URL" FORCE_FAIL=1 sh "$STACK/update.sh" 2>&1) && fail "bad release exited 0"
curl -fsS "$PUBLIC_URL/" >/dev/null || fail "site down after failed release (rollback broken)"
rollback_mail=$(docker compose exec -T api sqlite3 /data/app.db "SELECT count(*) FROM outbox WHERE payload LIKE '%rolled back%';" 2>/dev/null)
[ "${rollback_mail:-0}" -ge 1 ] || fail "rollback email not queued"
ok "4. bad release rolls back, site up, email queued"

# 5. Same bad release not retried, no second email.
mails_before=$(docker compose exec -T api sqlite3 /data/app.db "SELECT count(*) FROM outbox WHERE payload LIKE '%rolled back%';" 2>/dev/null)
env PUBLIC_URL="$PUBLIC_URL" FORCE_FAIL=1 sh "$STACK/update.sh" >/dev/null 2>&1
mails_after=$(docker compose exec -T api sqlite3 /data/app.db "SELECT count(*) FROM outbox WHERE payload LIKE '%rolled back%';" 2>/dev/null)
[ "$mails_before" = "$mails_after" ] || fail "bad release retried"
ok "5. known-bad release not retried"

# 6. A fixed release after a bad one installs.
docker tag "$API_IMAGE_SRC" "localhost:$REG_PORT/ari-api:v4"
docker tag "$WEB_IMAGE_SRC" "localhost:$REG_PORT/ari-web:v4"
docker push "localhost:$REG_PORT/ari-api:v4" >/dev/null
docker push "localhost:$REG_PORT/ari-web:v4" >/dev/null
sed -i 's/:v3/:v4/g' .env
env PUBLIC_URL="$PUBLIC_URL" sh "$STACK/update.sh" >/dev/null 2>&1 || fail "fixed release failed"
curl -fsS "$PUBLIC_URL/" >/dev/null || fail "site down after fixed release"
ok "6. fixed release installs"

# 7. Registry outage: non-zero exit, site untouched.
docker rm -f ari-test-registry-$$ >/dev/null 2>&1
sed -i 's/:v4/:v5/g' .env
before=$(docker compose images --format '{{.ID}}' | sort)
env PUBLIC_URL="$PUBLIC_URL" sh "$STACK/update.sh" >/dev/null 2>&1 && fail "outage exited 0"
after=$(docker compose images --format '{{.ID}}' | sort)
[ "$before" = "$after" ] || fail "outage touched the stack"
curl -fsS "$PUBLIC_URL/" >/dev/null || fail "site down after outage"
ok "7. registry outage exits non-zero, site untouched"

echo "UPDATER TEST OK"
