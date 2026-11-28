#!/usr/bin/env bash
# Samples the scale stack every 10 s until <seconds> have passed, into <out>/ (docs/phase-10-plan.md, "Measures and targets"):
#   resources.csv  CPU and memory per container (docker stats)
#   lag.csv        the platform's lags and backlogs, read from its database (it exports no metrics of its own). "Excess" is how far
#                  a worker is behind where its configuration says it should be (the owner's reading of "queue lag ≤ 1 min", 12 November):
#                    discovery, discovery_excess   now - the telemetry watermark; and that minus Discovery:Lag (60 s)
#                    health_stale    now - the oldest evaluation of a live subject (not Offline or Unknown); Health:Interval is 30 s
#                    health_evidence_p95  p95 over live subjects of now - last_evidence_at: how old the evidence behind what's
#                                    visible is (requirements §9 health freshness); a stalled health worker makes it grow
#                    health_age_p95  p95 over live subjects of evaluated_at - last_evidence_at (evidence age when written)
#                    alert_stale     now - the oldest last evaluation of an open alert (rules are due every 60 s)
#                    slo_lag, slo_excess   now - the oldest SLO's counted_through (an SLO not yet counted from its creation); and how far
#                                    past the moment its next minute became countable (its end + 10 s, SloCounter.Range, while telemetry
#                                    is fresh): normal is 0-30 s, a pass every 30 s; empty when there are no SLOs
#                    outbox_pending, outbox_oldest   outbox rows still to be delivered (not yet tried, in progress, or failed with a retry
#                                    due) and the oldest one's age; one given up for good is counted by run.sh, not here
#                    outbox_failed   of those, the ones that failed and wait for a retry
#                    planning_pending, planning_oldest  firing and resolving transitions whose notifications are not yet planned as requests, and the oldest one's age
#   collector.csv  the main Collector's own counters (accepted, refused, export failures per signal; queue size)
#   storage.csv    every 5 minutes: ClickHouse bytes and rows per otel table, PostgreSQL database size
#   scale/sample.sh <seconds> <out>
set -uo pipefail
cd "$(dirname "$0")/.."
source scale/lib.sh
SECONDS_TOTAL=$1 OUT=$2
mkdir -p "$OUT"
echo "time,container,cpu_pct,mem_bytes" > "$OUT/resources.csv"
echo "time,discovery,discovery_excess,health_stale,health_evidence_p95,health_age_p95,alert_stale,slo_lag,slo_excess,outbox_pending,outbox_oldest,planning_pending,planning_oldest,outbox_failed" > "$OUT/lag.csv"
echo "time,metric,value" > "$OUT/collector.csv"
echo "time,table,bytes,rows" > "$OUT/storage.csv"

bytes() {   # "1.5GiB" → bytes
  awk -v v="$1" 'BEGIN { n = v + 0; u = v; sub(/^[0-9.]+/, "", u);
    m = (u == "KiB") ? 1024 : (u == "MiB") ? 1048576 : (u == "GiB") ? 1073741824 : (u == "kB") ? 1000 : (u == "MB") ? 1e6 : (u == "GB") ? 1e9 : 1;
    printf "%.0f", n * m }'
}
LIVE="state NOT IN ('Offline', 'Unknown')"
# Outbox rows still to be delivered (Juice DeliveryState: 0 NotPublished, 1 InProgress, 3 Failed with a retry due).
OUTBOX_DUE="(state IN (0, 1) OR (state = 3 AND next_attempt_on IS NOT NULL))"
WATERMARK="(SELECT max(watermark) FROM reconciliation_checkpoints WHERE source = 'telemetry')"
LAG_SQL="SELECT to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"'),
  round(extract(epoch FROM now() - $WATERMARK)::numeric, 1),
  round(extract(epoch FROM now() - $WATERMARK)::numeric - 60, 1),
  round(extract(epoch FROM now() - (SELECT min(evaluated_at) FROM health_snapshots WHERE $LIVE))::numeric, 1),
  round((SELECT percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM now() - last_evidence_at)) FROM health_snapshots
         WHERE last_evidence_at IS NOT NULL AND $LIVE)::numeric, 1),
  round((SELECT percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM evaluated_at - last_evidence_at)) FROM health_snapshots
         WHERE last_evidence_at IS NOT NULL AND $LIVE)::numeric, 1),
  round(extract(epoch FROM now() - (SELECT min(last_evaluated_at) FROM alerts WHERE state <> 'Resolved'))::numeric, 1),
  round(extract(epoch FROM now() - (SELECT min(coalesce(counted_through, created_at)) FROM slos WHERE NOT retired))::numeric, 1),
  (SELECT CASE WHEN x IS NULL THEN NULL ELSE round(greatest(0, x)::numeric, 1) END FROM (SELECT extract(epoch FROM now()
     - ((SELECT min(coalesce(counted_through, created_at)) FROM slos WHERE NOT retired) + interval '70 seconds')) AS x) s),
  (SELECT count(*) FROM outbox_deliveries WHERE $OUTBOX_DUE),
  round(extract(epoch FROM now() - (SELECT min(creation_time) FROM outbox_deliveries WHERE $OUTBOX_DUE))::numeric, 1),
  (SELECT count(*) FROM alert_transitions WHERE notifications_planned_at IS NULL AND (to_state = 'Firing' OR from_state = 'Firing') AND at > now() - interval '1 hour'),
  round(extract(epoch FROM now() - (SELECT min(at) FROM alert_transitions WHERE notifications_planned_at IS NULL AND (to_state = 'Firing' OR from_state = 'Firing') AND at > now() - interval '1 hour'))::numeric, 1),
  (SELECT count(*) FROM outbox_deliveries WHERE state = 3 AND next_attempt_on IS NOT NULL)"
storage() {
  local t=$1
  ch "SELECT '$t', table, sum(bytes_on_disk), sum(rows) FROM system.parts WHERE active AND database = 'otel' GROUP BY table ORDER BY table FORMAT CSV" \
    | tr -d '"' >> "$OUT/storage.csv"
  echo "$t,postgres,$(pg "SELECT pg_database_size('juice_observability')"),0" >> "$OUT/storage.csv"
}

end=$(( $(date +%s) + SECONDS_TOTAL ))
n=0
while (( $(date +%s) < end )); do
  started=$(date +%s)
  t=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  timeout 20 docker stats --no-stream --format '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}' 2>/dev/null | grep "^$SCALE_PROJECT-" | while IFS='|' read -r name cpu mem; do
    echo "$t,${name#"$SCALE_PROJECT"-},${cpu%\%},$(bytes "${mem%% /*}")"
  done >> "$OUT/resources.csv"
  pg "$LAG_SQL" >> "$OUT/lag.csv"
  collector "$t" >> "$OUT/collector.csv"
  (( n % 30 == 0 )) && storage "$t"
  n=$((n + 1))
  sleep $(( 10 - ($(date +%s) - started) > 0 ? 10 - ($(date +%s) - started) : 0 ))
done
storage "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
