#!/bin/sh
# The updater. Run every minute by ari-update.timer (systemd).
# Tested by deploy/test-updater.sh; test before changing anything here.
#
# Why each part exists:
#   umask 077      backups hold site data, so they stay private
#   flock -n       the timer fires every minute; runs must not overlap
#   rollback tag   an untagged image can vanish on the containerd store, and a
#                  running container's image id cannot be tagged, so the saved
#                  NAME is what makes a rollback possible
#   label match    CI pushes one image at a time; never run a half-published pair
#   failed list    a bad release must not retry every minute while it is :latest
#   backup first   if the backup fails, the update is skipped
#   check pages    /api/health alone does not depend on web

set -u
umask 077

DEPLOY_DIR="${DEPLOY_DIR:-$(cd "$(dirname "$0")" && pwd)}"
cd "$DEPLOY_DIR" || exit 1

API_IMAGE="${API_IMAGE:-$(grep -m1 '^API_IMAGE=' .env 2>/dev/null | cut -d= -f2-)}"
WEB_IMAGE="${WEB_IMAGE:-$(grep -m1 '^WEB_IMAGE=' .env 2>/dev/null | cut -d= -f2-)}"
API_IMAGE="${API_IMAGE:-ghcr.io/0xnullsect0r/aricummings.com-api:latest}"
WEB_IMAGE="${WEB_IMAGE:-ghcr.io/0xnullsect0r/aricummings.com-web:latest}"
LOCK="${UPDATE_LOCK:-/tmp/ari-update.lock}"
FAILED_FILE="${FAILED_FILE:-$DEPLOY_DIR/failed-releases}"
BACKUP_DIR_HOST="${BACKUP_DIR_HOST:-$DEPLOY_DIR/update-backups}"
FORCE_FAIL="${FORCE_FAIL:-0}"
PUBLIC_URL="${PUBLIC_URL:?PUBLIC_URL must be set}"
ADMIN_URL="${ADMIN_URL:-$(grep -m1 '^ADMIN_ORIGIN=' .env 2>/dev/null | cut -d= -f2-)}"
ADMIN_URL="${ADMIN_URL:-$PUBLIC_URL}"

log() { logger -t ari-update "$@" 2>/dev/null || echo "$@" >&2; }

exec 9>"$LOCK" || exit 1
flock -n 9 || exit 0

die() { log "update failed: $1"; exit 1; }

label_of() {
  docker image inspect "$1" --format '{{index .Config.Labels "site.release"}}' 2>/dev/null
}

check_site() {
  # FORCE_FAIL makes the post-install check fail, so the full failure path runs:
  # record the release, roll back, verify the rollback, notify owners. The :-0
  # matters: the rollback branch unsets the variable before its own check.
  [ "${FORCE_FAIL:-0}" = "1" ] && return 1
  # health AND a real page AND the admin shell, 20 tries, 3 s apart: pages depend
  # on web, health does not, and the admin surface has its own port.
  i=0
  while [ $i -lt 20 ]; do
    if curl -fsS --max-time 10 "$PUBLIC_URL/api/health" >/dev/null 2>&1 \
       && curl -fsS --max-time 10 "$PUBLIC_URL/" >/dev/null 2>&1 \
       && curl -fsS --max-time 10 "$ADMIN_URL/" >/dev/null 2>&1; then
      return 0
    fi
    i=$((i + 1))
    sleep 3
  done
  return 1
}

backup_databases() {
  mkdir -p "$BACKUP_DIR_HOST"
  chmod 700 "$BACKUP_DIR_HOST"
  ts=$(date -u +%Y%m%dT%H%M%SZ)
  for db in app analytics; do
    docker compose exec -T api sh -c \
      "command -v sqlite3 >/dev/null 2>&1 || exit 3" || die "no sqlite3 in image for backup"
    out="$BACKUP_DIR_HOST/$db-$ts.db"
    # The backup lands inside the volume, then is verified present before the
    # copy out (older daemons can report exec done before tmpfs writes settle).
    docker compose exec -T api sqlite3 -cmd ".timeout 10000" "/data/$db.db" ".backup /data/.update-backup.db" \
      || die "backup of $db failed"
    present=0
    i=0
    while [ $i -lt 5 ]; do
      if docker compose exec -T api test -f /data/.update-backup.db; then present=1; break; fi
      i=$((i + 1)); sleep 1
    done
    [ "$present" = "1" ] || die "backup of $db did not appear"
    copied=0
    i=0
    while [ $i -lt 3 ]; do
      if docker compose cp api:/data/.update-backup.db "$out" >/dev/null 2>&1; then copied=1; break; fi
      i=$((i + 1)); sleep 1
    done
    [ "$copied" = "1" ] || die "copy of $db backup failed"
    docker compose exec -T api rm -f /data/.update-backup.db >/dev/null 2>&1 || true
    [ -s "$out" ] || die "backup of $db is empty"
    chmod 600 "$out"
  done
  # keep the latest 5
  ls -1t "$BACKUP_DIR_HOST" | tail -n +11 | while read -r f; do rm -f "$BACKUP_DIR_HOST/$f"; done
}

