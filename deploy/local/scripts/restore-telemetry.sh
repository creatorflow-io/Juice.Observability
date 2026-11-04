#!/usr/bin/env bash
# Restores a telemetry backup into a database (default: otel_restore, never the live one unless named).
#   scripts/restore-telemetry.sh backups/clickhouse/<file>.zip [database]
# To replace the live database: stop the Collector and the API, restore into otel, start them. Telemetry received since
# the backup is lost, and the Collector recreates nothing: the restore brings the tables and their retention back.
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
file=$1
database=${2:-otel_restore}
name=$(basename "$file")
[[ "$name" =~ ^[A-Za-z0-9._-]+\.zip$ ]] || { echo "invalid backup file name"; exit 1; }
[[ "$database" =~ ^[a-z_][a-z0-9_]*$ ]] || { echo "invalid database name"; exit 1; }
[[ -f backups/clickhouse/$name ]] || { echo "no such backup: backups/clickhouse/$name"; exit 1; }
ch() { docker compose exec -T clickhouse clickhouse-client --receive_timeout=86400 --query "$1" < /dev/null; }
ch "DROP DATABASE IF EXISTS $database SYNC"
ch "RESTORE DATABASE otel AS $database FROM File('/backups/$name')" > /dev/null
echo "restored $file into $database"
