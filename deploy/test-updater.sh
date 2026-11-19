#!/bin/sh
# Updater test: deploy/test-updater.sh <api-image> <web-image>
# Runs the real update.sh against a throwaway stack with a LOCAL registry, so
# the actual pull path is proven. Every release is built as a distinct image
# (one label apart), because tagging the same image under two tags would make
# every "new release" a no-change run. Scenarios per testing-and-parity.md.
#
# ARI_SMOKE_NET=host runs everything on the host network for machines whose
# firewall blocks docker's published ports; CI uses the normal bridge setup.
set -u

API_IMAGE_SRC="${1:?usage: test-updater.sh <api-image> <web-image>}"
WEB_IMAGE_SRC="${2:?usage: test-updater.sh <api-image> <web-image>}"
WORK="$(mktemp -d)"
HOST_MODE="${ARI_SMOKE_NET:-bridge}"
if [ "$HOST_MODE" = "host" ]; then REG_PORT=5000; else REG_PORT=$(( 9500 + ( $(date +%s) + $$ ) % 400 )); fi
STACK="$WORK/stack"
REG_NAME="ari-test-registry-$$"

cleanup() {
  if [ "${ARI_KEEP_ON_FAIL:-}" = "1" ] && [ -f "$WORK/.failed" ]; then
    echo "kept failed test stack in $STACK (registry $REG_NAME)"
    return 0
  fi
  (cd "$STACK" 2>/dev/null && docker compose down --remove-orphans >/dev/null 2>&1) || true
  docker rm -f "$REG_NAME" >/dev/null 2>&1
  rm -rf "$WORK"
}
trap cleanup EXIT INT TERM

fail() { echo "FAIL: $*" >&2; touch "$WORK/.failed" 2>/dev/null; exit 1; }
ok() { echo "ok: $*"; }

# Local registry, reached under a second name so the server's copy changes only
# by pulling (localhost: vs 127.0.0.1:).
if [ "$HOST_MODE" = "host" ]; then
  docker run -d --rm --network host --name "$REG_NAME" registry:2 >/dev/null || fail "registry"
else
  docker run -d --rm -p "127.0.0.1:$REG_PORT:5000" --name "$REG_NAME" registry:2 >/dev/null || fail "registry"
fi
sleep 3

# Two genuinely distinct references to one local registry. Docker normalizes
# localhost <-> 127.0.0.1, so the second name uses another loopback address.
REG="localhost:$REG_PORT"
ALT="127.0.0.2:$REG_PORT"

# A release is both images, one label apart from every other release, pushed
# under the localhost name; the alt name exists so the stack's copy can only
# change by pulling.
release() { # release <version>
  ver="$1"
  # A previous test run may have left the stack's refs cached in the local
  # daemon (pulls persist there); scrub them so the id comparison is real.
  docker rmi "$ALT/ari-api:v$ver" "$ALT/ari-web:v$ver" >/dev/null 2>&1 || true
  docker build -q -t "$REG/ari-api:v$ver" -f - "$WORK" >/dev/null <<DOCKERFILE
FROM $API_IMAGE_SRC
LABEL site.release=test-v$ver
DOCKERFILE
  docker build -q -t "$REG/ari-web:v$ver" -f - "$WORK" >/dev/null <<DOCKERFILE
FROM $WEB_IMAGE_SRC
LABEL site.release=test-v$ver
DOCKERFILE
  docker push "$REG/ari-api:v$ver" >/dev/null || fail "push api v$ver"
  docker push "$REG/ari-web:v$ver" >/dev/null || fail "push web v$ver"
    # Deliberately NO local tag under $ALT: the stack's refs resolve only by
  # pulling, which is what makes the updater's id comparison meaningful.
  if [ "$ver" = "1" ]; then
    # The initial stack start needs the alt name in the registry too.
    docker tag "$REG/ari-api:v1" "$ALT/ari-api:v1" && docker push "$ALT/ari-api:v1" >/dev/null
    docker tag "$REG/ari-web:v1" "$ALT/ari-web:v1" && docker push "$ALT/ari-web:v1" >/dev/null
    docker rmi "$ALT/ari-api:v1" "$ALT/ari-web:v1" >/dev/null 2>&1
  fi
}

release 1

# Throwaway deploy folder with the real compose + updater.
mkdir -p "$STACK/secrets"
cp "$(dirname "$0")/compose.yml" "$STACK/"
cp "$(dirname "$0")/update.sh" "$STACK/"
chmod +x "$STACK/update.sh"
echo "ari-test-key:$(openssl rand -base64 32)" >"$STACK/secrets/app_key"

