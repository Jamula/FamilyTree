#!/usr/bin/env bash
# Public synthetic-data HTTP smoke checks. Never create or read real family records.
set -euo pipefail
API="http://127.0.0.1:5181"
WEB="http://127.0.0.1:5182"
TMP="$(mktemp -d)"
API_PID=""
WEB_PID=""
cleanup() {
  [[ -z "$API_PID" ]] || kill "$API_PID" 2>/dev/null || true
  [[ -z "$WEB_PID" ]] || kill "$WEB_PID" 2>/dev/null || true
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
printf 'PASS HTTP smoke: health, capabilities, fail-closed people/tree, web home\n'
