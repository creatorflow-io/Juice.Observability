#!/usr/bin/env bash
# Integration checks for the local stack (roadmap Phase 1 Track A gate, requirements §4 step 7).
#   tests/verify.sh            ingestion security, tenant stamping, redaction, Grafana (when running)
#   tests/verify.sh --outage   also stops ClickHouse, sends while it is down, and checks the queued data lands after restart
# Needs: a running stack (docker compose up -d --wait), curl, openssl.
set -uo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
source .env

OTLP=https://localhost:4318
CA=secrets/tls/ca.crt
TOKEN=$(cut -d' ' -f1 secrets/otlp-tokens)
RUN=$(openssl rand -hex 8)
failures=0

pass() { echo "  ok    $1"; }
fail() { echo "  FAIL  $1"; failures=$((failures + 1)); }
check() { if [[ "$2" == "$3" ]]; then pass "$1"; else fail "$1 (expected '$3', got '$2')"; fi; }

# stdin is /dev/null: for an INSERT, clickhouse client reads more data from a non-terminal stdin and would wait forever.
ch() { docker compose exec -T clickhouse clickhouse client --user admin --password "$CLICKHOUSE_ADMIN_PASSWORD" -q "$1" </dev/null; }

# post <signal> <json> [curl args...] → HTTP status
post() {
  local signal=$1 body=$2; shift 2
  curl -s -m 30 -o /dev/null -w '%{http_code}' --ssl-no-revoke --cacert "$CA" -H 'Content-Type: application/json' "$@" \
    -d "$body" "$OTLP/v1/$signal"
}

forged='{"key":"juice.platform_tenant_id","value":{"stringValue":"forged"}},{"key":"platform.tenant.id","value":{"stringValue":"forged"}}'
resource='{"attributes":[{"key":"service.name","value":{"stringValue":"verify-'$RUN'"}},'$forged']}'
scope='{"name":"verify","attributes":['$forged']}'
now=$(date +%s)000000000
trace_id=$(openssl rand -hex 16)

traces='{"resourceSpans":[{"resource":'$resource',"scopeSpans":[{"scope":'$scope',"spans":[{
  "traceId":"'$trace_id'","spanId":"'$(openssl rand -hex 8)'","name":"verify","kind":1,
  "startTimeUnixNano":"'$now'","endTimeUnixNano":"'$now'",
  "attributes":['$forged',{"key":"db.password","value":{"stringValue":"hunter2"}},{"key":"verify.run","value":{"stringValue":"'$RUN'"}}],
  "events":[{"name":"e","timeUnixNano":"'$now'","attributes":['$forged']}]}]}]}]}'
metrics='{"resourceMetrics":[{"resource":'$resource',"scopeMetrics":[{"scope":'$scope',"metrics":[{
  "name":"verify.count","sum":{"aggregationTemporality":2,"isMonotonic":true,"dataPoints":[{
  "asInt":"1","startTimeUnixNano":"'$now'","timeUnixNano":"'$now'","attributes":['$forged']}]}}]}]}]}'
logs='{"resourceLogs":[{"resource":'$resource',"scopeLogs":[{"scope":{"name":"Juice.Verify","attributes":['$forged']},"logRecords":[{
  "timeUnixNano":"'$now'","severityNumber":9,"severityText":"Information","body":{"stringValue":"verify '$RUN'"},
  "attributes":['$forged',{"key":"Payload","value":{"stringValue":"{\"card\":\"4111\"}"}},{"key":"api_key","value":{"stringValue":"k"}}]}]}]}]}'

echo "Ingestion security"
check "plain HTTP is refused" "$(curl -s -m 30 -d '{}' http://localhost:4318/v1/traces | grep -c 'HTTP request to an HTTPS server')" "1"
check "no token → 401" "$(post traces "$traces")" "401"
check "wrong token → 401" "$(post traces "$traces" -H 'Authorization: Bearer not-a-token')" "401"
check "untrusted certificate is refused" \
  "$(curl -s -m 30 -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN" -d '{}' $OTLP/v1/traces)" "000"

