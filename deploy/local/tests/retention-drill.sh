#!/usr/bin/env bash
# The retention drill (docs/phase-10-plan.md milestone 4): retention seen to delete. A scratch compose project (juice-retention, its own
# volumes) from a clean checkout of this commit, with the TTLs set short in its .env (JUICE_*_TTL, as an operator sets them before the
# tables are created): the exporter creates its tables with them, telemetry is sent, and once it is older than its TTL ClickHouse removes
# it by itself, table by table, the trace index included. The load's rows (the generated shop, namespace shop) are watched until gone;
# the Collector's own host and self metrics keep arriving meanwhile, so the tables never empty: every row of any source must also be
# gone once past its TTL.
#   tests/retention-drill.sh [--keep]          TTL=4m (each signal's, in whole minutes), MINUTES=2 (of 1× load)
# ClickHouse applies a TTL in a merge, at most every merge_with_ttl_timeout (4 hours by default) per partition: the drill lowers that to
# 60 s on its scratch tables only, so it waits minutes, not hours; the deletion itself is ClickHouse's, nothing forces it.
# Needs the stack's ports free (the working stack stopped). Writes build/retention-drill-<time>/: drill.log, rows.tsv, result.md.
set -uo pipefail
cd "$(dirname "$0")/.."
here=$(pwd)
root=$(cd ../.. && pwd)
keep=false; [[ "${1:-}" == "--keep" ]] && keep=true
ttl=${TTL:-4m}
minutes=${MINUTES:-2}
to=$(git -C "$root" rev-parse HEAD)
stamp=$(date -u +%Y%m%dT%H%M)
out="$root/build/retention-drill-$stamp"; mkdir -p "$out"
tree="$root/build/retention-drill"
log="$out/drill.log"
source tests/drill-lib.sh
[[ "$ttl" =~ ^[1-9][0-9]*m$ ]] || { say "TTL is whole minutes, such as 4m (the exporter then writes toIntervalMinute)"; exit 2; }
ttl_minutes=${ttl%m}
export COMPOSE_PROJECT_NAME=juice-retention

require_free_ports "$(stack_ports)" juice-retention
remove_project juice-retention

cleanup() {
  if ! $keep && [[ -d "$tree/deploy/local" ]]; then
    (cd "$tree/deploy/local" && docker compose --profile platform down -v --remove-orphans >> "$log" 2>&1)
    remove_tree
  fi
}
trap cleanup EXIT

say "retention drill at ${to:0:7}: TTL $ttl for every signal, ${minutes} min of 1× load"
git -C "$root" worktree remove --force "$tree" >/dev/null 2>&1
git -C "$root" worktree add --detach "$tree" "$to" >> "$log" 2>&1 || { say "no worktree at $to"; exit 1; }
cd "$tree/deploy/local" 2>/dev/null && [[ "$(pwd -P)" != "$(cd "$here" && pwd -P)" ]] || { say "no deploy/local in $tree"; exit 1; }
JUICE_PLATFORM_TENANT_ID=retention-drill bash scripts/init.sh >> "$log" 2>&1 || { say "init.sh failed"; exit 1; }
sed -i "s/^COMPOSE_PROJECT_NAME=.*/COMPOSE_PROJECT_NAME=juice-retention/; s/^JUICE_TRACES_TTL=.*/JUICE_TRACES_TTL=$ttl/;
  s/^JUICE_METRICS_TTL=.*/JUICE_METRICS_TTL=$ttl/; s/^JUICE_LOGS_TTL=.*/JUICE_LOGS_TTL=$ttl/" .env
admin=$(env_of CLICKHOUSE_ADMIN_PASSWORD)
ch() { docker compose exec -T clickhouse clickhouse client --user admin --password "$admin" -q "$1" </dev/null; }

say "starting the telemetry stack (no platform)"
docker compose up -d --wait >> "$log" 2>&1 || { say "the stack didn't start: see $log"; exit 1; }
# Every table the exporter makes, the trace index (otel_traces_trace_id_ts, by trace start) included, and its time column.
tables="otel_traces otel_logs otel_traces_trace_id_ts otel_metrics_gauge otel_metrics_sum otel_metrics_histogram otel_metrics_exponential_histogram otel_metrics_summary"
listed="'${tables// /\',\'}'"
time_of() { case $1 in otel_metrics_*) echo TimeUnix ;; otel_traces_trace_id_ts) echo Start ;; *) echo Timestamp ;; esac; }
for _ in $(seq 1 60); do
  [[ "$(ch "SELECT count() FROM system.tables WHERE database = 'otel' AND name IN ($listed)")" == 8 ]] && break; sleep 2
