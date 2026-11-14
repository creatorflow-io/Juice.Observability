#!/usr/bin/env bash
# One scale step (docs/phase-10-plan.md P3, milestone 2): the generator at <multiplier> × the §9 reference rates, the query mix, the
# samplers and the probes, then a report. Needs the scale stack (scale/stack.sh up).
#   scale/run.sh <multiplier> [seconds]     default 1800 (the plan's 30 minutes); a smoke run: scale/run.sh 1 600
# The generator runs 3 minutes first (discovery, health and rules catch up on 100 new instances). The measured window is then exactly
# <seconds>: every part stops by its end, and the load goes on until the last breach run started in it has finished. Results go to
# build/scale/<UTC time>-<multiplier>x/: raw CSVs, the logs of each part, their exit statuses, and report.md.
set -uo pipefail
cd "$(dirname "$0")/.."
source scale/lib.sh
MULTIPLIER=${1:?multiplier}; SECONDS_RUN=${2:-1800}; WARMUP=${WARMUP:-180}
# Component ceilings (docs/phase-11-plan.md R19), with CEILING set (scale/steps.sh and stack.sh pass it on):
#   ingest   the ClickHouse-reading workers off (overlay), no query mix and no breach runs: the step is the load's multiplier
#   search   health, rules and SLO counting off, discovery on (the searches need the services); the load at SEARCH_LOAD× (1);
#            the step is the searches per second (half traces, half logs; the full mix has 2), no breach runs
#   workers  every worker on, the load at 1×, no query mix, breach runs on; the step is k copies of the 50 rules and 50 SLOs
#            (SLOs at most 200), written by scale/config.mjs before the step, platform-api then recreated to load them
CEILING=${CEILING:-}; SEARCH_RATE=2; LOAD=$MULTIPLIER; skipped=(); RULES=50; SLOS=50
case "$CEILING" in
  '') ;;
  ingest) skipped=(query-mix breach) ;;
  search) SEARCH_RATE=$MULTIPLIER; LOAD=${SEARCH_LOAD:-1}; skipped=(breach) ;;
  workers) LOAD=1; skipped=(query-mix)
    read -r RULES SLOS < <(node scale/config.mjs --factor "$MULTIPLIER" --out "$WORKERS_CONFIG") || { echo "config.mjs failed" >&2; exit 1; }
    dc up -d --wait --no-deps --force-recreate platform-api >/dev/null 2>&1 || { echo "platform-api didn't restart with $RULES rules" >&2; exit 1; } ;;
  *) echo "unknown CEILING $CEILING" >&2; exit 2 ;;
esac
skips() { [[ " ${skipped[*]} " == *" $1 "* ]]; }
# A Windows path where Git Bash runs it: Node reads /d/... as D:\d\..., and the generator and query mix write there.
root=$(cd ../.. && { pwd -W 2>/dev/null || pwd; })
[[ "$CEILING" == search ]] && out="$root/build/scale/$(date -u +%Y%m%dT%H%M)-search-${MULTIPLIER}per-s" || out="$root/build/scale/$(date -u +%Y%m%dT%H%M)-${CEILING:+$CEILING-}${MULTIPLIER}x"
mkdir -p "$out"
dc ps --status running --services 2>/dev/null | grep -qx platform-api || { echo "the scale stack isn't running: scale/stack.sh up" >&2; exit 1; }
# Stopped or failed early: nothing keeps running behind it, and an interrupt ends the run (a trap alone would carry on after it).
trap 'kill $(jobs -p) 2>/dev/null' EXIT
trap 'touch "$out/stop"; kill $(jobs -p) 2>/dev/null; exit 130' INT TERM

TOKEN=$(cut -d' ' -f1 secrets/otlp-tokens)
run=$(openssl rand -hex 4)
BREACH_MAX=420   # tests/breach-to-alert.sh gives up on a run after 7 minutes
# The commit, and "+changes" if anything in the repository (the platform image included) differs from it.
commit="$(git rev-parse --short HEAD)$([[ -n "$(git -C ../.. status --porcelain)" ]] && echo '+changes')"
cat > "$out/meta.json" <<JSON
{ "multiplier": $LOAD, "ceiling": "$CEILING", "search_rate": $SEARCH_RATE, "rules": $RULES, "slos": $SLOS, "skipped": [$(printf '"%s",' "${skipped[@]}" | sed 's/,$//')], "seconds": $SECONDS_RUN, "warmup": $WARMUP, "run": "$run", "project": "$SCALE_PROJECT", "commit": "$commit",
  "host_cores": $(nproc), "docker_cpus": $(docker info --format '{{.NCPU}}'), "docker_mem_bytes": $(docker info --format '{{.MemTotal}}') }
