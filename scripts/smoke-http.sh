#!/usr/bin/env bash
# Public synthetic-data HTTP smoke checks. Never create or read real family records.
set -euo pipefail
API="http://127.0.0.1:5181"
WEB="http://127.0.0.1:5182"
TMP="$(mktemp -d)"
API_PID=""
WEB_PID=""
PROVIDER_API_PID=""
PRIVATE_WEB_PID=""
cleanup() {
  [[ -z "$API_PID" ]] || kill "$API_PID" 2>/dev/null || true
  [[ -z "$WEB_PID" ]] || kill "$WEB_PID" 2>/dev/null || true
  [[ -z "$PROVIDER_API_PID" ]] || kill "$PROVIDER_API_PID" 2>/dev/null || true
  [[ -z "$PRIVATE_WEB_PID" ]] || kill "$PRIVATE_WEB_PID" 2>/dev/null || true
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
[[ "$(status "$WEB/private")" == 404 ]] || { echo "FAIL: private mode must be hidden by default"; exit 1; }
[[ "$(status "$WEB/demo")" == 200 ]] || { echo "FAIL: fictional explorer"; exit 1; }
grep -q 'Fictional family explorer' "$TMP/response.txt" || { echo "FAIL: demo fixture marker"; exit 1; }
grep -q 'Fictional family generation diagram' "$TMP/response.txt" || { echo "FAIL: SVG diagram absent"; exit 1; }
grep -q 'Scrollable fictional family diagram' "$TMP/response.txt" || { echo "FAIL: scrollable diagram region absent"; exit 1; }
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

# An explicitly enabled private page still fails closed on plain HTTP. It
# never contacts an HTTPS API from an insecure browser request.
PRIVATE_WEB="http://127.0.0.1:5184"
FAMILYTREE_PRIVATE_MODE=true \
FAMILYTREE_API_BASE_URL=https://localhost:5181 \
  dotnet run --project src/FamilyTree.Web/FamilyTree.Web.csproj \
    --no-restore --no-build -c Release --urls "$PRIVATE_WEB" \
    >"$TMP/web-private.log" 2>&1 &
PRIVATE_WEB_PID=$!
private_web_ready=0
for i in $(seq 1 25); do
  if curl --silent --max-time 1 -o /dev/null "$PRIVATE_WEB/health"; then
    private_web_ready=1
    break
  fi
  sleep 1
done
[[ "$private_web_ready" == 1 ]] || { echo "FAIL: opt-in private web startup"; exit 1; }
[[ "$(status "$PRIVATE_WEB/")" == 200 ]] || { echo "FAIL: private-mode landing page"; exit 1; }
grep -q 'Sign in to your private family space' "$TMP/response.txt" || { echo "FAIL: private-mode navigation"; exit 1; }
private_status="$(curl --silent --max-time 5 --dump-header "$TMP/private-headers.txt" \
  --output "$TMP/private.html" --write-out '%{http_code}' \
  --resolve localhost:5184:127.0.0.1 "http://localhost:5184/private")"
[[ "$private_status" == 200 ]] || { echo "FAIL: local private-mode page"; exit 1; }
grep -qi '^Cache-Control:.*no-store' "$TMP/private-headers.txt" || { echo "FAIL: private page cache policy"; exit 1; }
grep -q 'could not be reached securely' "$TMP/private.html" || { echo "FAIL: insecure browser request must fail closed"; exit 1; }
! grep -q 'Fictional family explorer\|Fictional 32-person demo' "$TMP/private.html" || { echo "FAIL: private page included demo data"; exit 1; }
private_wrong_host="$(curl --silent --max-time 5 --resolve public.example:5184:127.0.0.1 \
  -H 'Host: public.example' -o "$TMP/response.txt" -w '%{http_code}' \
  "http://public.example:5184/private")"
[[ "$private_wrong_host" == 404 ]] || { echo "FAIL: public Host exposed opt-in private page"; exit 1; }
printf 'PASS HTTP smoke: opt-in private page is local-only, non-cacheable and fails closed on HTTP\n'

# Local-only fictional edit -> moderator review -> link -> undo E2E.
# Never submit actual family names, user input, or credentials here.
[[ "$(status "$WEB/demo/workbench")" == 200 ]] || { echo "FAIL: fictional local workbench"; exit 1; }
grep -q 'Invented records only' "$TMP/response.txt" || { echo "FAIL: fictional-only marker"; exit 1; }
wrong_host="$(curl --silent --max-time 5 -H 'Host: familytree.public.example' \
    -o "$TMP/response.txt" -w '%{http_code}' "$WEB/demo/workbench")"
[[ "$wrong_host" == 404 ]] || { echo "FAIL: public Host exposed local workbench"; exit 1; }
bad="$(curl --silent --max-time 5 -X POST -o "$TMP/response.txt" -w '%{http_code}' \
    "$WEB/demo/workbench" -d 'action=propose-person')"
[[ "$bad" == 400 ]] || { echo "FAIL: missing anti-forgery token accepted"; exit 1; }
curl --silent --max-time 5 -c "$TMP/workbench-cookies" \
    -o "$TMP/workbench.html" "$WEB/demo/workbench"
token="$(grep -o "name='__RequestVerificationToken' value='[^']*'" \
    "$TMP/workbench.html" | head -n 1 | sed "s/.*value='//;s/'$//")"