done
ttls=$(ch "SELECT name, extract(engine_full, 'TTL (.*?) SETTINGS') FROM system.tables WHERE database = 'otel' AND name IN ($listed) ORDER BY name FORMAT TSV")
say "TTLs: $(echo "$ttls" | tr '\t\n' '  ')"
set_ttl=$(echo "$ttls" | grep -c "toIntervalMinute($ttl_minutes)")
check "the exporter created every table, the trace index included, with the configured TTL ($set_ttl of 8 at $ttl)" '[[ "$set_ttl" == 8 ]]'
for t in $tables; do ch "ALTER TABLE otel.$t MODIFY SETTING merge_with_ttl_timeout = 60" >> "$log" 2>&1; done

say "sending ${minutes} min of 1× load"
JUICE_LOCAL_DIR="$tree/deploy/local/" node "$here/scale/generate.mjs" --multiplier 1 --duration $((minutes * 60)) --out "$out/load" >> "$log" 2>&1 \
  || { say "the load failed: see $log"; exit 1; }
sent_at=$(date +%s)
# The load's rows in each table (the generated shop; every trace in the index is the load's: nothing else sends spans). A count that
# isn't a number (a failed query) is printed as it came, and never counts as gone.
rows() {
  local t where query=""
  for t in $tables; do
    [[ $t == otel_traces_trace_id_ts ]] && where=1 || where="JuiceServiceNamespace = 'shop'"
    query+="${query:+ UNION ALL }SELECT '$t', count() FROM otel.$t WHERE $where"
  done
  # A failed query prints no line for a table: each table is listed, its count empty then, which never reads as gone.
  local got; got=$(ch "$query FORMAT TSV")
  for t in $tables; do echo -e "$t\t$(echo "$got" | awk -F'\t' -v t="$t" '$1 == t { print $2 }')"; done
}
# Rows of any source past their TTL by more than a TTL merge's interval (60 s) and a minute to run it; "?" when a count failed.
past_ttl() {
  local t query=""
  for t in $tables; do
    query+="${query:+ UNION ALL }SELECT count() AS n FROM otel.$t WHERE $(time_of "$t") < now() - toIntervalMinute($ttl_minutes) - INTERVAL 2 MINUTE"
  done
  local n; n=$(ch "SELECT sum(n) FROM ($query)")
  is_count "$n" && echo "$n" || echo "?"
}
stored=$(rows); echo "$stored" | sed "s/^/$(date -u +%H:%M:%S)\tstored\t/" >> "$out/rows.tsv"
say "stored: $(echo "$stored" | tr '\t\n' '= ')"
spans=$(echo "$stored" | awk -F'\t' '$1 == "otel_traces" { print $2 }')
check "telemetry stored before it expired ($spans spans)" 'is_count "$spans" && (( spans > 0 ))'

# Every row is past its TTL a TTL after the load ended; then ClickHouse's own TTL merges remove it. Watched every 30 s, up to 15 minutes.
left=""
for _ in $(seq 1 30); do
  sleep 30
  now=$(rows); echo "$now" | sed "s/^/$(date -u +%H:%M:%S)\tleft\t/" >> "$out/rows.tsv"
  left=$(echo "$now" | awk -F'\t' '$2 != "0"' | tr '\t\n' '= ')
  say "after $(( $(date +%s) - sent_at )) s: ${left:-every row of the load gone}"
  [[ -z "$left" ]] && break
done
gone_after=$(( $(date +%s) - sent_at ))
check "the load's rows deleted by their TTL, with nothing forcing it (${left:-all gone} ${gone_after} s after the load ended)" '[[ -z "$left" ]]'
old=$(past_ttl)
check "no row of any source left past its TTL (the Collector's own metrics included: $old)" '[[ "$old" == 0 ]]'

{
  echo "# Retention drill, $stamp UTC, ${to:0:7}"
  echo
  echo "TTL $ttl on every signal (JUICE_*_TTL in .env before the tables were created), merge_with_ttl_timeout 60 s on the scratch tables,"
  echo "${minutes} min of 1× load; every row of it gone $gone_after s after the load ended."
  echo
  echo "| When | Stage | Table | Rows of the load |"; echo "|---|---|---|--:|"
  awk -F'\t' '{ printf "| %s | %s | %s | %s |\n", $1, $2, $3, $4 }' "$out/rows.tsv"
  echo
  grep -E ' (ok|FAILED): ' "$log" | sed 's/^[0-9:]* /- /'
} > "$out/result.md"
say "result: $out/result.md"
if (( ${#failures[@]} )); then say "${#failures[@]} check(s) failed"; exit 1; fi
say "every check passed"