JSON
echo "scale step ×$MULTIPLIER: ${WARMUP} s warm-up, then ${SECONDS_RUN} s measured → $out"

# The Collector's counters before any load: what it refuses or fails from here on (warm-up included) counts as lost.
echo "time,metric,value" > "$out/collector-before.csv"; collector "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$out/collector-before.csv"
node scale/generate.mjs --multiplier "$LOAD" --duration $((WARMUP + SECONDS_RUN + BREACH_MAX)) --out "$out" --stop-file "$out/stop" > "$out/generator.log" 2>&1 &
gen=$!
sleep "$WARMUP" & wait $!   # in the background, so a TERM stops the step at once (the trap waits for a foreground command)
start_epoch=$(date +%s); end_epoch=$((start_epoch + SECONDS_RUN))
date -u -d "@$start_epoch" +%Y-%m-%dT%H:%M:%SZ > "$out/start"
date -u -d "@$end_epoch" +%Y-%m-%dT%H:%M:%SZ > "$out/end"

scale/sample.sh "$SECONDS_RUN" "$out" > "$out/sample.log" 2>&1 &
sampler=$!
if skips query-mix; then mix=""; else
  node scale/query-mix.mjs --until "$end_epoch" --out "$out" --search-rate "$SEARCH_RATE" > "$out/query-mix.log" 2>&1 &
  mix=$!
fi
# Rule breach-to-alert alongside (requirements §9), one run after another while one can still finish in the window's load: each sends
# a failing breach-<id> service and waits for service-error-rate to fire.
# Its own trap passes a stop on to the run in progress, which a kill of the subshell alone wouldn't reach.
( trap 'kill $run_pid 2>/dev/null; exit 0' TERM
  skips breach && { echo "breach runs skipped (ceiling $CEILING)"; exit 0; }
  while (( $(date +%s) + 240 < end_epoch )) && [[ ! -e "$out/stop" ]]; do tests/breach-to-alert.sh 1 & run_pid=$!; wait $run_pid; done ) > "$out/breach.log" 2>&1 &
breach=$!

# Ingestion freshness: every 10 s one log record through the main Collector, timed until ClickHouse answers for it (over HTTP: a
# docker exec per poll would add a second of its own). 60 s without it counts as 60 s.
echo "time,ms" > "$out/freshness.csv"
chq() { curl -s -m 5 --user "admin:$CLICKHOUSE_ADMIN_PASSWORD" --data-binary "$1" http://localhost:8123/; }
probe() {
  local id=$1 now sent deadline
  now=$(date +%s)000000000; sent=$(date +%s%3N); deadline=$((sent + 60000))
  curl -s -m 10 -o /dev/null --ssl-no-revoke --cacert secrets/tls/ca.crt -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"probe-'$run'"}}]},"scopeLogs":[{"logRecords":[{"timeUnixNano":"'$now'","body":{"stringValue":"'$id'"}}]}]}]}' \
    https://localhost:4318/v1/logs
  while (( $(date +%s%3N) < deadline )); do
    [[ "$(chq "SELECT count() FROM otel.otel_logs WHERE ServiceName = 'probe-$run' AND Body = '$id' AND Timestamp > now() - INTERVAL 10 MINUTE")" == 1 ]] \
      && { echo $(( $(date +%s%3N) - sent )); return; }
    sleep 0.2
  done
  echo 60000
}
i=0
while (( $(date +%s) < end_epoch )); do
  echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),$(probe "p$i")" >> "$out/freshness.csv"; i=$((i + 1)); sleep 10 & wait $!
done

wait "$sampler"; echo "sampler $?" > "$out/exits"
[[ -n "$mix" ]] && { wait "$mix"; echo "query-mix $?" >> "$out/exits"; }
wait "$breach"; echo "breach $?" >> "$out/exits"
# The load has done its job once the last breach run is over.
touch "$out/stop"; wait "$gen"; echo "generator $?" >> "$out/exits"
start=$(cat "$out/start") stop=$(cat "$out/end")

