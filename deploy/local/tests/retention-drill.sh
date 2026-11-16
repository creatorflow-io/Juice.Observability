#!/usr/bin/env bash
# The retention drill (docs/phase-10-plan.md milestone 4): retention seen to delete. A scratch compose project (juice-retention, its own
# volumes) from a clean checkout of this commit, with the TTLs set short in its .env (JUICE_*_TTL, as an operator sets them before the
# tables are created): the exporter creates its tables with them, telemetry is sent, and once it is older than its TTL ClickHouse removes
# it by itself, table by table. The load's rows (the generated shop, namespace shop) are watched until gone; the Collector's own host and
# self metrics keep arriving meanwhile, so the tables never empty: every row of any source must also be gone once past its TTL.
#   tests/retention-drill.sh [--keep]          TTL=4m (each signal's), MINUTES=2 (of 1× load)
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
say() { echo "$(date -u +%H:%M:%S) $*" | tee -a "$log"; }
failures=()
check() { if eval "$2"; then say "ok: $1"; else say "FAILED: $1"; failures+=("$1"); fi; }
export COMPOSE_PROJECT_NAME=juice-retention

ports=$(docker compose config --format json 2>/dev/null \
  | node -e 'let j="";process.stdin.on("data",d=>j+=d).on("end",()=>{const c=JSON.parse(j);console.log([...new Set(Object.values(c.services)
      .flatMap(s=>(s.ports||[]).map(p=>p.published)).filter(Boolean))].join(" "))})')
[[ -n "$ports" ]] || { say "can't read the stack's ports from docker-compose.yml"; exit 1; }
for port in $ports; do
  [[ -n "$(docker ps -q --filter label=com.docker.compose.project=juice-retention --filter publish="$port")" ]] && continue
  if timeout 1 bash -c "exec 3<>/dev/tcp/127.0.0.1/$port" 2>/dev/null; then say "port $port is in use (the working stack?): stop it first"; exit 2; fi
done
docker compose -p juice-retention down -v --remove-orphans >> "$log" 2>&1
docker volume ls -q --filter label=com.docker.compose.project=juice-retention | xargs -r docker volume rm >> "$log" 2>&1

cleanup() {
  if ! $keep && [[ -d "$tree/deploy/local" ]]; then
    (cd "$tree/deploy/local" && docker compose down -v --remove-orphans >> "$log" 2>&1)
    git -C "$root" worktree remove --force "$tree" >> "$log" 2>&1
    say "the drill's project, volumes and worktree removed"
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
admin=$(sed -n 's/^CLICKHOUSE_ADMIN_PASSWORD=//p' .env)
ch() { docker compose exec -T clickhouse clickhouse client --user admin --password "$admin" -q "$1" </dev/null; }

say "starting the telemetry stack (no platform)"
docker compose up -d --wait >> "$log" 2>&1 || { say "the stack didn't start: see $log"; exit 1; }
tables="otel_traces otel_logs otel_metrics_gauge otel_metrics_sum otel_metrics_histogram otel_metrics_exponential_histogram otel_metrics_summary"
for _ in $(seq 1 60); do
  [[ "$(ch "SELECT count() FROM system.tables WHERE database = 'otel' AND name IN ('${tables// /\',\'}')")" == 7 ]] && break; sleep 2
done
ttls=$(ch "SELECT name, extract(engine_full, 'TTL [^S]*') FROM system.tables WHERE database = 'otel' AND name IN ('${tables// /\',\'}') ORDER BY name FORMAT TSV")
say "TTLs: $(echo "$ttls" | tr '\t\n' '  ')"
set_ttl=$(echo "$ttls" | grep -c "toIntervalMinute(${ttl%m})")
check "the exporter created every table with the configured TTL ($set_ttl of 7 at $ttl)" '[[ "$set_ttl" == 7 ]]'
for t in $tables; do ch "ALTER TABLE otel.$t MODIFY SETTING merge_with_ttl_timeout = 60" >> "$log" 2>&1; done

say "sending ${minutes} min of 1× load"
JUICE_LOCAL_DIR="$tree/deploy/local/" node "$here/scale/generate.mjs" --multiplier 1 --duration $((minutes * 60)) --out "$out/load" >> "$log" 2>&1 \
  || { say "the load failed: see $log"; exit 1; }
sent_at=$(date +%s)
# The load's rows in each table (the generated shop).
rows() {
  local t
  for t in $tables; do echo -e "$t\t$(ch "SELECT count() FROM otel.$t WHERE JuiceServiceNamespace = 'shop'")"; done
}
# Rows of any source past their TTL by more than a TTL merge's interval (60 s) and a minute to run it.
expired() {
  local t col sum=0 n
  for t in $tables; do
    [[ $t == otel_metrics_* ]] && col=TimeUnix || col=Timestamp
    n=$(ch "SELECT count() FROM otel.$t WHERE $col < now() - toIntervalMinute(${ttl%m}) - INTERVAL 2 MINUTE"); sum=$(( sum + n ))
  done
  echo "$sum"
}
stored=$(rows); echo "$stored" | sed "s/^/$(date -u +%H:%M:%S)\tstored\t/" >> "$out/rows.tsv"
say "stored: $(echo "$stored" | tr '\t\n' '= ')"
spans=$(echo "$stored" | awk -F'\t' '$1 == "otel_traces" { print $2 + 0 }')
check "telemetry stored before it expired ($spans spans)" '(( spans > 0 ))'

# Every row is past its TTL a TTL after the load ended; then ClickHouse's own TTL merges remove it. Watched every 30 s, up to 15 minutes.
left=""
for i in $(seq 1 30); do
  sleep 30
  now=$(rows); echo "$now" | sed "s/^/$(date -u +%H:%M:%S)\tleft\t/" >> "$out/rows.tsv"
  left=$(echo "$now" | awk -F'\t' '$2 > 0' | tr '\t\n' '= ')
  expired=$(( $(date +%s) - sent_at ))
  say "after ${expired} s: ${left:-every row of the load gone}"
  [[ -z "$left" ]] && break
done
gone_after=$(( $(date +%s) - sent_at ))
check "the load's rows deleted by their TTL, with nothing forcing it (${left:-all gone} ${gone_after} s after the load ended)" '[[ -z "$left" ]]'
old=$(expired)
check "no row of any source left past its TTL (the Collector's own metrics included: $old)" '[[ "$old" == 0 ]]'

{
  echo "# Retention drill, $stamp UTC, ${to:0:7}"
  echo
  echo "TTL $ttl on every signal (JUICE_*_TTL in .env before the tables were created), merge_with_ttl_timeout 60 s on the scratch tables,"
  echo "${minutes} min of 1× load; every row gone $gone_after s after the load ended."
  echo
  echo "| When | Stage | Table | Rows |"; echo "|---|---|---|--:|"
  awk -F'\t' '{ printf "| %s | %s | %s | %s |\n", $1, $2, $3, $4 }' "$out/rows.tsv"
  echo
  grep -E ' (ok|FAILED): ' "$log" | sed 's/^[0-9:]* /- /'
} > "$out/result.md"
say "result: $out/result.md"
if (( ${#failures[@]} )); then say "${#failures[@]} check(s) failed"; exit 1; fi
say "every check passed"
