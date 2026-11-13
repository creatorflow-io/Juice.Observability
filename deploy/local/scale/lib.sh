# Sourced by the scale scripts, from deploy/local: the scale stack's compose command and quick ClickHouse and PostgreSQL queries.
export MSYS_NO_PATHCONV=1
source .env
SCALE_PROJECT=${SCALE_PROJECT:-juice-scale}
# CEILING=ingest|search adds that ceiling's overlay (scale/compose.ceiling-<name>.yml: which workers are off), for docs/phase-11-plan.md R19.
SCALE_FILES=(-f docker-compose.yml -f scale/compose.scale.yml)
[[ -n "${CEILING:-}" ]] && SCALE_FILES+=(-f "scale/compose.ceiling-$CEILING.yml")
dc() { docker compose -p "$SCALE_PROJECT" "${SCALE_FILES[@]}" --profile platform "$@"; }
# Each query gives up after 20 s: a stalled stack leaves a missing sample, which the report judges, rather than a hung run.
ch() { timeout 20 docker compose -p "$SCALE_PROJECT" "${SCALE_FILES[@]}" --profile platform exec -T clickhouse clickhouse client --user admin --password "$CLICKHOUSE_ADMIN_PASSWORD" -q "$1" </dev/null; }
pg() { timeout 20 docker compose -p "$SCALE_PROJECT" "${SCALE_FILES[@]}" --profile platform exec -T postgres psql -U juice -d juice_observability -AtF, -c "$1" </dev/null; }
# The main Collector's self-metrics at time $1, summed over label sets, as CSV lines: time,metric,value.
collector() {
  curl -s -m 5 http://localhost:8888/metrics | awk -v t="$1" '
    /^otelcol_(receiver_accepted|receiver_refused|exporter_sent|exporter_send_failed|exporter_enqueue_failed)_(spans|metric_points|log_records)/ ||
    /^otelcol_exporter_queue_size/ { name = $1; sub(/\{.*/, "", name); if (name ~ /queue_size/) { q[name] += $NF } else { s[name] += $NF } }
    END { for (k in s) printf "%s,%s,%.0f\n", t, k, s[k]; for (k in q) printf "%s,%s,%.0f\n", t, k, q[k] }'
}
