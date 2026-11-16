#!/usr/bin/env bash
# The disaster-recovery drill, which is also the restore on a fresh project (docs/phase-10-plan.md milestone 4, P8; requirements §9:
# RPO ≤ 24 h, RTO ≤ 4 h). The working stack is backed up as the schedule would (scripts/backup.sh, scripts/backup-telemetry.sh), then
# "lost": stopped, and not used again. A new host is stood in for by a fresh compose project (juice-dr: its own volumes and network)
# built from a clean checkout of this commit and what an operator keeps off the host: the backups, .env and secrets/. It restores the
# platform database and the telemetry, starts, and is checked: every table back with at least what was there at the backup, the
# platform answering, the old data found through its API, and new telemetry flowing.
#   tests/dr-drill.sh [--keep]
# RPO: the time between the last backup and the loss (here, minutes; in production, the backup schedule's interval). RTO: from the
# start of the rebuild to the platform answering ok on the new project. The working stack's volumes are never touched: it is stopped,
# and the caller starts it again. The drill's own project and worktree are removed at the end unless --keep; the two backup files stay
# in backups/ (they are real backups of the working stack).
# Writes build/dr-drill-<time>/: drill.log, before.tsv, after.tsv, result.md. Exit 0 when every check passed.
set -uo pipefail
cd "$(dirname "$0")/.."
here=$(pwd)
root=$(cd ../.. && pwd)
keep=false; [[ "${1:-}" == "--keep" ]] && keep=true
to=$(git -C "$root" rev-parse HEAD)
stamp=$(date -u +%Y%m%dT%H%M)
out="$root/build/dr-drill-$stamp"; mkdir -p "$out"
tree="$root/build/dr-drill"
log="$out/drill.log"
say() { echo "$(date -u +%H:%M:%S) $*" | tee -a "$log"; }
failures=()
check() { if eval "$2"; then say "ok: $1"; else say "FAILED: $1"; failures+=("$1"); fi; }
env_of() { sed -n "s/^$1=//p" "$2/.env"; }

# What there is, as the drill upgrade-drill.sh counts it: every telemetry table's rows, and every platform table's but those whose rows
# come and go by design (the outbox's, expiring idempotency keys, leases and heartbeats).
transient='^(outbox|idempotency_records|worker_heartbeats|worker_leases|__ef_migrations_history)'
snapshot() {   # in a stack's directory, with its project
  local admin; admin=$(env_of CLICKHOUSE_ADMIN_PASSWORD .)
  # The rows a query sees (count()), not the parts' row totals: a part keeps the rows a lightweight DELETE masked until it is merged,
  # and a backup carries only the rows that are there.
  local t
  for t in $(docker compose exec -T clickhouse clickhouse client --user admin --password "$admin" -q "SELECT name FROM system.tables
      WHERE database = 'otel' AND name LIKE 'otel_%' AND engine LIKE '%MergeTree' ORDER BY name" </dev/null); do
    echo -e "$t	$(docker compose exec -T clickhouse clickhouse client --user admin --password "$admin" -q "SELECT count() FROM otel.$t" </dev/null)"
  done
  local query
  query=$(docker compose exec -T postgres psql -U juice -d juice_observability -At -c "SELECT string_agg(format('SELECT %L, count(*) FROM %I',
    'pg.' || tablename, tablename), ' UNION ALL ' ORDER BY tablename) FROM pg_tables WHERE schemaname = 'public' AND tablename !~ '$transient'" </dev/null)
  [[ -n "$query" ]] && docker compose exec -T postgres psql -U juice -d juice_observability -At -F $'\t' -c "$query" </dev/null || echo -e "pg.tables\t"
}

