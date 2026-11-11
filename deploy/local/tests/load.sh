#!/usr/bin/env bash
# Synthetic load at the requirements §9 reference rates, with an ingestion-freshness probe running alongside.
#   tests/load.sh [duration]     default 120s; the release acceptance run uses 30m
# Load: 1,000 spans/s, 10,000 metric points/s, 1,000 log records/s from telemetrygen over authenticated TLS OTLP.
# This exercises the Collector → ClickHouse path only; it is not evidence of Juice export (see tests/verify.sh and the
# sample for that). One resource per signal, so it does not model 10 services / 100 instances.
# Needs docker compose --profile test: the load goes through the test Collector, the main one's configuration with the
# tenant local-test, so it stays out of the tenant the running API works on.
set -uo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
source .env

docker compose --profile test ps --status running --services 2>/dev/null | grep -qx otel-collector-test \
  || { echo "the test Collector is not running: docker compose --profile test up -d --wait"; exit 1; }

DURATION=${1:-120s}
IMAGE=ghcr.io/open-telemetry/opentelemetry-collector-contrib/telemetrygen:v0.163.0
TOKEN=$(cut -d' ' -f1 secrets/otlp-tokens)
RUN=$(openssl rand -hex 4)
SERVICE=load-$RUN
NETWORK=${COMPOSE_PROJECT_NAME}_default

ch() { docker compose exec -T clickhouse clickhouse client --user admin --password "$CLICKHOUSE_ADMIN_PASSWORD" -q "$1" </dev/null; }
collector_metric() {   # sum of a Collector self-metric across its label sets
  curl -s http://localhost:18888/metrics | awk -v m="$1" 'index($0, m) == 1 { s += $NF } END { printf "%d", s }'
}

gen() {   # gen <signal> <workers> <rate per worker> [extra args...]
  local signal=$1 workers=$2 rate=$3; shift 3
  # Labelled with the run, so a caller (tests/streaming-benchmark.sh) can stop this run's generators and no one else's.
  docker run --rm -d --label "juice.load=${LOAD_LABEL:-$RUN}" --network "$NETWORK" -v "$(pwd -W 2>/dev/null || pwd)/secrets/tls/ca.crt:/ca.crt:ro" "$IMAGE" "$signal" \
    --otlp-endpoint otel-collector-test:4317 --ca-cert /ca.crt --otlp-header "Authorization=\"Bearer $TOKEN\"" \
    --service "$SERVICE" --workers "$workers" --rate "$rate" --duration "$DURATION" "$@"
}

before_refused=$(collector_metric otelcol_receiver_refused_)
before_enqueue_failed=$(collector_metric otelcol_exporter_enqueue_failed_)
before_send_failed=$(collector_metric otelcol_exporter_send_failed_)

echo "load run $RUN for $DURATION: 1,000 spans/s, 10,000 metric points/s, 1,000 logs/s (service $SERVICE)"
ids=(
  "$(gen traces 5 200 --child-spans 1)"     # 5 workers × 200 spans/s (--rate counts spans; root + 1 child per trace)
  "$(gen metrics 10 1000)"                  # 10 workers × 1,000 points/s
  "$(gen logs 5 200)"                       # 5 workers × 200 logs/s
)

# Freshness probe: every 10 s send one log record and time it until it is queryable in ClickHouse.
samples=()
probe() {
  local id=$1 now sent
  now=$(date +%s)000000000
  sent=$(date +%s%3N)
  curl -s -o /dev/null --ssl-no-revoke --cacert secrets/tls/ca.crt -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"probe-'$RUN'"}}]},"scopeLogs":[{"logRecords":[{"timeUnixNano":"'$now'","body":{"stringValue":"'$id'"}}]}]}]}' \
    https://localhost:14318/v1/logs
  for _ in $(seq 1 300); do
    if [[ "$(ch "SELECT count() FROM otel.otel_logs WHERE ServiceName = 'probe-$RUN' AND Body = '$id'")" == "1" ]]; then
      echo $(( $(date +%s%3N) - sent )); return
    fi
    sleep 0.2
  done
  echo 60000
}

sleep 10   # let the generators reach their rate
i=0
while docker ps -q --no-trunc | grep -q "${ids[2]}"; do
  ms=$(probe "p$i"); samples+=("$ms"); i=$((i + 1))
  echo "  probe $i: ${ms} ms"
  sleep 10
done
for id in "${ids[@]}"; do docker wait "$id" >/dev/null 2>&1; done
sleep 5

sorted=($(printf '%s\n' "${samples[@]}" | sort -n))
n=${#sorted[@]}
p95=${sorted[$(( (n * 95 + 99) / 100 - 1 ))]}
echo
echo "freshness: $n probes, p50 ${sorted[$(( (n - 1) / 2 ))]} ms, p95 $p95 ms, max ${sorted[$((n - 1))]} ms (target p95 ≤ 30000 ms)"
echo "stored:    spans $(ch "SELECT count() FROM otel.otel_traces WHERE ServiceName = '$SERVICE'")," \
     "metric points $(ch "SELECT count() FROM otel.otel_metrics_gauge WHERE ServiceName = '$SERVICE'")," \
     "logs $(ch "SELECT count() FROM otel.otel_logs WHERE ServiceName = '$SERVICE'")"
echo "collector: refused $(( $(collector_metric otelcol_receiver_refused_) - before_refused )), " \
     "enqueue failed $(( $(collector_metric otelcol_exporter_enqueue_failed_) - before_enqueue_failed )), " \
     "send failed $(( $(collector_metric otelcol_exporter_send_failed_) - before_send_failed ))"
(( p95 <= 30000 ))
