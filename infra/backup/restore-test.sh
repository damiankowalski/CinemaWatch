#!/usr/bin/env bash
# Restore test (docs/SAAS_INFRA.md §4.26): proves that a backup can be decrypted and loaded - a backup that was never
# restored is only a hope.
#
#   sudo bash restore-test.sh                   the newest local copy (/opt/cinemawatch/backups/daily)
#   sudo bash restore-test.sh --b2              the newest copy in Backblaze B2 (asks for a temporary read-only key)
#   sudo bash restore-test.sh <file.dump.age>   a given copy
#
# Asks for the age private key (from the password manager; typed in, never written to disk), restores into the scratch
# database cinemawatch_restore_test in the running PostgreSQL, compares the row count of every table with the live
# database and drops the scratch database again. The live database is only read.
set -euo pipefail

app_dir="${CINEMAWATCH_DIR:-/opt/cinemawatch}"   # both overridable for tests only
config="${CINEMAWATCH_BACKUP_CONFIG:-/etc/cinemawatch/backup.env}"
local_dir="$app_dir/backups/daily"
scratch=cinemawatch_restore_test

die() { echo "ERROR: $*" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || die "run it with sudo"
compose_file="$app_dir/compose.yaml"
[ -f "$compose_file" ] || die "$compose_file is missing"
compose() { docker compose -f "$compose_file" "$@"; }
# stdin from /dev/null: "docker compose exec" would otherwise swallow the input of the loop that calls it.
psql_in() { compose exec -T postgres psql -U cinemawatch -d "$1" -v ON_ERROR_STOP=1 -At -c "$2" < /dev/null; }
grep -qx postgres <<< "$(compose ps --status running --services 2>/dev/null || true)" || die "postgres is not running"

work="$(mktemp -d)"
chmod 700 "$work"
cleanup() {
  compose exec -T postgres dropdb -U cinemawatch --if-exists "$scratch" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

# ---- Which backup ----------------------------------------------------------------------------------------------
if [ "${1:-}" = "--b2" ]; then
  [ -f "$config" ] || die "$config is missing (the bucket name is read from it)"
  bucket="$(sed -n 's/^B2_BUCKET=//p' "$config" | head -1)"
  [ -n "$bucket" ] || die "B2_BUCKET is empty in $config"
  echo "A temporary B2 key with read access to $bucket (delete it in Backblaze after the test)."
  read -rp "  keyID: " b2_id
  read -rsp "  applicationKey (not shown): " b2_key
  echo
  export RCLONE_CONFIG_B2R_TYPE=b2 RCLONE_CONFIG_B2R_ACCOUNT="$b2_id" RCLONE_CONFIG_B2R_KEY="$b2_key"
  newest="$(rclone lsf "b2r:$bucket/daily/" --include 'cinemawatch-*.dump.age' | sort | tail -1)"
  [ -n "$newest" ] || die "no backup found in b2:$bucket/daily/"
  rclone copyto "b2r:$bucket/daily/$newest" "$work/$newest"
  unset RCLONE_CONFIG_B2R_KEY b2_key
  file="$work/$newest"
  echo "downloaded $newest from B2"
elif [ -n "${1:-}" ]; then
  file="$1"
else
  file="$(find "$local_dir" -maxdepth 1 -name 'cinemawatch-*.dump.age' -printf '%T@ %p\n' 2>/dev/null | sort -rn | head -1 | cut -d' ' -f2-)"
  [ -n "$file" ] || die "no backup in $local_dir"
fi
[ -s "$file" ] || die "$file is missing or empty"
echo "backup: $(basename "$file") ($(du -h "$file" | cut -f1))"

# ---- Decrypt and restore into the scratch database --------------------------------------------------------------
read -rsp "age private key (AGE-SECRET-KEY-1..., from the password manager; not shown): " age_key
echo
[[ "$age_key" == AGE-SECRET-KEY-1* ]] || die "that is not an age private key"

compose exec -T postgres dropdb -U cinemawatch --if-exists "$scratch"
compose exec -T postgres createdb -U cinemawatch "$scratch"
# The plain dump only streams through the pipe; the key comes from a file descriptor, never from disk.
if ! age --decrypt --identity <(printf '%s\n' "$age_key") "$file" \
     | compose exec -T postgres pg_restore -U cinemawatch -d "$scratch" --no-owner --exit-on-error; then
  unset age_key
  die "decryption or restore failed (a wrong key? the messages above say which)"
fi
unset age_key
echo "restored into $scratch"

# ---- Compare with the live database -----------------------------------------------------------------------------
tables="$(psql_in "$scratch" "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1")"
[ -n "$tables" ] || die "the restored database has no tables"
printf '\n%-32s %10s %10s\n' "table" "backup" "live now"
while read -r table; do
  backup_rows="$(psql_in "$scratch" "SELECT count(*) FROM \"$table\"")"
  live_rows="$(psql_in cinemawatch "SELECT count(*) FROM \"$table\"" 2>/dev/null || echo "-")"
  printf '%-32s %10s %10s\n' "$table" "$backup_rows" "$live_rows"
done <<< "$tables"

last_backup="$(psql_in "$scratch" 'SELECT max(migration_id) FROM "__EFMigrationsHistory"')"
last_live="$(psql_in cinemawatch 'SELECT max(migration_id) FROM "__EFMigrationsHistory"')"
echo
echo "last migration: backup $last_backup, live $last_live"
[ "$last_backup" = "$last_live" ] || echo "note: the schema changed since this backup - restoring it needs the app version of that time"
echo "PASS: the backup decrypts and restores (live counts may be higher: rows added since the backup)"
