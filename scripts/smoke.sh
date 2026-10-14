#!/usr/bin/env bash
# Local smoke test for the queue-first flow. Usage: BASE=http://localhost:5080 bash scripts/smoke.sh
# Needs the API, Azurite (queues) and the worker (`func start` in src/RelayHub.Worker) running; without the worker the
# message stays on the incoming queue and the "events" step prints a hint instead of failing.
# Logs in as the owner first (dev fallback owner@relayhub.local / relayhub-dev-owner; override with OWNER_EMAIL / OWNER_PASSWORD)
# and sends the bearer on every API call. Hooks keep using the source API key, never the bearer.
# (macOS: port 5000 belongs to AirPlay Receiver, so run the API on another port.)
set -euo pipefail
BASE="${BASE:-http://localhost:5080}"
J='Content-Type: application/json'
PY=$(command -v python3 || command -v python)   # macOS ships python3 only
echo "## health";        curl -fsS "$BASE/health"; echo
echo "## login";         LOGIN=$(curl -fsS -X POST "$BASE/api/v1/auth/login" -H "$J" -d "{\"email\":\"${OWNER_EMAIL:-owner@relayhub.local}\",\"password\":\"${OWNER_PASSWORD:-relayhub-dev-owner}\"}")
TOKEN=$(echo "$LOGIN" | "$PY" -c 'import sys,json;print(json.load(sys.stdin)["token"])')
A="Authorization: Bearer $TOKEN"
echo "$LOGIN" | "$PY" -c 'import sys,json;u=json.load(sys.stdin)["user"];print(f"signed in as {u[\"email\"]} ({u[\"role\"]}) in workspace {u[\"workspaceName\"]}")'
echo "## me";            curl -fsS "$BASE/api/v1/auth/me" -H "$A"; echo
echo "## no bearer -> 401"; curl -sS -o /dev/null -w "%{http_code}\n" "$BASE/api/v1/events"
echo "## create source"; SRC=$(curl -fsS -X POST "$BASE/api/v1/sources" -H "$J" -H "$A" -d '{"name":"smoke-form"}'); echo "$SRC"
KEY=$(echo "$SRC" | "$PY" -c 'import sys,json;print(json.load(sys.stdin)["apiKey"])')
SID=$(echo "$SRC" | "$PY" -c 'import sys,json;print(json.load(sys.stdin)["source"]["id"])')
echo "## create destination (bad URL -> 400 expected)"; curl -sS -o /dev/null -w "%{http_code}\n" -X POST "$BASE/api/v1/destinations" -H "$J" -H "$A" -d '{"name":"x","type":"discord","config":{"webhookUrl":"http://evil"}}'
RUN="smoke-$(date +%s)"   # unique per run, so the events check cannot match an older message
echo "## ingest (202 queued)"; curl -fsS -X POST "$BASE/api/v1/hooks/smoke-form" -H "$J" -H "X-Api-Key: $KEY" -H "Idempotency-Key: $RUN" -d "{\"name\":\"Anna\",\"email\":\"anna@example.com\",\"message\":\"$RUN: do you build websites?\"}"; echo
echo "## duplicate (202 queued; the worker ignores it)"; curl -fsS -X POST "$BASE/api/v1/hooks/smoke-form" -H "$J" -H "X-Api-Key: $KEY" -H "Idempotency-Key: $RUN" -d '{"name":"Anna","message":"again"}'; echo
echo "## forged key -> 401"; curl -sS -o /dev/null -w "%{http_code}\n" -X POST "$BASE/api/v1/hooks/smoke-form" -H "$J" -H "X-Api-Key: src_000000000000_0000" -d '{"name":"Mallory"}'
echo "## events (waiting up to 20 s for the worker)"
for i in $(seq 1 10); do
  EVENTS=$(curl -fsS "$BASE/api/v1/events?q=$RUN&pageSize=5" -H "$A")
  if [ "$(echo "$EVENTS" | "$PY" -c 'import sys,json;print(json.load(sys.stdin)["total"])')" != "0" ]; then echo "$EVENTS"; break; fi
  if [ "$i" = 10 ]; then echo "no event yet: is the worker running? (cd src/RelayHub.Worker && func start)"; fi
  sleep 2
done
echo "## conversations"; curl -fsS "$BASE/api/v1/conversations?q=$RUN&pageSize=5" -H "$A"; echo
echo "## stats";         curl -fsS "$BASE/api/v1/stats/summary" -H "$A"; echo
echo "## realtime negotiate (503 when SignalR is not configured)"; curl -sS -o /dev/null -w "%{http_code}\n" -X POST "$BASE/api/v1/realtime/negotiate" -H "$A"
echo "## cleanup";       curl -fsS -o /dev/null -w "%{http_code}\n" -X DELETE "$BASE/api/v1/sources/$SID" -H "$A"
