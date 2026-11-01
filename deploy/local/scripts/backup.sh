#!/usr/bin/env bash
# Backs up the platform database (PostgreSQL, custom format) to backups/juice_observability-<UTC time>.dump.
# Schedule it daily (cron, Task Scheduler) for the requirements §9 RPO of 24 hours; copy the file off the host.
# Telemetry in ClickHouse is not in this backup (see README.md, Backups).
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
mkdir -p backups
file=backups/juice_observability-$(date -u +%Y%m%dT%H%M%SZ).dump
docker compose exec -T postgres pg_dump -U juice -d juice_observability --format=custom --no-owner < /dev/null > "$file"
echo "$file ($(wc -c < "$file") bytes)"
