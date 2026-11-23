#!/usr/bin/env bash
# Public synthetic-data HTTP smoke checks. Never create or read real family records.
set -euo pipefail
API="http://127.0.0.1:5181"
WEB="http://127.0.0.1:5182"
TMP="$(mktemp -d)"
API_PID=""
WEB_PID=""
PROVIDER_API_PID=""
cleanup() {
  [[ -z "$API_PID" ]] || kill "$API_PID" 2>/dev/null || true
  [[ -z "$WEB_PID" ]] || kill "$WEB_PID" 2>/dev/null || true
  [[ -z "$PROVIDER_API_PID" ]] || kill "$PROVIDER_API_PID" 2>/dev/null || true
  rm -rf "$TMP"
}
trap cleanup EXIT

dotnet run --project src/FamilyTree.Api/FamilyTree.Api.csproj --no-restore --no-build -c Release --urls "$API" >"$TMP/api.log" 2>&1 &
API_PID=$!
dotnet run --project src/FamilyTree.Web/FamilyTree.Web.csproj --no-restore --no-build -c Release --urls "$WEB" >"$TMP/web.log" 2>&1 &
WEB_PID=$!
ready=0
for i in $(seq 1 35); do
  if curl --silent --max-time 1 -o /dev/null "$API/health" && curl --silent --max-time 1 -o /dev/null "$WEB/"; then
    ready=1
    break
  fi
  sleep 1
done
if [[ "$ready" != 1 ]]; then
  echo "FAIL: API or web did not become ready; review job logs"
  exit 1
fi

status() {
  curl --silent --max-time 5 --output "$TMP/response.txt" --write-out '%{http_code}' "$1"
}
[[ "$(status "$API/health")" == 200 ]] || { echo "FAIL: API health"; exit 1; }
grep -q '"status":"ok"' "$TMP/response.txt" || { echo "FAIL: health JSON"; exit 1; }
[[ "$(status "$API/api/v1/capabilities")" == 200 ]] || { echo "FAIL: public capability"; exit 1; }
grep -q '"publicFamilyRecords":false' "$TMP/response.txt" || { echo "FAIL: privacy capability flag"; exit 1; }
[[ "$(status "$API/api/v1/people")" == 501 ]] || { echo "FAIL: people API must fail closed"; exit 1; }
[[ "$(status "$API/api/v1/tree")" == 501 ]] || { echo "FAIL: tree API must fail closed"; exit 1; }
[[ "$(status "$WEB/")" == 200 ]] || { echo "FAIL: home page"; exit 1; }
[[ "$(status "$WEB/demo")" == 200 ]] || { echo "FAIL: fictional explorer"; exit 1; }
grep -q 'Fictional family explorer' "$TMP/response.txt" || { echo "FAIL: demo fixture marker"; exit 1; }
grep -q 'Center on ' "$TMP/response.txt" || { echo "FAIL: accessible direct recenter links"; exit 1; }
grep -q 'Keyboard and touch navigation' "$TMP/response.txt" || { echo "FAIL: accessible navigation instructions"; exit 1; }
[[ "$(status "$WEB/demo?above=9&below=-1&social=yes")" == 200 ]] || { echo "FAIL: bounded tree options"; exit 1; }
# Security invariant: reserved private paths never trust forged identity headers
# or token-shaped text, and known/unknown IDs receive identical empty replies.
for path in /api/v1/people/123 /api/v1/people/456 /api/v1/families/123 \
    /api/v1/media/private /api/v1/search /api/v1/exports \
    /api/v1/admin/members /api/v1/invitations/accept /api/v1/mcp; do
  code="$(curl --silent --max-time 5 -o "$TMP/response.txt" -w '%{http_code}' \
    -H 'Authorization: Bearer forged' -H 'X-Forwarded-User: administrator' \
    -H 'X-Family-ID: spoof' "$API$path")"
  [[ "$code" == 501 ]] || { echo "FAIL: reserved private path exposed ($path)"; exit 1; }
  [[ ! -s "$TMP/response.txt" ]] || { echo "FAIL: private path leaked body ($path)"; exit 1; }
done
code="$(curl --silent --max-time 5 -X POST -o "$TMP/response.txt" -w '%{http_code}' \
  "$API/api/v1/people" -H 'Content-Type: application/json' --data '{"name":"fictional"}')"
[[ "$code" == 501 ]] || { echo "FAIL: unauthorized private write"; exit 1; }
[[ ! -s "$TMP/response.txt" ]] || { echo "FAIL: unauthorized write echoed body"; exit 1; }
printf 'PASS HTTP smoke: fail-closed private routes, identity spoofing and empty responses\n'

# OIDC is default-off without configured identity-provider secrets.
[[ "$(status "$API/auth/providers")" == 200 ]] || { echo "FAIL: OIDC providers endpoint"; exit 1; }
grep -q '"membershipAuthorization":false' "$TMP/response.txt" || { echo "FAIL: OIDC login cannot grant family access"; exit 1; }
[[ "$(status "$API/auth/session")" == 401 ]] || { echo "FAIL: anonymous session"; exit 1; }
[[ "$(status "$API/auth/login/google")" == 404 ]] || { echo "FAIL: unconfigured provider must not issue login challenge"; exit 1; }

# Real providers require separate owner registration. This branch tests only
# a locally configured *synthetic* provider listing, never a live OIDC login.
PROVIDER_API="http://127.0.0.1:5183"
env -u FAMILYTREE_OIDC_MICROSOFT_TENANT_ID \
    -u FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID \
    -u FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET \
    FAMILYTREE_OIDC_GOOGLE_CLIENT_ID=synthetic-test-client \
    FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET=synthetic-test-secret \
    dotnet run --project src/FamilyTree.Api/FamilyTree.Api.csproj \
      --no-restore --no-build -c Release --urls "$PROVIDER_API" \
      >"$TMP/api-provider.log" 2>&1 &
PROVIDER_API_PID=$!
provider_ready=0
for i in $(seq 1 25); do
  if curl --silent --max-time 1 -o /dev/null "$PROVIDER_API/health"; then
    provider_ready=1
    break
  fi
  sleep 1
done
[[ "$provider_ready" == 1 ]] || { echo "FAIL: synthetic provider API startup"; exit 1; }
[[ "$(status "$PROVIDER_API/auth/providers")" == 200 ]] || { echo "FAIL: configured OIDC listing"; exit 1; }
grep -q '"providers":\["google"\]' "$TMP/response.txt" || { echo "FAIL: only explicit Google scheme exposed"; exit 1; }
! grep -q 'synthetic-test-secret' "$TMP/response.txt" || { echo "FAIL: OIDC secret leaked"; exit 1; }
[[ "$(status "$PROVIDER_API/auth/session")" == 401 ]] || { echo "FAIL: configured provider cannot grant anonymous session"; exit 1; }
[[ "$(status "$PROVIDER_API/api/v1/people")" == 501 ]] || { echo "FAIL: provider registration cannot unlock private family APIs"; exit 1; }
printf 'PASS HTTP smoke: configured synthetic OIDC provider and privacy boundary\n'