if [ "$HOST_MODE" = "host" ]; then
  # Host-network variant of the same topology: no published ports, fixed
  # off-8080 listener ports, same two services.
  PUB_PORT=18180; ADM_PORT=18181; INT_PORT=18182; WEB_PORT=13000
  python3 - "$STACK/compose.yml" "$PUB_PORT" "$ADM_PORT" "$INT_PORT" "$WEB_PORT" <<'PY'
import sys, re
path, pub, adm, internal, webport = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]
text = open(path).read()
text = text.replace("""    ports:
      - "${HTTP_BIND:-127.0.0.1:8080}:8080"
      - "${ADMIN_BIND:-127.0.0.1:8081}:8081"   # omit for one-port mode
""", "")
text = text.replace("    networks: [default, private]", "    network_mode: host")
text = text.replace("    networks: [private]", "    network_mode: host")
text = text.replace("ARI_PUBLIC_PORT: 8080", f"ARI_PUBLIC_PORT: {pub}")
text = text.replace("ARI_INTERNAL_PORT: 8082", f"ARI_INTERNAL_PORT: {internal}")
text = text.replace("ADMIN_PORT: 8081", f"ADMIN_PORT: {adm}")
text = text.replace("ARI_WEB_ORIGIN: http://web:3000", f"ARI_WEB_ORIGIN: http://127.0.0.1:{webport}")
text = text.replace("      ARI_API_INTERNAL: http://api:8082", f"      ARI_API_INTERNAL: http://127.0.0.1:{internal}\n      PORT: {webport}")
text = re.sub(r"\nnetworks:\n  private:\n    internal: true\n", "\n", text)
open(path, "w").write(text)
PY
  cat >"$STACK/.env" <<EOF
API_IMAGE=$ALT/ari-api:v1
WEB_IMAGE=$ALT/ari-web:v1
PUBLIC_ORIGIN=http://localhost:$PUB_PORT
ADMIN_ORIGIN=http://localhost:$ADM_PORT
WEBAUTHN_RP_ID=localhost
TRUSTED_PROXY_CIDRS=
EOF
  PUBLIC_URL="http://localhost:$PUB_PORT"
else
  cat >"$STACK/.env" <<EOF
API_IMAGE=$ALT/ari-api:v1
WEB_IMAGE=$ALT/ari-web:v1
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

# Unique project per run: leftover same-name projects from crashed runs would
# otherwise collide with this one (wrong container addressed by compose exec/cp).
export COMPOSE_PROJECT_NAME="ariupd$$"

# ARI_UPDATE_TRACE=1 runs update.sh with -x for debugging this test.
UPDATE_SH="sh"
[ "${ARI_UPDATE_TRACE:-}" = "1" ] && UPDATE_SH="sh -x"

start_stack() {
  env PUBLIC_URL="$PUBLIC_URL" docker compose up -d --wait --wait-timeout 180 >/dev/null 2>&1 \
    || { docker compose logs 2>&1 | tail -30; fail "stack did not start"; }
}

# Scenario 2 setup: start from v1 (pulled through the alt name).
start_stack
ok "stack up from v1"

# 1. No change does nothing, no backup.
before=$(docker compose images --format '{{.ID}}' | sort)
env PUBLIC_URL="$PUBLIC_URL" $UPDATE_SH "$STACK/update.sh" >/dev/null 2>&1 || fail "no-change run exited non-zero"
after=$(docker compose images --format '{{.ID}}' | sort)
[ "$before" = "$after" ] || fail "no-change run changed images"
[ -z "$(ls "$STACK/update-backups" 2>/dev/null)" ] || fail "no-change run made a backup"
ok "1. no change does nothing"

