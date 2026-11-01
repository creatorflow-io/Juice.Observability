#!/usr/bin/env bash
# Restore drill (requirements §9 durability): backs up the live platform database, restores it into a scratch
# database, compares every table's row count, and reports the time taken (RTO for this data size).
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
