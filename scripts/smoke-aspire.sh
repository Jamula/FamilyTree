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

# Don't mistake unrelated services for a successful AppHost startup.
python3 - <<'PY'
import socket
for port in (5181, 5182):
    with socket.socket() as sock:
        sock.settimeout(0.6)
        if sock.connect_ex(("127.0.0.1", port)) == 0:
            raise SystemExit(f"FAIL: pre-existing listener on localhost:{port}")
PY

# The Aspire CLI may download its public CLI bundle on the first local run.
# Aspire CLI's AppHost launcher selects the Debug target path by default.
# The parent solution is built in Release by CI, so also compile the
# Debug AppHost before the one-command startup test.
dotnet build src/FamilyTree.AppHost/FamilyTree.AppHost.csproj --no-restore -c Debug >"$TMP/build.log" 2>&1 ||
  { echo "FAIL: Debug AppHost build"; exit 1; }
dotnet run --project src/FamilyTree.AppHost/FamilyTree.AppHost.csproj --no-restore -c Debug >"$TMP/aspire.log" 2>&1 &
PID=$!

ready=0
for i in $(seq 1 80); do
  if curl --fail --silent --max-time 2 "http://127.0.0.1:5181/health" -o "$TMP/api.json" &&
     curl --fail --silent --max-time 2 "http://127.0.0.1:5182/" -o /dev/null; then
    if ! kill -0 "$PID" 2>/dev/null; then
      echo "FAIL: AppHost exited even though localhost listeners responded"
      exit 1
    fi
    ready=1
    break
  fi
  if ! kill -0 "$PID" 2>/dev/null; then
    echo "FAIL: Aspire AppHost exited before services were ready"
    awk 'BEGIN{n=0} /[Uu]nhandled exception|[Ff]ailed to start|[Ff]atal error/{n=22} n>0{print; n--}' "$TMP/aspire.log" |
      sed -E 's/(token|password|secret)=([^[:space:]]+)/\1=[redacted]/g;s/[A-Za-z0-9_+=/-]{64,}/[redacted]/g' |
      tail -n 35 || true
    exit 1
  fi
  sleep 2
done
if [[ "$ready" != 1 ]]; then
  echo "FAIL: Aspire API and Web did not start within the local limit"
  awk 'BEGIN{n=0} /[Uu]nhandled exception|[Ff]ailed to start|[Ff]atal error/{n=22} n>0{print; n--}' "$TMP/aspire.log" |
      sed -E 's/(token|password|secret)=([^[:space:]]+)/\1=[redacted]/g;s/[A-Za-z0-9_+=/-]{64,}/[redacted]/g' |
      tail -n 35 || true
  exit 1
fi
grep -q '"status":"ok"' "$TMP/api.json" || { echo "FAIL: AppHost API health endpoint"; exit 1; }
echo "PASS: Aspire AppHost started API and Web on localhost, with synthetic-only health request"
