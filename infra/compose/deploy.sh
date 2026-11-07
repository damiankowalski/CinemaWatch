#!/usr/bin/env bash
# Deploy (or roll back to) a CinemaWatch version on the server (docs/SAAS_INFRA.md §4.24).
#
#   sudo bash ~/CinemaWatch/infra/compose/deploy.sh 0.5.1
#
# 1. Checks: root, the version's form, /opt/cinemawatch/.env (root only), the tag web-v<version> in the clone.
# 2. A pg_dump of the running database to /opt/cinemawatch/backups (the newest 10 are kept).
# 3. The compose file of that very tag goes to /opt/cinemawatch/compose.yaml, the version into .env.
# 4. docker compose pull + up -d; then wait until /healthz answers "ok" and every service runs.
# Nothing is rolled back automatically: to go back, run this script with the previous version (it prints it), and if
# a database migration was the problem, restore the dump first (§4.24).
set -euo pipefail

version="${1:-}"
app_dir="${CINEMAWATCH_DIR:-/opt/cinemawatch}"   # CINEMAWATCH_DIR only for tests
repo_dir="$(cd "$(dirname "$0")/../.." && pwd)"
env_file="$app_dir/.env"
compose_file="$app_dir/compose.yaml"
backup_dir="$app_dir/backups"
keep_backups=10
services=(postgres web cloudflared)

die() { echo "ERROR: $*" >&2; exit 1; }
step() { printf '\n== %s\n' "$*"; }
compose() { docker compose --project-directory "$app_dir" -f "$compose_file" --env-file "$env_file" "$@"; }

# ---- 1. Checks -------------------------------------------------------------------------------------------------
[ -n "$version" ] || die "usage: sudo bash $0 <version>   (e.g. 0.5.1 or 0.5.1-beta.1)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-beta\.[0-9]+)?$ ]] || die "the version looks like 0.5.1 or 0.5.1-beta.1, not '$version'"
[ "$(id -u)" -eq 0 ] || die "run it with sudo"
[ -f "$env_file" ] || die "$env_file is missing - first deployment: docs/SAAS_INFRA.md §4.23"
[ "$(stat -c '%a %U' "$env_file")" = "600 root" ] || die "$env_file must belong to root with mode 600 (sudo chown root:root, sudo chmod 600)"
grep -q '^CINEMAWATCH_VERSION=' "$env_file" || die "$env_file has no CINEMAWATCH_VERSION= line"

# git runs as the clone's owner: as root it would refuse the clone ("dubious ownership") or leave root-owned files in it.
repo_owner="$(stat -c '%U' "$repo_dir")"
git_as_owner() { runuser -u "$repo_owner" -- git -C "$repo_dir" "$@"; }
tag="web-v$version"
step "Tag $tag"
git_as_owner fetch --quiet --tags origin || die "git fetch failed (network? the clone at $repo_dir?)"
git_as_owner rev-parse --quiet --verify "refs/tags/$tag^{commit}" >/dev/null || die "the tag $tag does not exist - was it pushed?"
echo "$tag = $(git_as_owner rev-parse --short "refs/tags/$tag^{commit}")"

previous=""
[ -f "$compose_file" ] && previous="$(sed -n 's/^CINEMAWATCH_VERSION=//p' "$env_file" | head -1)"
echo "running now: ${previous:-nothing}, deploying: $version"

# ---- 2. Backup of the running database --------------------------------------------------------------------------
step "Backup"
install -d -m 0700 -o root -g root "$backup_dir"
running_before=""
[ -f "$compose_file" ] && running_before="$(compose ps --status running --services 2>/dev/null || true)"
if grep -qx postgres <<< "$running_before"; then
  dump="$backup_dir/cinemawatch-$(date -u +%Y%m%dT%H%M%SZ)-before-$version.dump"
  compose exec -T postgres pg_dump -U cinemawatch -d cinemawatch --format=custom > "$dump" \
    || { rm -f "$dump"; die "pg_dump failed - nothing was changed"; }
  [ -s "$dump" ] || { rm -f "$dump"; die "pg_dump wrote nothing - nothing was changed"; }
  chmod 600 "$dump"
  echo "saved $(basename "$dump") ($(du -h "$dump" | cut -f1))"
  # Keep the newest $keep_backups dumps (the names contain no spaces).
  find "$backup_dir" -maxdepth 1 -name 'cinemawatch-*.dump' -printf '%T@ %p\n' | sort -rn \
    | tail -n +$((keep_backups + 1)) | cut -d' ' -f2- | xargs -r rm -f --
else
  echo "no running database yet - nothing to back up"
fi

# ---- 3. Compose file of the tag, version in .env ----------------------------------------------------------------
step "Compose file and version"
git_as_owner show "refs/tags/$tag:infra/compose/compose.yaml" > "$compose_file.new" \
  || { rm -f "$compose_file.new"; die "the tag $tag has no infra/compose/compose.yaml"; }
install -m 0640 -o root -g root "$compose_file.new" "$compose_file"
rm -f "$compose_file.new"
sed -i "s/^CINEMAWATCH_VERSION=.*/CINEMAWATCH_VERSION=$version/" "$env_file"
chown root:root "$env_file"
chmod 600 "$env_file"
echo "$compose_file from $tag; .env: CINEMAWATCH_VERSION=$version"

# ---- 4. Pull and start ------------------------------------------------------------------------------------------
step "Pull"
compose pull
step "Start"
compose up -d --remove-orphans

step "Health"
web="$(compose ps -q web)"
[ -n "$web" ] || die "no web container"
ip="$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$web")"
healthy=""
for _ in $(seq 1 60); do
  if [ "$(curl -s --max-time 2 "http://$ip:8080/healthz" || true)" = "ok" ]; then
    healthy=yes
    break
  fi
  sleep 2
done
if [ -z "$healthy" ]; then
  compose logs --no-color --tail 80 web || true
  die "/healthz did not answer ok within 2 minutes (log above). To go back: sudo bash $0 ${previous:-<previous version>}"
fi
echo "/healthz ok"

sleep 5   # let a service that fails at once (e.g. a wrong tunnel token) show it
running="$(compose ps --status running --services)"
for service in "${services[@]}"; do
  if ! grep -qx "$service" <<< "$running"; then
    compose logs --no-color --tail 40 "$service" || true
    die "$service is not running (log above)"
  fi
done

step "Done: $version"
compose ps
[ -n "$previous" ] && [ "$previous" != "$version" ] && echo "previous version: $previous (to go back: sudo bash $0 $previous)"
exit 0
