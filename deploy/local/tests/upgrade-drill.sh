#!/usr/bin/env bash
# The upgrade drill (docs/phase-10-plan.md milestone 4, docs/phase-11-plan.md R17): a stack installed at a released version, given telemetry
# at the reference load, then upgraded in place to this commit as an operator would: the new code checked out over the old (its .env,
# secrets and volumes kept), scripts/init.sh, compose up, scripts/apply-clickhouse-access.sh. Then: nothing lost, every migration applied,
# the new pieces working on the old data (the resource columns on parts written before them, the row policies, platform_monitor, the
# platform pipeline, the heartbeats), and telemetry still flowing.
#   tests/upgrade-drill.sh [--keep]            FROM=api-1.2.0 (the release tag), MINUTES=10 (of 1× load before the upgrade)
# Runs as its own compose project (juice-upgrade), from a git worktree under build/upgrade-drill, on the stack's usual ports: the working
# stack (and anything else on those ports) must be stopped first. The drill's own stack and volumes are removed at the end unless --keep.
# Writes build/upgrade-drill-<time>/: drill.log, before.tsv and after.tsv (rows and records), result.md. Exit 0 when every check passed.
set -uo pipefail
cd "$(dirname "$0")/.."
here=$(pwd)
root=$(cd ../.. && pwd)
keep=false; [[ "${1:-}" == "--keep" ]] && keep=true
from=${FROM:-api-1.2.0}
to=$(git -C "$root" rev-parse HEAD)
minutes=${MINUTES:-10}
stamp=$(date -u +%Y%m%dT%H%M)
out="$root/build/upgrade-drill-$stamp"; mkdir -p "$out"
tree="$root/build/upgrade-drill"
# Every compose call (this script's and the stack's own scripts') on the drill's project, never the working stack's.
export COMPOSE_PROJECT_NAME=juice-upgrade
log="$out/drill.log"
say() { echo "$(date -u +%H:%M:%S) $*" | tee -a "$log"; }
failures=()
check() { if eval "$2"; then say "ok: $1"; else say "FAILED: $1"; failures+=("$1"); fi; }

if [[ -n "$(docker ps -q --filter publish=4318 --filter publish=8123)" ]]; then
  say "the stack's ports are in use (the working stack?): stop it first"; exit 2
fi

cleanup() {
  if ! $keep && [[ -d "$tree/deploy/local" ]]; then
    (cd "$tree/deploy/local" && docker compose --profile platform down -v --remove-orphans >> "$log" 2>&1)
    git -C "$root" worktree remove --force "$tree" >> "$log" 2>&1
    say "the drill's stack, volumes and worktree removed"
  fi
}
trap cleanup EXIT

say "upgrade drill: $from → $to (${minutes} min of 1× load first)"
git -C "$root" worktree remove --force "$tree" >/dev/null 2>&1
git -C "$root" worktree add --detach "$tree" "$from" >> "$log" 2>&1 || { say "no worktree at $from"; exit 1; }
cd "$tree/deploy/local"
JUICE_PLATFORM_TENANT_ID=upgrade-drill bash scripts/init.sh >> "$log" 2>&1 || { say "init.sh at $from failed"; exit 1; }
sed -i 's/^COMPOSE_PROJECT_NAME=.*/COMPOSE_PROJECT_NAME=juice-upgrade/' .env
dc() { docker compose --profile platform "$@"; }
env_of() { sed -n "s/^$1=//p" .env; }
ch() { dc exec -T clickhouse clickhouse client --user admin --password "$(env_of CLICKHOUSE_ADMIN_PASSWORD)" -q "$1" </dev/null; }
pg() { dc exec -T postgres psql -U juice -d juice_observability -Atc "$1" </dev/null; }

say "starting $from"
dc up -d --build --wait >> "$log" 2>&1 || { say "$from didn't start: see $log"; exit 1; }
say "sending ${minutes} min of 1× load"
JUICE_LOCAL_DIR="$tree/deploy/local/" node "$here/scale/generate.mjs" --multiplier 1 --duration $((minutes * 60)) --out "$out/before-load" >> "$log" 2>&1
sleep 90   # discovery, health and rules catch up on the load's last minute

# What there is before: every telemetry table's rows, and the platform database's records.
snapshot() {
  ch "SELECT table, sum(rows) FROM system.parts WHERE active AND database = 'otel' AND table LIKE 'otel_%' AND table NOT LIKE '%_mv' GROUP BY table ORDER BY table FORMAT TSV"
  for t in services service_instances resources dependencies rules alerts health_snapshots health_transitions audit_records slos slo_buckets; do
    echo -e "pg.$t\t$(pg "SELECT count(*) FROM $t" 2>/dev/null || echo -)"
  done
  echo -e "pg.migrations\t$(pg 'SELECT count(*) FROM __ef_migrations_history')"
}
snapshot > "$out/before.tsv"
say "before: $(tr '\t\n' '= ' < "$out/before.tsv")"

