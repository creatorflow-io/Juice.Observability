#!/usr/bin/env bash
# Applies clickhouse/init-users.sh to the running ClickHouse now: the users, their grants and Grafana's tenant row policy; then
# clickhouse/resource-columns.sh, the resource columns on the Collector's tables.
# The stack does this by itself on every start (the clickhouse-access service); this is for applying a change without a
# restart, and prints the policies in place. Every statement in the script is idempotent.
#   scripts/apply-clickhouse-access.sh
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
# Recreates ClickHouse only if its configuration changed; data stays in its volume.
docker compose up -d --wait clickhouse
docker compose up -d --wait --force-recreate --no-deps clickhouse-access
docker compose up -d --wait --force-recreate --no-deps clickhouse-columns
docker compose exec -T clickhouse sh -c 'clickhouse client --user "$CLICKHOUSE_USER" --password "$CLICKHOUSE_PASSWORD" -q "SELECT short_name, arraySort(groupArray(table)) AS tables FROM system.row_policies GROUP BY short_name ORDER BY short_name FORMAT PrettyCompact"'