echo "Authenticated ingestion (run $RUN)"
sent_ms=$(date +%s%3N)
for s in traces metrics logs; do
  body=${!s}
  check "$s accepted" "$(post $s "$body" -H "Authorization: Bearer $TOKEN")" "200"
done

# Exporter batches flush after 1s; poll until the last signal sent is queryable. The elapsed time is this run's ingestion
# freshness (send → queryable in ClickHouse), an upper bound at the polling resolution (~0.2 s plus the query time).
freshness_ms=
for _ in $(seq 1 100); do
  if [[ "$(ch "SELECT count() FROM otel.otel_logs WHERE ServiceName = 'verify-$RUN'")" == "1" ]]; then
    freshness_ms=$(( $(date +%s%3N) - sent_ms )); break
  fi
  sleep 0.2
done
echo "  info  ingestion freshness ${freshness_ms:-timeout} ms (send → queryable)"

svc="ServiceName = 'verify-$RUN'"
echo "Tenant stamping (stored records carry only the trusted tenant)"
for t in otel_traces otel_metrics_sum otel_logs; do
  scope_check=true; [[ $t == otel_traces ]] && scope_check=false   # the traces table stores no scope attributes
  check "$t: one record" "$(ch "SELECT count() FROM otel.$t WHERE $svc")" "1"
  check "$t: resource tenant = $JUICE_PLATFORM_TENANT_ID" \
    "$(ch "SELECT ResourceAttributes['juice.platform_tenant_id'] FROM otel.$t WHERE $svc")" "$JUICE_PLATFORM_TENANT_ID"
  check "$t: no platform.tenant.id on resource" \
    "$(ch "SELECT mapContains(ResourceAttributes, 'platform.tenant.id') FROM otel.$t WHERE $svc")" "0"
  $scope_check && check "$t: no tenant on scope" \
    "$(ch "SELECT length(mapFilter((k, v) -> k LIKE '%tenant%', ScopeAttributes)) FROM otel.$t WHERE $svc")" "0"
done
check "span: no tenant on record" \
  "$(ch "SELECT length(mapFilter((k, v) -> k LIKE '%tenant%', SpanAttributes)) FROM otel.otel_traces WHERE $svc")" "0"
