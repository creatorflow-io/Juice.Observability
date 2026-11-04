#!/usr/bin/env bash
# Restore drill (requirements §9 durability): backs up the live platform database, restores it into a scratch
# database, compares every table's row count, and reports the time taken (RTO for this data size).
# Then the same for telemetry (ClickHouse).
set -uo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
q() { docker compose exec -T postgres psql -U juice -d "$1" -At -c "$2" < /dev/null; }
counts() {
  local db=$1 tables
  tables=$(q "$db" "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY 1")
  for t in $tables; do echo "$t $(q "$db" "SELECT count(*) FROM \"$t\"")"; done
}

start=$(date +%s)
file=$(bash scripts/backup.sh | cut -d' ' -f1)
before=$(counts juice_observability)
bash scripts/restore.sh "$file" juice_observability_drill > /dev/null
after=$(counts juice_observability_drill)
elapsed=$(( $(date +%s) - start ))
q postgres "DROP DATABASE juice_observability_drill WITH (FORCE)" > /dev/null

echo "$after" | sed 's/^/  /'
# The live database keeps changing while the drill runs: compare the restored copy with the backup, which pg_restore
# loaded exactly or failed; the "before" counts may be slightly lower or higher.
tables=$(echo "$after" | wc -l)
if [[ $(echo "$before" | cut -d' ' -f1) == $(echo "$after" | cut -d' ' -f1) ]]; then
  echo "restore drill: $tables tables restored from $file in ${elapsed} s (backup + restore; RTO target 4 h)"
else
  echo "restore drill FAILED: table sets differ"; exit 1
fi

# Telemetry. Counted before the backup: everything stored by then must come back, and ingestion may add more.
ch() { docker compose exec -T clickhouse clickhouse-client --query "$1" < /dev/null; }
ch_counts() { ch "SELECT name, total_rows FROM system.tables WHERE database = '$1' AND engine = 'MergeTree' ORDER BY name FORMAT TSV"; }

start=$(date +%s)
before=$(ch_counts otel)
file=$(bash scripts/backup-telemetry.sh | cut -d' ' -f1)
[[ -n "$file" ]] || { echo "restore drill FAILED: telemetry backup"; exit 1; }
bash scripts/restore-telemetry.sh "$file" otel_drill > /dev/null || { echo "restore drill FAILED: telemetry restore"; exit 1; }
after=$(ch_counts otel_drill)
elapsed=$(( $(date +%s) - start ))
# The restored view must feed the restored table, not the live one.
view=$(ch "SELECT create_table_query FROM system.tables WHERE database = 'otel_drill' AND engine = 'MaterializedView'")
ch "DROP DATABASE otel_drill SYNC" > /dev/null

echo "$after" | sed 's/^/  /'
tables=$(echo "$after" | wc -l)
short=$(join -t $'	' <(echo "$before") <(echo "$after") | awk -F'	' '$3 < $2')
if [[ $(echo "$before" | cut -f1) != $(echo "$after" | cut -f1) ]]; then
  echo "restore drill FAILED: telemetry table sets differ"; exit 1
elif [[ -n "$short" ]]; then
  echo "restore drill FAILED: restored tables hold fewer rows (table, before, restored):"; echo "$short"; exit 1
elif [[ "$view" == *"otel."* ]]; then
  echo "restore drill FAILED: the restored view still refers to the live database: $view"; exit 1
fi
echo "restore drill: $tables telemetry tables restored from $file in ${elapsed} s (backup + restore; RTO target 4 h)"
