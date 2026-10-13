#!/usr/bin/env bash
# Local smoke test for the Week 1 slice. Usage: BASE=http://localhost:5080 bash scripts/smoke.sh
# (macOS: port 5000 belongs to AirPlay Receiver, so run the API on another port.)
set -euo pipefail
BASE="${BASE:-http://localhost:5000}"
J='Content-Type: application/json'
PY=$(command -v python3 || command -v python)   # macOS ships python3 only
echo "## health";        curl -fsS "$BASE/health"; echo
echo "## create source"; SRC=$(curl -fsS -X POST "$BASE/api/v1/sources" -H "$J" -d '{"name":"smoke-form"}'); echo "$SRC"
KEY=$(echo "$SRC" | "$PY" -c 'import sys,json;print(json.load(sys.stdin)["apiKey"])')
SID=$(echo "$SRC" | "$PY" -c 'import sys,json;print(json.load(sys.stdin)["source"]["id"])')
echo "## create destination (bad URL -> 400 expected)"; curl -sS -o /dev/null -w "%{http_code}\n" -X POST "$BASE/api/v1/destinations" -H "$J" -d '{"name":"x","type":"discord","config":{"webhookUrl":"http://evil"}}'
echo "## ingest";        curl -fsS -X POST "$BASE/api/v1/hooks/smoke-form" -H "$J" -H "X-Api-Key: $KEY" -H "Idempotency-Key: smoke-1" -d '{"name":"Anna","email":"anna@example.com","message":"Do you build websites?"}'; echo
echo "## duplicate";     curl -fsS -X POST "$BASE/api/v1/hooks/smoke-form" -H "$J" -H "X-Api-Key: $KEY" -H "Idempotency-Key: smoke-1" -d '{"name":"Anna","message":"again"}'; echo
echo "## forged key -> 401"; curl -sS -o /dev/null -w "%{http_code}\n" -X POST "$BASE/api/v1/hooks/smoke-form" -H "$J" -H "X-Api-Key: src_000000000000_0000" -d '{"name":"Mallory"}'
echo "## events";        curl -fsS "$BASE/api/v1/events?pageSize=5"; echo
echo "## cleanup";       curl -fsS -o /dev/null -w "%{http_code}\n" -X DELETE "$BASE/api/v1/sources/$SID"
