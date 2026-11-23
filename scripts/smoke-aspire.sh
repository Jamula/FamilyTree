#!/usr/bin/env bash
# Prove one-command Aspire orchestration starts two localhost-only services.
# Never provision a container, cloud resource or private family record.
set -euo pipefail
TMP="$(mktemp -d)"
PID=""
cleanup() {
  [[ -z "$PID" ]] || { pkill -TERM -P "$PID" 2>/dev/null || true; kill "$PID" 2>/dev/null || true; }
  rm -rf "$TMP"
}
trap cleanup EXIT

# The Aspire CLI may download its public CLI bundle on the first local run.
dotnet run --project src/FamilyTree.AppHost/FamilyTree.AppHost.csproj --no-restore -c Release >"$TMP/aspire.log" 2>&1 &
PID=$!

ready=0
for i in $(seq 1 80); do
  if curl --fail --silent --max-time 2 "http://127.0.0.1:5181/health" -o "$TMP/api.json" &&
     curl --fail --silent --max-time 2 "http://127.0.0.1:5182/" -o /dev/null; then
    ready=1
    break
  fi
  if ! kill -0 "$PID" 2>/dev/null; then
    echo "FAIL: Aspire AppHost exited before services were ready"
    grep -iE "error|exception|failed|could not|not found|ASPIRE[0-9]+" "$TMP/aspire.log" | sed -E "s/[[:alnum:]_-]{36,}/[redacted]/g" | tail -n 25 || true
    exit 1
  fi
  sleep 2
done
if [[ "$ready" != 1 ]]; then
  echo "FAIL: Aspire API and Web did not start within the local limit"
  grep -iE "error|exception|failed|could not|not found|ASPIRE[0-9]+" "$TMP/aspire.log" | sed -E "s/[[:alnum:]_-]{36,}/[redacted]/g" | tail -n 25 || true
  exit 1
fi
grep -q '"status":"ok"' "$TMP/api.json" || { echo "FAIL: AppHost API health endpoint"; exit 1; }
echo "PASS: Aspire AppHost started API and Web on localhost, with synthetic-only health request"