notify() {
  # through the site's own outbox; never allowed to fail the update
  docker compose exec -T api sqlite3 /data/app.db \
    "INSERT INTO outbox(kind, payload, next_attempt_at, created_at)
     VALUES('mail', json_quote(json_object('to','$1','subject','aricummings.com update','text','$2')),
            strftime('%s','now')*1000, strftime('%s','now')*1000);" \
    >/dev/null 2>&1 || true
}

# ---- rollback tags, before the pull can replace anything ----------------------
# A running container's image id cannot be tagged on the containerd store, and an
# untagged image can vanish, so the saved NAME is what makes rollback possible.

id_of() { docker image inspect "$1" --format '{{.ID}}' 2>/dev/null || echo none; }

for pair in "api $API_IMAGE" "web $WEB_IMAGE"; do
  set -- $pair
  running_id=$(id_of "$2")
  [ "$running_id" = "none" ] || docker tag "$running_id" "ari-rollback/$1:previous" 2>/dev/null || true
done

# ---- pull --------------------------------------------------------------------
# Compare the local ids of the referenced images, not the containers': a pull
# changes what's on disk before anything restarts.

before_ids="$(id_of "$API_IMAGE") $(id_of "$WEB_IMAGE")"
docker compose pull --quiet api web 2>/dev/null || die "registry unreachable, nothing touched"
after_ids="$(id_of "$API_IMAGE") $(id_of "$WEB_IMAGE")"
[ "$before_ids" = "$after_ids" ] && exit 0   # nothing changed

release_hash=$(printf '%s' "$after_ids" | sha256sum | cut -d' ' -f1)
grep -qx "$release_hash" "$FAILED_FILE" 2>/dev/null && { log "skipping known-bad release"; exit 0; }

api_label=$(label_of "$API_IMAGE")
web_label=$(label_of "$WEB_IMAGE")
# A missing label means an image built outside the release pipeline; treat it as
# a mismatch and wait, never run an unlabelled pair.
if [ -z "$api_label" ] || [ -z "$web_label" ] || [ "$api_label" != "$web_label" ]; then
  log "images carry different release labels ('$api_label' vs '$web_label'); waiting for the pair"
  exit 0
fi

backup_databases

# ---- install ------------------------------------------------------------------

if ! docker compose up -d --wait --wait-timeout 180 --remove-orphans >/dev/null 2>&1; then
  install_failed=1
elif ! check_site; then
  install_failed=1
fi

if [ "${install_failed:-0}" = "1" ]; then
  log "release failed; recording $release_hash and rolling back"
  echo "$release_hash" >>"$FAILED_FILE"
  docker compose logs --tail 200 >"$BACKUP_DIR_HOST/failed-$(date -u +%Y%m%dT%H%M%SZ).log" 2>/dev/null || true
  unset FORCE_FAIL   # the simulation is over; the rollback check must be real
  API_IMAGE=ari-rollback/api:previous WEB_IMAGE=ari-rollback/web:previous \
    docker compose up -d --wait --wait-timeout 180 >/dev/null 2>&1
  if check_site; then
    log "rollback to previous succeeded"
    notify "contact@aricummings.com" "Release $release_hash failed and rolled back. Logs are in $BACKUP_DIR_HOST."
  else
    log "ROLLBACK ALSO FAILED: site is down, needs a person"
    notify "contact@aricummings.com" "Release $release_hash failed AND the rollback failed. The site needs attention."
  fi
  exit 1
fi

docker image prune -f >/dev/null 2>&1 || true
log "updated to $release_hash"
notify "contact@aricummings.com" "Updated to release $release_hash."
exit 0