check "span event: no tenant" \
  "$(ch "SELECT length(mapFilter((k, v) -> k LIKE '%tenant%', \`Events.Attributes\`[1])) FROM otel.otel_traces WHERE $svc")" "0"
check "datapoint: no tenant" \
  "$(ch "SELECT length(mapFilter((k, v) -> k LIKE '%tenant%', Attributes)) FROM otel.otel_metrics_sum WHERE $svc")" "0"
check "log: no tenant on record" \
  "$(ch "SELECT length(mapFilter((k, v) -> k LIKE '%tenant%', LogAttributes)) FROM otel.otel_logs WHERE $svc")" "0"

echo "Redaction"
check "span: credential attribute removed" \
  "$(ch "SELECT mapContains(SpanAttributes, 'db.password') FROM otel.otel_traces WHERE $svc")" "0"
check "span: other attributes kept" \
  "$(ch "SELECT SpanAttributes['verify.run'] FROM otel.otel_traces WHERE $svc")" "$RUN"
check "log: Juice payload removed" \
  "$(ch "SELECT mapContains(LogAttributes, 'Payload') FROM otel.otel_logs WHERE $svc")" "0"
check "log: credential attribute removed" \
  "$(ch "SELECT mapContains(LogAttributes, 'api_key') FROM otel.otel_logs WHERE $svc")" "0"

echo "Storage and access"
check "traces TTL set" "$(ch "SELECT count() FROM system.tables WHERE database='otel' AND name='otel_traces' AND create_table_query LIKE '%TTL%'")" "1"
check "grafana_reader cannot write" \
  "$(docker compose exec -T clickhouse clickhouse client --user grafana_reader --password "$CLICKHOUSE_GRAFANA_PASSWORD" \
      -q "INSERT INTO otel.otel_logs (Body) VALUES ('x')" </dev/null >/dev/null 2>&1 && echo written || echo denied)" "denied"
check "grafana_reader cannot read system users" \
  "$(docker compose exec -T clickhouse clickhouse client --user grafana_reader --password "$CLICKHOUSE_GRAFANA_PASSWORD" \
      -q "SELECT count() FROM system.users" </dev/null >/dev/null 2>&1 && echo readable || echo denied)" "denied"
# Grafana is optional (compose profile "grafana"): its checks run only when it is up.
if docker compose ps --status running --services 2>/dev/null | grep -qx grafana; then
  grafana_health=$(curl -s -m 30 -u "admin:$GRAFANA_ADMIN_PASSWORD" http://localhost:3000/api/datasources/uid/juice-clickhouse/health)
  check "Grafana ClickHouse data source healthy" "$(echo "$grafana_health" | grep -o '"status":"[A-Z]*"')" '"status":"OK"'
  check "Grafana anonymous access disabled" "$(curl -s -m 30 -o /dev/null -w '%{http_code}' http://localhost:3000/api/search)" "401"

  echo "Grafana sign-in (OIDC, Keycloak realm juice)"
  # oidc_login <user> <password> → prints "<grafana login> <org role>", or "refused"
  oidc_login() {
    local jar form action back
    jar=$(mktemp)
    # Grafana → Keycloak authorization endpoint (with state and PKCE challenge)
    form=$(curl -s -m 30 -L -c "$jar" -b "$jar" http://localhost:3000/login/generic_oauth)
    action=$(echo "$form" | grep -o 'action="[^"]*login-actions/authenticate[^"]*"' | head -1 | sed 's/^action="//; s/"$//; s/&amp;/\&/g')
    [[ -z "$action" ]] && { echo "no-login-form"; rm -f "$jar"; return; }
    # Submit credentials; Keycloak redirects back to Grafana with the code; Grafana exchanges it and sets its session.
    back=$(curl -s -m 30 -o /dev/null -w '%{redirect_url}' -c "$jar" -b "$jar" --data-urlencode "username=$1" \
      --data-urlencode "password=$2" --data-urlencode "credentialId=" "$action")
    # A failed login re-renders the Keycloak form instead of redirecting.
    [[ -z "$back" ]] && { echo "refused"; rm -f "$jar"; return; }
    curl -s -m 30 -o /dev/null -c "$jar" -b "$jar" "$back"
    local user orgs
    user=$(curl -s -m 30 -b "$jar" http://localhost:3000/api/user)
    if ! echo "$user" | grep -q '"login"'; then echo "refused"; rm -f "$jar"; return; fi
    orgs=$(curl -s -m 30 -b "$jar" http://localhost:3000/api/user/orgs)
    echo "$(echo "$user" | grep -o '"login":"[^"]*"' | cut -d'"' -f4) $(echo "$orgs" | grep -o '"role":"[^"]*"' | head -1 | cut -d'"' -f4)"
    rm -f "$jar"
  }
  check "password sign-in refused (OIDC only)" \
    "$(curl -s -m 30 -H 'Content-Type: application/json' -d "{\"user\":\"admin\",\"password\":\"$GRAFANA_ADMIN_PASSWORD\"}" \
        http://localhost:3000/login | grep -o 'auth.client.notConfigured')" "auth.client.notConfigured"
  check "alice (client role admin) → Admin" "$(oidc_login alice "$KEYCLOAK_DEMO_PASSWORD")" "alice Admin"
  check "erin (editor) → Editor" "$(oidc_login erin "$KEYCLOAK_DEMO_PASSWORD")" "erin Editor"
  check "victor (viewer) → Viewer" "$(oidc_login victor "$KEYCLOAK_DEMO_PASSWORD")" "victor Viewer"
  check "nora (no Grafana role) refused" "$(oidc_login nora "$KEYCLOAK_DEMO_PASSWORD")" "refused"
  check "wrong password refused" "$(oidc_login alice not-the-password)" "refused"
  check "OIDC admin is not Grafana server admin" \
    "$(curl -s -m 30 -u "admin:$GRAFANA_ADMIN_PASSWORD" 'http://localhost:3000/api/users/lookup?loginOrEmail=alice' | grep -o '"isGrafanaAdmin":[a-z]*')" '"isGrafanaAdmin":false'
else
  echo "Grafana not running (optional, profile grafana): Grafana checks skipped"
fi

# A Grafana token must never be a platform API token: the realm limits each client's tokens to its own roles, so the
# audience-resolve mapper does not add juice-platform (fullScopeAllowed=false and clientScopeMappings).
grafana_token_claims() {
  local jar verifier challenge form action back code
  jar=$(mktemp)
  verifier=$(openssl rand -hex 32)
  challenge=$(printf %s "$verifier" | openssl dgst -sha256 -binary | openssl base64 -A | tr '+/' '-_' | tr -d '=')
  form=$(curl -s -m 30 -c "$jar" -b "$jar" "http://localhost:8080/realms/juice/protocol/openid-connect/auth?client_id=grafana&response_type=code&scope=openid%20email%20profile&redirect_uri=http%3A%2F%2Flocalhost%3A3000%2Flogin%2Fgeneric_oauth&state=verify&code_challenge=$challenge&code_challenge_method=S256")
  action=$(echo "$form" | grep -o 'action="[^"]*login-actions/authenticate[^"]*"' | head -1 | sed 's/^action="//; s/"$//; s/&amp;/\&/g')
  back=$(curl -s -m 30 -o /dev/null -w '%{redirect_url}' -c "$jar" -b "$jar" --data-urlencode "username=$1" \
    --data-urlencode "password=$KEYCLOAK_DEMO_PASSWORD" --data-urlencode "credentialId=" "$action")
  code=$(echo "$back" | grep -o 'code=[^&]*' | cut -d= -f2)
  rm -f "$jar"
  curl -s -m 30 -d grant_type=authorization_code -d client_id=grafana -d "client_secret=$GRAFANA_OIDC_CLIENT_SECRET" \
    -d "code=$code" -d "redirect_uri=http://localhost:3000/login/generic_oauth" -d "code_verifier=$verifier" \
    http://localhost:8080/realms/juice/protocol/openid-connect/token \
    | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const p=JSON.parse(Buffer.from(JSON.parse(s).access_token.split('.')[1],'base64url'));console.log(JSON.stringify({aud:p.aud??null,clients:Object.keys(p.resource_access??{})}))})"
}
check "Grafana token has no platform audience or roles (alice holds both)" "$(grafana_token_claims alice)" '{"aud":null,"clients":["grafana"]}'

if [[ "${1:-}" == "--outage" ]]; then
  echo "Backend outage (bounded queue and retry)"
  docker compose stop clickhouse >/dev/null 2>&1
  out_run=$(openssl rand -hex 8)
  out_logs=${logs//verify-$RUN/verify-$out_run}
  check "accepted while ClickHouse is down" "$(post logs "$out_logs" -H "Authorization: Bearer $TOKEN")" "200"
  sleep 3
  queued=$(curl -s -m 30 http://localhost:8888/metrics | grep -E '^otelcol_exporter_queue_size\{.*clickhouse/logs' | awk '{print $2}')
  check "record waits in the exporter queue" "$([[ "${queued:-0}" != "0" ]] && echo queued || echo empty)" "queued"
  docker compose start clickhouse >/dev/null 2>&1
  docker compose up -d --wait clickhouse >/dev/null 2>&1
  landed=0
  for _ in $(seq 1 60); do
    landed=$(ch "SELECT count() FROM otel.otel_logs WHERE ServiceName = 'verify-$out_run'" 2>/dev/null || echo 0)
    [[ "$landed" == "1" ]] && break
    sleep 1
  done
  check "queued record lands after ClickHouse returns" "$landed" "1"
fi

echo
if (( failures )); then echo "$failures check(s) failed"; exit 1; fi
echo "all checks passed"
