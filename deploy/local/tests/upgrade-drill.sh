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
source tests/drill-lib.sh

cleanup() {
  if ! $keep && [[ -d "$tree/deploy/local" ]]; then
    (cd "$tree/deploy/local" && docker compose --profile platform down -v --remove-orphans >> "$log" 2>&1)
    remove_tree
  fi
}
# Every port the stack publishes (read from the compose file, the release this checkout is) must be free, whoever holds it (a local API
# on 5180, say): checked first, before anything is removed. An earlier drill's stack holding them doesn't count: it goes next, with its
# volumes (interrupted, or --keep), for a fresh install at $from with this run's secrets.
require_free_ports "$(stack_ports --profile platform)" juice-upgrade
remove_project juice-upgrade

trap cleanup EXIT

say "upgrade drill: $from → $to (${minutes} min of 1× load first)"
git -C "$root" worktree remove --force "$tree" >/dev/null 2>&1
git -C "$root" worktree add --detach "$tree" "$from" >> "$log" 2>&1 || { say "no worktree at $from"; exit 1; }
# Everything below runs in the drill's own checkout, never the working one: stop if it isn't there.
cd "$tree/deploy/local" 2>/dev/null && [[ "$(pwd -P)" != "$(cd "$here" && pwd -P)" ]] || { say "$from has no deploy/local in $tree"; exit 1; }
JUICE_PLATFORM_TENANT_ID=upgrade-drill bash scripts/init.sh >> "$log" 2>&1 || { say "init.sh at $from failed"; exit 1; }
# The exported COMPOSE_PROJECT_NAME already wins over .env; the drill's .env says so too, for anyone running compose in its directory.
sed -i 's/^COMPOSE_PROJECT_NAME=.*/COMPOSE_PROJECT_NAME=juice-upgrade/' .env
dc() { docker compose --profile platform "$@"; }
ch() { dc exec -T clickhouse clickhouse client --user admin --password "$(env_of CLICKHOUSE_ADMIN_PASSWORD)" -q "$1" </dev/null; }
psql_() { dc exec -T postgres psql -U juice -d juice_observability -At "$@" </dev/null; }
pg() { psql_ -c "$1"; }

say "starting $from"
# 1.2.0's API can crash as it starts (exit 139: Npgsql's GSS encryption probe, fixed in 1.3.0, docs/scale-tests.md, Phase 11 fixes §1),
# and its compose file has no restart policy for it: started again, up to three times, each crash logged with the API's last lines.
for attempt in 1 2 3; do
  dc up -d --build --wait >> "$log" 2>&1 && break
  say "$from didn't start (attempt $attempt): the API exited $(docker inspect -f '{{.State.ExitCode}}' "$(dc ps -aq platform-api)" 2>/dev/null)"
  dc logs --tail 40 platform-api >> "$log" 2>&1
  (( attempt == 3 )) && { say "$from didn't start: see $log"; exit 1; }
done
say "sending ${minutes} min of 1× load"
load_from=$(date -u +%Y-%m-%dT%H:%M:%SZ)
JUICE_LOCAL_DIR="$tree/deploy/local/" node "$here/scale/generate.mjs" --multiplier 1 --duration $((minutes * 60)) --out "$out/before-load" >> "$log" 2>&1 \
  || { say "the load before the upgrade failed: see $log"; exit 1; }
load_to=$(date -u +%Y-%m-%dT%H:%M:%SZ)
sleep 90   # discovery, health and rules catch up on the load's last minute

# What there is: every telemetry table's rows, and every platform table's (as tests/restore-drill.sh counts them). Tables whose rows
# come and go by design are left out: the outbox's (delivered messages are removed), expiring idempotency keys, and the leases' and
# heartbeats' (live state).
transient='^(outbox|idempotency_records|worker_heartbeats|worker_leases|__ef_migrations_history)'
snapshot() {
  ch "SELECT table, sum(rows) FROM system.parts WHERE active AND database = 'otel' AND table LIKE 'otel_%' AND table NOT LIKE '%_mv' GROUP BY table ORDER BY table FORMAT TSV"
  # One psql call: the counting query built from pg_tables, then run. If it fails, each table on its own, and a count that fails there
  # leaves its line empty, which the comparison counts as lost: a table never drops out of it unseen.
  local names query
  names=$(pg "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename !~ '$transient' ORDER BY 1")
  query=$(pg "SELECT string_agg(format('SELECT %L, count(*) FROM %I', 'pg.' || tablename, tablename), ' UNION ALL ' ORDER BY tablename)
    FROM pg_tables WHERE schemaname = 'public' AND tablename !~ '$transient'")
  [[ -n "$names" ]] || echo -e "pg.tables\t"   # not even the list: an empty count, so the comparison fails
  if ! psql_ -F $'\t' -c "$query"; then
    for t in $names; do echo -e "pg.$t\t$(pg "SELECT count(*) FROM \"$t\"" 2>/dev/null)"; done
  fi
  echo -e "pg.migrations\t$(pg 'SELECT count(*) FROM __ef_migrations_history')"
}
snapshot > "$out/before.tsv"
spans_before=$(awk -F'\t' '$1 == "otel_traces" { print $2 + 0 }' "$out/before.tsv")
(( spans_before > 0 )) || { say "no spans stored before the upgrade: the drill would test nothing"; exit 1; }
say "before: $(tr '\t\n' '= ' < "$out/before.tsv")"