# 2. A good release installs, backup 600, success email queued.
release 2
sed -i 's/:v1/:v2/g' .env
[ -n "${ARI_UPDATE_TRACE:-}" ] && TRACE_OUT=/dev/stderr || TRACE_OUT=/dev/null
env PUBLIC_URL="$PUBLIC_URL" $UPDATE_SH "$STACK/update.sh" >"$TRACE_OUT" 2>&1 || fail "good release failed to install"
curl -fsS "$PUBLIC_URL/" >/dev/null || fail "site down after good release"
backup=$(ls "$STACK/update-backups"/*.db 2>/dev/null | head -1)
[ -n "$backup" ] || fail "no backup after update"
[ "$(stat -c '%a' "$backup")" = "600" ] || fail "backup not 600"
outbox_rows=$(docker compose exec -T api sqlite3 /data/app.db "SELECT count(*) FROM outbox WHERE payload LIKE '%Updated to release%';" 2>/dev/null)
[ "${outbox_rows:-0}" -ge 1 ] || fail "success email not queued"
ok "2. good release installs, backup 600, email queued"

# 3. One image published, not the other: nothing changes.
docker build -q -t "$REG/ari-api:v3" -f - "$WORK" >/dev/null <<DOCKERFILE
FROM $API_IMAGE_SRC
LABEL site.release=test-v3
DOCKERFILE
docker push "$REG/ari-api:v3" >/dev/null
sed -i 's/ari-api:v2/ari-api:v3/' .env
before=$(docker compose images --format '{{.ID}}' | sort)
env PUBLIC_URL="$PUBLIC_URL" $UPDATE_SH "$STACK/update.sh" >/dev/null 2>&1
after=$(docker compose images --format '{{.ID}}' | sort)
[ "$before" = "$after" ] || fail "half-published pair changed the stack"
ok "3. half-published pair waits"

# Complete the pair, install v3.
docker build -q -t "$REG/ari-web:v3" -f - "$WORK" >/dev/null <<DOCKERFILE
FROM $WEB_IMAGE_SRC
LABEL site.release=test-v3
DOCKERFILE
docker push "$REG/ari-web:v3" >/dev/null
sed -i 's/ari-web:v2/ari-web:v3/' .env
env PUBLIC_URL="$PUBLIC_URL" $UPDATE_SH "$STACK/update.sh" >/dev/null 2>&1 || fail "v3 install failed"
curl -fsS "$PUBLIC_URL/" >/dev/null || fail "site down after v3"

# 4. A bad release (v4 pair published, install forced to fail) rolls back.
release 4
sed -i 's/:v3/:v4/g' .env
env PUBLIC_URL="$PUBLIC_URL" FORCE_FAIL=1 $UPDATE_SH "$STACK/update.sh" >/dev/null 2>&1 && fail "bad release exited 0"
curl -fsS "$PUBLIC_URL/" >/dev/null || fail "site down after failed release (rollback broken)"
rollback_mail=$(docker compose exec -T api sqlite3 /data/app.db "SELECT count(*) FROM outbox WHERE payload LIKE '%rolled back%';" 2>/dev/null)
[ "${rollback_mail:-0}" -ge 1 ] || fail "rollback email not queued"
ok "4. bad release rolls back, site up, email queued"

# 5. Same bad release not retried, no second email.
mails_before=$(docker compose exec -T api sqlite3 /data/app.db "SELECT count(*) FROM outbox WHERE payload LIKE '%rolled back%';" 2>/dev/null)
env PUBLIC_URL="$PUBLIC_URL" FORCE_FAIL=1 $UPDATE_SH "$STACK/update.sh" >/dev/null 2>&1
mails_after=$(docker compose exec -T api sqlite3 /data/app.db "SELECT count(*) FROM outbox WHERE payload LIKE '%rolled back%';" 2>/dev/null)
[ "$mails_before" = "$mails_after" ] || fail "bad release retried"
ok "5. known-bad release not retried"

# 6. A fixed release after a bad one installs.
release 5
sed -i 's/:v4/:v5/g' .env
env PUBLIC_URL="$PUBLIC_URL" $UPDATE_SH "$STACK/update.sh" >/dev/null 2>&1 || fail "fixed release failed"
curl -fsS "$PUBLIC_URL/" >/dev/null || fail "site down after fixed release"
ok "6. fixed release installs"

# 7. Registry outage: non-zero exit, site untouched.
docker rm -f "$REG_NAME" >/dev/null 2>&1
sed -i 's/:v5/:v6/g' .env
before=$(docker compose images --format '{{.ID}}' | sort)
env PUBLIC_URL="$PUBLIC_URL" $UPDATE_SH "$STACK/update.sh" >/dev/null 2>&1 && fail "outage exited 0"
after=$(docker compose images --format '{{.ID}}' | sort)
[ "$before" = "$after" ] || fail "outage touched the stack"
curl -fsS "$PUBLIC_URL/" >/dev/null || fail "site down after outage"
ok "7. registry outage exits non-zero, site untouched"

echo "UPDATER TEST OK"
