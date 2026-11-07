#!/usr/bin/env bash
# Container test (CI): starts PostgreSQL and the web app from infra/compose/compose.yaml exactly as on the server, with
# throwaway settings, and checks the running image. No tunnel, no e-mail, no poller: nothing leaves the runner.
#
#   infra/compose/smoke-test.sh <image> <tag>      e.g. infra/compose/smoke-test.sh cinemawatch-web ci
#
# Checks: the container runs as the non-root user; it migrates an empty database and creates the owner's account;
# /healthz answers "ok"; the sign-in page renders in Polish (ICU and tzdata present, or the app would refuse to start);
# the sign-in wall redirects; behind the trusted tunnel the form cookie is Secure; no unhandled exception and no
# Kerberos probe in the log.
set -euo pipefail

image="${1:?usage: smoke-test.sh <image> <tag>}"
tag="${2:?usage: smoke-test.sh <image> <tag>}"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
project="cinemawatch-smoke"

cat > "$work/.env" <<EOF
CINEMAWATCH_IMAGE=$image
CINEMAWATCH_VERSION=$tag
POSTGRES_PASSWORD=smoketest0only0not0a0secret
SAAS_BASE_URL=https://cinemawatch.example
SAAS_OWNER_EMAIL=owner@example.test
SAAS_POLLER_ENABLED=false
SAAS_HEARTBEAT_URL=
MAIL_HOST=mail.invalid
MAIL_FROM_ADDRESS=cinemawatch@example.test
TUNNEL_TOKEN=unused-in-the-smoke-test
EOF

compose() { docker compose -f "$here/compose.yaml" --env-file "$work/.env" -p "$project" "$@"; }
logs() { compose logs --no-color web; }
cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "---- web log ----"
    logs || true
  fi
  compose down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "PASS: $*"; }

user="$(docker image inspect -f '{{.Config.User}}' "$image:$tag")"
if [ -z "$user" ] || [ "$user" = "root" ] || [ "$user" = "0" ]; then
  fail "the image runs as root (User='$user')"
fi
pass "image runs as user $user"

compose up -d postgres web

# The web container publishes no port (as on the server): reach it on the container network from the runner.
container="$(compose ps -q web)"
ip="$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$container")"
base="http://$ip:8080"

for _ in $(seq 1 60); do
  if [ "$(curl -s --max-time 2 "$base/healthz" || true)" = "ok" ]; then
    break
  fi
  if [ "$(docker inspect -f '{{.State.Running}}' "$container")" != "true" ]; then
    fail "the web container stopped"
  fi
  sleep 2
done
[ "$(curl -s --max-time 5 "$base/healthz")" = "ok" ] || fail "/healthz did not answer ok within 2 minutes"
pass "/healthz ok"

# Files, not pipes: with pipefail, "grep -q" closing a pipe early would turn a match into a failure.
logs > "$work/web.log"
grep -q "Applied [0-9]* database migration(s)" "$work/web.log" || fail "no migrations applied to the empty database"
pass "migrations applied at start-up"
grep -q "Owner account created" "$work/web.log" || fail "the owner's account was not created"
pass "owner's account created (the invitation e-mail fails on purpose: no mail server)"

headers="$work/login.headers"
body="$work/login.html"
curl -s --max-time 10 -D "$headers" -o "$body" -H "X-Forwarded-Proto: https" "$base/Account/Login"
grep -q "^HTTP/1.1 200" "$headers" || fail "sign-in page: $(head -1 "$headers")"
grep -q '<html lang="pl">' "$body" || fail "sign-in page is not Polish"
grep -q "Zaloguj się" "$body" || fail "sign-in page lacks its Polish title"
pass "sign-in page renders in Polish"
grep -iE "^set-cookie: \.AspNetCore\.Antiforgery\..*; secure" "$headers" >/dev/null || fail "form cookie is not Secure behind the trusted tunnel"
pass "form cookie Secure with X-Forwarded-Proto: https"

home="$(curl -s --max-time 10 -o /dev/null -w '%{http_code} %{redirect_url}' "$base/")"
case "$home" in
  "302 "*"/Account/Login"*) pass "sign-in wall redirects ($home)" ;;
  *) fail "home page without sign-in: $home" ;;
esac

logs > "$work/web.log"
if grep -qi "unhandled exception" "$work/web.log"; then
  fail "unhandled exception in the log"
fi
pass "no unhandled exceptions"
if grep -q "libgssapi" "$work/web.log"; then
  fail "the database driver probes Kerberos (libgssapi) - GSS Encryption Mode=Disable missing in compose.yaml?"
fi
pass "no Kerberos probe in the log"
echo "Container test passed: $image:$tag"