say "upgrading in place to $to"
git -C "$tree" checkout --detach "$to" >> "$log" 2>&1 || { say "checkout of $to failed"; exit 1; }
bash scripts/init.sh >> "$log" 2>&1 || { say "init.sh at $to failed"; exit 1; }
sed -i 's/^COMPOSE_PROJECT_NAME=.*/COMPOSE_PROJECT_NAME=juice-upgrade/' .env
started=$(date +%s)
dc up -d --build --wait --remove-orphans >> "$log" 2>&1 || { say "$to didn't start: see $log"; exit 1; }
up_in=$(( $(date +%s) - started ))
check "scripts/apply-clickhouse-access.sh ran (step 5 of the runbook)" 'bash scripts/apply-clickhouse-access.sh >> "$log" 2>&1'
say "compose up to the stack up again: $up_in s"

say "sending 2 min of 1× load through the upgraded stack"
check "the load after the upgrade sent" 'JUICE_LOCAL_DIR="$tree/deploy/local/" node "$here/scale/generate.mjs" --multiplier 1 --duration 120 --out "$out/after-load" >> "$log" 2>&1'
sleep 60
snapshot > "$out/after.tsv"
say "after: $(tr '\t\n' '= ' < "$out/after.tsv")"

# ─── the checks ───
# Every count before against after: one missing after (a table dropped, a count that failed) is lost too.
lost=$(join -t $'\t' -a1 -e MISSING -o 0,1.2,2.2 <(sort "$out/before.tsv") <(sort "$out/after.tsv") \
  | awk -F'\t' '$2 !~ /^[0-9]+$/ || $3 !~ /^[0-9]+$/ || $3 + 0 < $2 + 0 { print $1 }' | tr '\n' ' ')
check "nothing lost: every table and record count at least what it was (${lost:-all kept})" '[[ -z "$lost" ]]'
grew=$(join -t $'\t' <(sort "$out/before.tsv") <(sort "$out/after.tsv") | awk -F'\t' '$1 == "otel_traces" && $3 > $2 { print "yes" }')
check "telemetry still flows: more spans after the upgrade's load" '[[ "$grew" == yes ]]'
applied=$(pg 'SELECT count(*) FROM __ef_migrations_history')
shipped=$(ls "$tree/src/Juice.Observability.Data/Migrations" | grep -cE '^[0-9]{14}_[A-Za-z0-9]+\.cs$')
check "every migration the release has applied ($applied of $shipped)" '[[ "$applied" == "$shipped" ]]'
columns=$(ch "SELECT count() FROM system.columns WHERE database = 'otel' AND name LIKE 'Juice%' AND default_kind = 'MATERIALIZED'")
check "the resource columns on all seven tables ($columns of 84)" '[[ "$columns" == 84 ]]'
# The platform reads the old data through the new columns: a trace search over the load before the upgrade (its tenant filter is
# JuiceTenant now, on parts written before the column existed) finds its traces.
token=$(viewer_token "$(env_of KEYCLOAK_DEMO_PASSWORD)")
tenant=$(first_tenant "$token")
found=$(curl -s -H "Authorization: Bearer $token" "http://localhost:5180/api/v1/tenants/$tenant/telemetry/traces?from=$load_from&to=$load_to&limit=10" | page_items)
check "the old traces found through the new columns ($found of a page of 10, from before the upgrade)" '[[ "$found" -gt 0 ]]'
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
# The drill's stack enables every worker the platform has (WorkerPulse.KnownWorkers, 8), and each must be passing: every live row that
# should pass (the lease holder's, or every replica's for a worker without a lease) has a pass that ended within its threshold (three
# intervals, at least 60 s) and succeeded. One replica's good row doesn't hide another's failing one.
live="enabled AND seen_at > now() - interval '1 minute' AND (held OR NOT leased)"
workers=$(pg "SELECT count(DISTINCT worker) FROM worker_heartbeats WHERE $live")
failing=$(pg "SELECT count(*) FROM worker_heartbeats WHERE $live AND NOT (coalesce(last_outcome IN ('success', 'partial'), false)
  AND coalesce(last_ended_at > now() - greatest(interval_seconds * 3, 60) * interval '1 second', false))")
check "every worker running and passing since the upgrade ($workers of 8, $failing rows failing)" '[[ "$workers" == 8 && "$failing" == 0 ]]'
status=""
for _ in $(seq 1 24); do
  status=$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5180/health/platform); [[ "$status" == 200 ]] && break; sleep 5
done
check "/health/platform ok (HTTP $status)" '[[ "$status" == 200 ]]'
# The API reports as service.name juice-observability-platform (PlatformTelemetry.ServiceName), the Collector's self-scrape as otel-collector.
leaked=$(pg "SELECT count(*) FROM services WHERE name LIKE 'juice-observability%' OR name LIKE 'otel%' OR namespace LIKE 'juice%'")
check "the platform's own telemetry isn't in the tenant's inventory ($leaked services)" '[[ "$leaked" == 0 ]]'

{
  echo "# Upgrade drill $from → ${to:0:7}, $stamp UTC"
  echo
  echo "${minutes} min of 1× load at $from, then upgraded in place (init.sh, compose up, apply-clickhouse-access.sh), then 2 min more."
  echo "Compose up to the stack up again: $up_in s."
  echo
  echo "| Count | Before | After |"; echo "|---|--:|--:|"
  join -t $'\t' -a1 -a2 -e - -o 0,1.2,2.2 <(sort "$out/before.tsv") <(sort "$out/after.tsv") | awk -F'\t' '{ printf "| %s | %s | %s |\n", $1, $2, $3 }'
  echo
  grep -E ' (ok|FAILED): ' "$log" | sed 's/^[0-9:]* /- /'
} > "$out/result.md"
say "result: $out/result.md"
if (( ${#failures[@]} )); then say "${#failures[@]} check(s) failed"; exit 1; fi
say "every check passed"
