#!/usr/bin/env bash
# Daily backup of the CinemaWatch database (docs/SAAS_INFRA.md §4.25), run by cinemawatch-backup.timer at 02:30 UTC.
# Installed by infra/ansible as /usr/local/sbin/cinemawatch-backup; by hand: sudo /usr/local/sbin/cinemawatch-backup
#
# 1. pg_dump of the running database, encrypted with age for the public key in the config (the server cannot decrypt).
# 2. Upload to Backblaze B2 (key with write access only: the server can add backups, never delete them; the bucket's
#    lifecycle rule removes them after 30 days).
# 3. The newest 7 encrypted copies stay in /opt/cinemawatch/backups/daily.
# 4. Healthchecks.io: /start, then success or /fail with the reason (a missing ping raises the alarm too).
#
# Config: /etc/cinemawatch/backup.env (root, 600; template infra/backup/backup.env.example).
set -Eeuo pipefail   # -E: the ERR trap below also fires inside functions

config="${CINEMAWATCH_BACKUP_CONFIG:-/etc/cinemawatch/backup.env}"   # both overridable for tests only
app_dir="${CINEMAWATCH_DIR:-/opt/cinemawatch}"
local_dir="$app_dir/backups/daily"
keep_local=7

heartbeat=""
ping() {   # ping [suffix] [message]; never fails the backup
  [ -n "$heartbeat" ] || return 0
  curl -fsS -m 10 --retry 3 -o /dev/null --data-raw "${2:-}" "$heartbeat${1:-}" || echo "warning: heartbeat ping failed" >&2
}
fail() {
  echo "ERROR: $*" >&2
  ping /fail "$*"
  exit 1
}
trap 'fail "unexpected error in line $LINENO"' ERR

[ "$(id -u)" -eq 0 ] || { echo "ERROR: run it as root (sudo)" >&2; exit 1; }
[ -f "$config" ] || { echo "ERROR: $config is missing (docs/SAAS_INFRA.md §4.25)" >&2; exit 1; }
[ "$(stat -c '%a %U' "$config")" = "600 root" ] || { echo "ERROR: $config must belong to root with mode 600" >&2; exit 1; }

# KEY=value lines only; nothing in the file is executed.
declare -A cfg=()
while IFS='=' read -r key value; do
  if [[ "$key" =~ ^[A-Z0-9_]+$ ]]; then
    cfg["$key"]="$value"
  fi
done < "$config"
heartbeat="${cfg[HEARTBEAT_URL]:-}"
for key in B2_KEY_ID B2_KEY B2_BUCKET AGE_RECIPIENT; do
  [ -n "${cfg[$key]:-}" ] || fail "$key is empty in $config"
done
[[ "${cfg[AGE_RECIPIENT]}" == age1* ]] || fail "AGE_RECIPIENT is not an age public key (age1...)"

ping /start
compose_file="$app_dir/compose.yaml"
[ -f "$compose_file" ] || fail "$compose_file is missing - nothing deployed yet?"
compose() { docker compose -f "$compose_file" "$@"; }
running="$(compose ps --status running --services 2>/dev/null || true)"
grep -qx postgres <<< "$running" || fail "the postgres container is not running"

install -d -m 0700 -o root -g root "$local_dir"
name="cinemawatch-$(date -u +%Y%m%dT%H%M%SZ).dump.age"
part="$local_dir/.$name.part"
# pipefail: a failing pg_dump fails the whole pipeline, so a half dump is never kept or uploaded.
if ! compose exec -T postgres pg_dump -U cinemawatch -d cinemawatch --format=custom \
     | age --encrypt --recipient "${cfg[AGE_RECIPIENT]}" > "$part"; then
  rm -f "$part"
  fail "pg_dump or encryption failed"
fi
[ -s "$part" ] || { rm -f "$part"; fail "the encrypted dump is empty"; }
chmod 600 "$part"
mv "$part" "$local_dir/$name"
size="$(du -h "$local_dir/$name" | cut -f1)"
echo "saved $name ($size)"

# rclone's remote is defined by environment variables: no rclone.conf with the key on disk.
# --no-check-dest: upload without listing the bucket (the key may only write).
if ! RCLONE_CONFIG_B2_TYPE=b2 RCLONE_CONFIG_B2_ACCOUNT="${cfg[B2_KEY_ID]}" RCLONE_CONFIG_B2_KEY="${cfg[B2_KEY]}" \
     rclone copyto --no-check-dest --retries 3 --low-level-retries 5 \
       "$local_dir/$name" "b2:${cfg[B2_BUCKET]}/daily/$name"; then
  fail "upload of $name to B2 failed (kept locally)"
fi
echo "uploaded to b2:${cfg[B2_BUCKET]}/daily/$name"

# Keep the newest $keep_local local copies (the names contain no spaces).
find "$local_dir" -maxdepth 1 -name 'cinemawatch-*.dump.age' -printf '%T@ %p\n' | sort -rn \
  | tail -n +$((keep_local + 1)) | cut -d' ' -f2- | xargs -r rm -f --

trap - ERR
ping "" "$name ($size) uploaded"
echo "done"