say "upgrading in place to $to"
git -C "$tree" checkout --detach "$to" >> "$log" 2>&1 || { say "checkout of $to failed"; exit 1; }
bash scripts/init.sh >> "$log" 2>&1 || { say "init.sh at $to failed"; exit 1; }
sed -i 's/^COMPOSE_PROJECT_NAME=.*/COMPOSE_PROJECT_NAME=juice-upgrade/' .env
started=$(date +%s)
dc up -d --build --wait --remove-orphans >> "$log" 2>&1 || { say "$to didn't start: see $log"; exit 1; }
bash scripts/apply-clickhouse-access.sh >> "$log" 2>&1 || say "apply-clickhouse-access.sh failed: see $log"
say "upgraded and up in $(( $(date +%s) - started )) s"

say "sending 2 min of 1× load through the upgraded stack"
JUICE_LOCAL_DIR="$tree/deploy/local/" node "$here/scale/generate.mjs" --multiplier 1 --duration 120 --out "$out/after-load" >> "$log" 2>&1
sleep 60
snapshot > "$out/after.tsv"
say "after: $(tr '\t\n' '= ' < "$out/after.tsv")"

# ─── the checks ───
lost=$(join -t $'\t' <(sort "$out/before.tsv") <(sort "$out/after.tsv") | awk -F'\t' '$2 != "-" && $3 + 0 < $2 + 0 { print $1 }')
check "nothing lost: every table and record count at least what it was (${lost:-all kept})" '[[ -z "$lost" ]]'
grew=$(join -t $'\t' <(sort "$out/before.tsv") <(sort "$out/after.tsv") | awk -F'\t' '$1 == "otel_traces" && $3 > $2 { print "yes" }')
check "telemetry still flows: more spans after the upgrade's load" '[[ "$grew" == yes ]]'
migrations=$(pg 'SELECT migration_id FROM __ef_migrations_history ORDER BY 1 DESC LIMIT 1')
check "every migration applied (latest: $migrations)" '[[ "$migrations" == *WorkerPassSeconds ]]'
columns=$(ch "SELECT count() FROM system.columns WHERE database = 'otel' AND name LIKE 'Juice%' AND default_kind = 'MATERIALIZED'")
check "the resource columns on all seven tables ($columns of 84)" '[[ "$columns" == 84 ]]'
mismatch=$(ch "SELECT countIf(JuiceTenant != ResourceAttributes['juice.platform_tenant_id']) + countIf(JuiceServiceNamespace != ResourceAttributes['service.namespace']) FROM otel.otel_traces")
check "the columns hold their keys on parts written before them ($mismatch mismatches over every span)" '[[ "$mismatch" == 0 ]]'
policy=$(ch "SELECT select_filter FROM system.row_policies WHERE short_name = 'grafana_tenant' LIMIT 1")
check "Grafana's row policy on the tenant column ($policy)" '[[ "$policy" == *JuiceTenant* ]]'
monitor=$(dc exec -T clickhouse clickhouse client --user platform_monitor --password "$(env_of CLICKHOUSE_MONITOR_PASSWORD)" -q "SELECT count() FROM system.disks" </dev/null)
check "platform_monitor exists and reads the system tables" '[[ "$monitor" -ge 1 ]]'
platform=""
for _ in $(seq 1 12); do
  platform=$(ch "SELECT count() FROM otel.otel_metrics_gauge WHERE JuiceTenant = 'juice-platform' AND TimeUnix > now() - INTERVAL 2 MINUTE")
  [[ "$platform" -gt 0 ]] && break; sleep 10
done
check "the platform's own metrics arrive under the reserved tenant ($platform points in 2 min)" '[[ "$platform" -gt 0 ]]'
heartbeats=$(pg "SELECT count(DISTINCT worker) FROM worker_heartbeats WHERE seen_at > now() - interval '1 minute'")
check "every worker's heartbeat written ($heartbeats of 8)" '[[ "$heartbeats" == 8 ]]'
status=""
for _ in $(seq 1 24); do
  status=$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5180/health/platform); [[ "$status" == 200 ]] && break; sleep 5
done
check "/health/platform ok (HTTP $status)" '[[ "$status" == 200 ]]'
leaked=$(pg "SELECT count(*) FROM services WHERE namespace LIKE 'juice%' OR name LIKE 'otel%'")
check "the platform's own telemetry isn't in the tenant's inventory ($leaked services)" '[[ "$leaked" == 0 ]]'

{
  echo "# Upgrade drill $from → ${to:0:7}, $stamp UTC"
  echo
  echo "${minutes} min of 1× load at $from, then upgraded in place (init.sh, compose up, apply-clickhouse-access.sh), then 2 min more."
  echo
  echo "| Count | Before | After |"; echo "|---|--:|--:|"
  join -t $'\t' <(sort "$out/before.tsv") <(sort "$out/after.tsv") | awk -F'\t' '{ printf "| %s | %s | %s |\n", $1, $2, $3 }'
  echo
  grep -E ' (ok|FAILED): ' "$log" | sed 's/^[0-9:]* /- /'
} > "$out/result.md"
say "result: $out/result.md"
if (( ${#failures[@]} )); then say "${#failures[@]} check(s) failed"; exit 1; fi
say "every check passed"
