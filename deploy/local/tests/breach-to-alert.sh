#!/usr/bin/env bash
# Breach-to-alert through the running platform (requirements §9: for a 1-minute rule schedule, p95 qualifying breach
# to alert ≤ 2 minutes after ForDuration). Needs the stack and the API (scripts/run-api.sh) with deploy/local/rules.json.
# Sends a failing service's spans every 5 s and polls the API until rule service-error-rate fires for it.
#   tests/breach-to-alert.sh [runs]     default 1; each run uses a new service
# It sends to the main Collector on purpose: the alert comes from the running API, which reads only that tenant. So each
# run leaves a breach-<id> service and its alert in the Console.
set -uo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
source .env

RUNS=${1:-1}
API=http://localhost:5180/api/v1
OTLP=https://localhost:4318
TOKEN=$(cut -d' ' -f1 secrets/otlp-tokens)
FOR_DURATION=120   # rules.json service-error-rate

access_token() {
  curl -s -m 30 -d grant_type=password -d client_id=juice-platform-cli -d username=victor -d "password=$KEYCLOAK_DEMO_PASSWORD" \
    http://localhost:8080/realms/juice/protocol/openid-connect/token | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).access_token))"
}

send_failures() {   # 30 failing server spans, now
  local service=$1 now spans=""
  now=$(date +%s)000000000
  for _ in $(seq 1 30); do
    spans+='{"traceId":"'$(openssl rand -hex 16)'","spanId":"'$(openssl rand -hex 8)'","name":"GET /orders","kind":2,"startTimeUnixNano":"'$now'","endTimeUnixNano":"'$now'","status":{"code":2}},'
  done
  curl -s -m 30 -o /dev/null --ssl-no-revoke --cacert secrets/tls/ca.crt -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"'$service'"}}]},"scopeSpans":[{"spans":['"${spans%,}"']}]}]}' \
    "$OTLP/v1/traces"
}

results=()
for run in $(seq 1 "$RUNS"); do
  service=breach-$(openssl rand -hex 4)
  bearer=$(access_token)
  tenant=$(curl -s -m 30 -H "Authorization: Bearer $bearer" "$API/tenants" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).items[0].id))")
  start=$(date +%s)
  fired=
  while [[ -z "$fired" ]] && (( $(date +%s) - start < 420 )); do
    send_failures "$service"
    sleep 5
    bearer=$( (( ($(date +%s) - start) % 240 < 5 )) && access_token || echo "$bearer")
    fired=$(curl -s -m 30 -H "Authorization: Bearer $bearer" "$API/tenants/$tenant/alerts?state=firing&rule=service-error-rate&limit=200" \
      | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const a=JSON.parse(s).items.find(i=>i.subjectKey==='service:$service@unspecified');console.log(a?'yes':'')})")
  done
  if [[ -z "$fired" ]]; then
    echo "run $run ($service): did not fire within 7 minutes"
    results+=(9999)
    continue
  fi
  elapsed=$(( $(date +%s) - start ))
  after=$(( elapsed - FOR_DURATION ))
  echo "run $run ($service): firing seen ${elapsed} s after the breach started, ${after} s after ForDuration (target ≤ 120 s)"
  results+=("$after")
done
sorted=($(printf '%s\n' "${results[@]}" | sort -n))
p95=${sorted[$(( (${#sorted[@]} * 95 + 99) / 100 - 1 ))]}
echo "breach-to-alert after ForDuration: p95 ${p95} s over ${#sorted[@]} runs (target ≤ 120 s; seen by polling every 5 s)"
(( p95 <= 120 ))