# ─── 1. the latest backups of the working stack, then the loss ───
[[ -n "$(docker compose ps -q --status running postgres clickhouse 2>/dev/null)" ]] || { say "the working stack isn't running: nothing to back up"; exit 2; }
say "backing up the working stack (the schedule's backups)"
# The writers stop first (the Collectors and the API: the stack is about to be lost anyway), so what is counted is exactly what the
# backups hold: a restored table with fewer rows is a loss, not ingestion or a recount between the count and the backup. Backing up under
# load is tests/restore-drill.sh's.
docker compose --profile platform --profile test stop otel-collector otel-collector-test platform-api >> "$log" 2>&1
snapshot > "$out/before.tsv"
db_backup=$(bash scripts/backup.sh | cut -d' ' -f1) || { say "scripts/backup.sh failed"; exit 1; }
telemetry_backup=$(bash scripts/backup-telemetry.sh | cut -d' ' -f1) || { say "scripts/backup-telemetry.sh failed"; exit 1; }
backed_up=$(date +%s)
say "backups: $db_backup ($(du -h "$db_backup" | cut -f1)), $telemetry_backup ($(du -h "$telemetry_backup" | cut -f1))"
spans_before=$(awk -F'\t' '$1 == "otel_traces" { print $2 + 0 }' "$out/before.tsv")
(( spans_before > 0 )) || { say "no spans on the working stack: the drill would test nothing"; exit 1; }
# A window of telemetry stored before the backup, for the search after the restore.
admin=$(env_of CLICKHOUSE_ADMIN_PASSWORD .)
window=$(docker compose exec -T clickhouse clickhouse client --user admin --password "$admin" -q "SELECT formatDateTime(max(Timestamp) - INTERVAL 1 HOUR,
  '%Y-%m-%dT%H:%i:%SZ', 'UTC'), formatDateTime(max(Timestamp), '%Y-%m-%dT%H:%i:%SZ', 'UTC') FROM otel.otel_traces WHERE JuiceTenant = '$(env_of JUICE_PLATFORM_TENANT_ID .)'
  FORMAT TSV" </dev/null)
read -r search_from search_to <<< "$window"

say "the loss: the working stack stops (its volumes stay, unused by the drill)"
docker compose --profile platform --profile test --profile grafana --profile agent stop >> "$log" 2>&1
lost_at=$(date +%s)

# ─── 2. the rebuild on a fresh project ───
export COMPOSE_PROJECT_NAME=juice-dr
docker compose -p juice-dr down -v --remove-orphans >> "$log" 2>&1
docker volume ls -q --filter label=com.docker.compose.project=juice-dr | xargs -r docker volume rm >> "$log" 2>&1
ports=$(docker compose --profile platform config --format json 2>/dev/null \
  | node -e 'let j="";process.stdin.on("data",d=>j+=d).on("end",()=>{const c=JSON.parse(j);console.log([...new Set(Object.values(c.services)
      .flatMap(s=>(s.ports||[]).map(p=>p.published)).filter(Boolean))].join(" "))})')
for port in $ports; do
  if timeout 1 bash -c "exec 3<>/dev/tcp/127.0.0.1/$port" 2>/dev/null; then say "port $port is still in use: stop what holds it first"; exit 2; fi
done

cleanup() {
  if ! $keep && [[ -d "$tree/deploy/local" ]]; then
    (cd "$tree/deploy/local" && docker compose --profile platform down -v --remove-orphans >> "$log" 2>&1)
    git -C "$root" worktree remove --force "$tree" >> "$log" 2>&1
    say "the drill's project, volumes and worktree removed"
  fi
}
trap cleanup EXIT

started=$(date +%s)
say "rebuilding on a fresh project (juice-dr) from $to"
git -C "$root" worktree remove --force "$tree" >/dev/null 2>&1
git -C "$root" worktree add --detach "$tree" "$to" >> "$log" 2>&1 || { say "no worktree at $to"; exit 1; }
cd "$tree/deploy/local" 2>/dev/null && [[ "$(pwd -P)" != "$(cd "$here" && pwd -P)" ]] || { say "no deploy/local in $tree"; exit 1; }
# What the operator kept off the host: the configuration and secrets, and the two backups.
cp "$here/.env" .env && cp -r "$here/secrets" . && mkdir -p backups/clickhouse \
  && cp "$here/$db_backup" backups/ && cp "$here/$telemetry_backup" backups/clickhouse/ || { say "copying the kept files failed"; exit 1; }
sed -i 's/^COMPOSE_PROJECT_NAME=.*/COMPOSE_PROJECT_NAME=juice-dr/' .env
dc() { docker compose --profile platform "$@"; }
# The stores first, restored before anything writes to them: the Collector would create empty telemetry tables, the API migrate an
# empty database.
dc up -d --wait postgres clickhouse >> "$log" 2>&1 || { say "PostgreSQL and ClickHouse didn't start"; exit 1; }
bash scripts/restore.sh "backups/$(basename "$db_backup")" juice_observability >> "$log" 2>&1 || { say "scripts/restore.sh failed"; exit 1; }
bash scripts/restore-telemetry.sh "backups/clickhouse/$(basename "$telemetry_backup")" otel >> "$log" 2>&1 || { say "scripts/restore-telemetry.sh failed"; exit 1; }
restored=$(date +%s)
say "restored in $(( restored - started )) s"
# Counted now, before the platform starts: what came back from the backups. Once it runs, it changes them as it should (the SLO counter
# recounts its last minutes, Slos:Lateness, with what they have now).
snapshot > "$out/after.tsv"
dc up -d --build --wait --remove-orphans >> "$log" 2>&1 || { say "the platform didn't start: see $log"; exit 1; }
check "scripts/apply-clickhouse-access.sh ran" 'bash scripts/apply-clickhouse-access.sh >> "$log" 2>&1'
status=""
for _ in $(seq 1 36); do
  status=$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5180/health/platform); [[ "$status" == 200 ]] && break; sleep 5