# Drain before counting: what the window sent may still be in the Collector's export queue or retrying. Wait until the queue is empty
# and the window's stored rows stop changing (6 minutes at most, longer than the exporter's 300 s retry), then take the Collector's
# final counters, so a send that failed after retries counts as lost.
window_rows() { ch "SELECT (SELECT count() FROM otel.otel_traces WHERE ResourceAttributes['service.namespace'] = 'shop' AND Timestamp >= parseDateTimeBestEffort('$start') AND Timestamp < parseDateTimeBestEffort('$stop'))
  + (SELECT count() FROM otel.otel_logs WHERE ResourceAttributes['service.namespace'] = 'shop' AND Timestamp >= parseDateTimeBestEffort('$start') AND Timestamp < parseDateTimeBestEffort('$stop'))"; }
drain_end=$(( $(date +%s) + 360 )); last=-1
while (( $(date +%s) < drain_end )); do
  queue=$(collector x | awk -F, '$2 == "otelcol_exporter_queue_size" { print $3 }')
  rows=$(window_rows)
  [[ "${queue:-1}" == 0 && -n "$rows" && "$rows" == "$last" ]] && break
  last=$rows; sleep 10
done
echo "drained in $(( 360 - (drain_end - $(date +%s)) )) s (queue ${queue:-?}, rows $rows)" > "$out/drain.log"
collector "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$out/collector.csv"

# Webhook dispatch: first attempt after the transition, for deliveries planned in the window up to 30 s before its end (requirements §9:
# p95 ≤ 30 s). One never attempted counts as waiting until the window's end, and so does a transition whose notifications aren't
# planned yet (it has no delivery to time; only a transition to Firing, or from it, notifies), so neither can drop out of the percentile.
pg "WITH waits AS (
      SELECT extract(epoch FROM coalesce(first_attempt_at, '$stop') - created_at) AS s, first_attempt_at IS NOT NULL AS attempted, status = 'Delivered' AS delivered
        FROM webhook_deliveries WHERE created_at >= '$start' AND created_at < timestamptz '$stop' - interval '30 seconds'
      UNION ALL
      SELECT extract(epoch FROM timestamptz '$stop' - at), false, false
        FROM alert_transitions WHERE notifications_planned_at IS NULL AND (to_state = 'Firing' OR from_state = 'Firing') AND at >= '$start' AND at < timestamptz '$stop' - interval '30 seconds')
    SELECT count(*), count(*) FILTER (WHERE attempted),
      round(percentile_cont(0.5) WITHIN GROUP (ORDER BY s)::numeric, 2), round(percentile_cont(0.95) WITHIN GROUP (ORDER BY s)::numeric, 2),
      round(max(s)::numeric, 2), count(*) FILTER (WHERE delivered),
      (SELECT count(*) FROM alert_transitions WHERE notifications_planned_at IS NULL AND (to_state = 'Firing' OR from_state = 'Firing') AND at >= '$start' AND at < timestamptz '$stop' - interval '30 seconds')
    FROM waits" > "$out/webhooks.csv"
pg "SELECT count(*) FROM alert_transitions WHERE at >= '$start' AND at < '$stop'" > "$out/transitions.csv"
# Outbox deliveries created in the window and given up for good (Failed with no next attempt: MaxRetryAttempts used): dropped.
pg "SELECT count(*) FROM outbox_deliveries WHERE state = 3 AND next_attempt_on IS NULL AND creation_time >= '$start' AND creation_time < '$stop'" > "$out/outbox.csv"
# What reached ClickHouse from the shop in the window, per signal.
{
  echo "signal,rows"
  echo "spans,$(ch "SELECT count() FROM otel.otel_traces WHERE ResourceAttributes['service.namespace'] = 'shop' AND Timestamp >= parseDateTimeBestEffort('$start') AND Timestamp < parseDateTimeBestEffort('$stop')")"
  echo "logs,$(ch "SELECT count() FROM otel.otel_logs WHERE ResourceAttributes['service.namespace'] = 'shop' AND Timestamp >= parseDateTimeBestEffort('$start') AND Timestamp < parseDateTimeBestEffort('$stop')")"
  for t in gauge sum histogram; do
    echo "points_$t,$(ch "SELECT count() FROM otel.otel_metrics_$t WHERE (ResourceAttributes['service.namespace'] = 'shop' OR startsWith(ResourceAttributes['host.name'], 'host-')) AND TimeUnix >= parseDateTimeBestEffort('$start') AND TimeUnix < parseDateTimeBestEffort('$stop')")"
  done
} > "$out/stored.csv"

trap - EXIT
node scale/report.mjs "$out"
