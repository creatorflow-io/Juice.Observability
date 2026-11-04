#!/usr/bin/env bash
# Backs up the telemetry database (ClickHouse otel) to backups/clickhouse/otel-<UTC time>.zip, a full backup each time.
# Schedule it alongside scripts/backup.sh and copy the file off the host; it holds every tenant's telemetry.
# Ingestion and queries keep running during the backup.
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
mkdir -p backups/clickhouse
# On Linux a bind-mounted directory created by Docker or this script is not writable by the clickhouse user.
docker compose exec -T -u root clickhouse chown clickhouse /backups < /dev/null
name=otel-$(date -u +%Y%m%dT%H%M%SZ).zip
docker compose exec -T clickhouse clickhouse-client --receive_timeout=86400 \
  --query "BACKUP DATABASE otel TO File('/backups/$name')" < /dev/null > /dev/null
file=backups/clickhouse/$name
echo "$file ($(wc -c < "$file") bytes)"