[[ -n "$token" ]] || { echo "FAIL: anti-forgery form token missing"; exit 1; }
submit_workbench() {
  local version
  version="$(curl --silent --max-time 5 -b "$TMP/workbench-cookies" "$WEB/demo/workbench" | grep -o "name='version' value='[0-9]*'" | head -n 1 | sed "s/.*value='//;s/'$//")"
  [[ -n "$version" ]] || { echo "FAIL: missing revision token"; return 1; }
  curl --silent --max-time 5 -b "$TMP/workbench-cookies" \
    -X POST -o "$TMP/response.txt" -w '%{http_code}' \
    "$WEB/demo/workbench" \
    --data-urlencode "__RequestVerificationToken=$token" \
    --data-urlencode "action=$1" \
    --data-urlencode "version=$version"
}
[[ "$(submit_workbench approve)" == 409 ]] || { echo "FAIL: premature approval permitted"; exit 1; }
[[ "$(submit_workbench propose-person)" == 303 ]] || { echo "FAIL: propose fictional person"; exit 1; }
[[ "$(submit_workbench propose-person)" == 409 ]] || { echo "FAIL: second pending proposal allowed"; exit 1; }
[[ "$(submit_workbench approve)" == 303 ]] || { echo "FAIL: moderator approval"; exit 1; }
[[ "$(status "$WEB/demo/workbench")" == 200 ]] || { echo "FAIL: workbench view"; exit 1; }
grep -q 'Fictional Guest 1' "$TMP/response.txt" || { echo "FAIL: approved guest invisible"; exit 1; }
[[ "$(submit_workbench propose-link)" == 303 ]] || { echo "FAIL: propose synthetic relationship"; exit 1; }
[[ "$(submit_workbench approve)" == 303 ]] || { echo "FAIL: approve synthetic relationship"; exit 1; }
[[ "$(status "$WEB/demo/workbench")" == 200 ]] || { echo "FAIL: reviewed links"; exit 1; }
grep -q '1 links have passed' "$TMP/response.txt" || { echo "FAIL: approved relationship invisible"; exit 1; }
[[ "$(submit_workbench undo)" == 303 ]] || { echo "FAIL: reversible audit"; exit 1; }
[[ "$(status "$WEB/demo/workbench")" == 200 ]] || { echo "FAIL: post-undo view"; exit 1; }
grep -q '0 links have passed' "$TMP/response.txt" || { echo "FAIL: undo did not restore graph"; exit 1; }
printf 'PASS HTTP smoke: fictional workbench CSRF, errors, edit, approve, link and undo\n'