done
up_at=$(date +%s)
check "/health/platform ok on the new project (HTTP $status)" '[[ "$status" == 200 ]]'

# ─── 3. the checks ───
lost=$(join -t $'\t' -a1 -e MISSING -o 0,1.2,2.2 <(sort "$out/before.tsv") <(sort "$out/after.tsv") \
  | awk -F'\t' '$2 !~ /^[0-9]+$/ || $3 !~ /^[0-9]+$/ || $3 + 0 < $2 + 0 { print $1 }' | tr '\n' ' ')
check "every table back with at least what it held at the backup (${lost:-all})" '[[ -z "$lost" ]]'
token=$(curl -s http://localhost:8080/realms/juice/protocol/openid-connect/token -d grant_type=password -d client_id=juice-platform-cli \
  -d username=victor --data-urlencode "password=$(env_of KEYCLOAK_DEMO_PASSWORD .)" | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p')
tenant=$(curl -s -H "Authorization: Bearer $token" http://localhost:5180/api/v1/tenants | sed -n 's/.*"id":"\([^"]*\)".*/\1/p' | head -1)
services=$(curl -s -H "Authorization: Bearer $token" "http://localhost:5180/api/v1/tenants/$tenant/health?subjectType=service&limit=200" | grep -o '"subjectId"' | wc -l)
check "the services and their health back through the API ($services services)" '[[ "$services" -gt 0 ]]'
found=$(curl -s -H "Authorization: Bearer $token" "http://localhost:5180/api/v1/tenants/$tenant/telemetry/traces?from=$search_from&to=$search_to&limit=10" \
  | grep -o '"traceId"' | wc -l)
check "the telemetry back: traces from the hour before the backup found ($found of a page of 10)" '[[ "$found" -gt 0 ]]'
JUICE_LOCAL_DIR="$tree/deploy/local/" node "$here/scale/generate.mjs" --multiplier 1 --duration 60 --out "$out/after-load" >> "$log" 2>&1
sleep 30
spans_after=$(docker compose exec -T clickhouse clickhouse client --user admin --password "$(env_of CLICKHOUSE_ADMIN_PASSWORD .)" \
  -q "SELECT count() FROM otel.otel_traces WHERE Timestamp > now() - INTERVAL 3 MINUTE" </dev/null)
check "new telemetry stored on the new project ($spans_after spans in 3 min)" '[[ "$spans_after" -gt 0 ]]'

rpo=$(( lost_at - backed_up )); rto=$(( up_at - started ))
check "RPO within 24 h ($rpo s from the last backup to the loss)" '(( rpo <= 86400 ))'
check "RTO within 4 h ($rto s from the start of the rebuild to the platform answering ok)" '(( rto <= 14400 ))'

{
  echo "# Disaster-recovery drill, $stamp UTC, ${to:0:7}"
  echo
  echo "Backups: \`$db_backup\` ($(du -h "$here/$db_backup" | cut -f1)), \`$telemetry_backup\` ($(du -h "$here/$telemetry_backup" | cut -f1))."
  echo "RPO $rpo s (the last backup to the loss). RTO $rto s (rebuild started to the platform answering ok; the restores $(( restored - started )) s of it)."
  echo
  echo "| Count | At the backup | Restored |"; echo "|---|--:|--:|"
  join -t $'\t' -a1 -a2 -e - -o 0,1.2,2.2 <(sort "$out/before.tsv") <(sort "$out/after.tsv") | awk -F'\t' '{ printf "| %s | %s | %s |\n", $1, $2, $3 }'
  echo
  grep -E ' (ok|FAILED): ' "$log" | sed 's/^[0-9:]* /- /'
} > "$out/result.md"
say "result: $out/result.md"
if (( ${#failures[@]} )); then say "${#failures[@]} check(s) failed"; exit 1; fi
say "every check passed"
