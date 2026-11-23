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
    exit 1
  fi
  sleep 2
done
[[ "$ready" == 1 ]] || { echo "FAIL: Aspire API and Web did not start within the local limit"; exit 1; }
grep -q '"status":"ok"' "$TMP/api.json" || { echo "FAIL: AppHost API health endpoint"; exit 1; }
echo "PASS: Aspire AppHost started API and Web on localhost, with synthetic-only health request"
