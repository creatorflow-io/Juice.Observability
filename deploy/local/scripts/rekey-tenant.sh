#!/usr/bin/env bash
# Moves this deployment's telemetry from its old tenant name to its tenant Id (the tenant pattern, juice-docs decision record):
# every row stamped ResourceAttributes['juice.platform_tenant_id'] = FROM is rewritten to TO, and its JuiceTenant column with it.
#   scripts/rekey-tenant.sh local-dev 0193f0aa-0000-7000-8000-0000000000a1
#
# The whole move, in order (docs/deployment.md, "Moving to a tenant Id"):
#   1. Back up (scripts/backup.sh, scripts/backup-telemetry.sh).
#   2. In .env: JUICE_PLATFORM_PREVIOUS_TENANT_ID=<old name>, JUICE_PLATFORM_TENANT_ID=<the Id>; update the shared Keycloak
#      (dev-infra scripts/keycloak-update.sh), whose platform_tenant claims name the tenant.
#   3. docker compose up -d: the Collector stamps the Id from now on, Grafana's row policy follows it, and the API renames the tenant
#      at start (Platform:PreviousTenantKey) and tells enrolled agents the new one.
#   4. This script, for what was stamped before: run it once the Collector stamps the Id, so no old name arrives after it.
#   5. Once every agent reports the new tenant (agents.reported_tenant), remove JUICE_PLATFORM_PREVIOUS_TENANT_ID.
#
# A mutation per table, run to its end (mutations_sync = 2); safe to run again: a second run finds nothing to rewrite.
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1

from=${1:-}
to=${2:-}
# The same rules as the API's (TenantProvisioner) and init-users.sh, so only these values reach the SQL.
if [[ ! "$from" =~ ^[a-z0-9][a-z0-9-]{1,62}$ ]]; then
  echo "usage: $0 <old tenant name> <tenant Id>: the old name '$from' isn't a tenant name" >&2
  exit 2
fi
if [[ ! "$to" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || [[ "$to" == 00000000-0000-0000-0000-000000000000 ]]; then
  echo "usage: $0 <old tenant name> <tenant Id>: '$to' isn't a tenant Id (a GUID, lowercase, with hyphens)" >&2
  exit 2
fi
if [[ "$from" == juice-platform ]]; then
  echo "juice-platform is the platform's own telemetry, not a tenant" >&2
  exit 2
fi

# The tables with the stamp: the same list as clickhouse/init-users.sh and clickhouse/resource-columns.sh.
tables="otel_traces otel_logs otel_metrics_gauge otel_metrics_sum otel_metrics_histogram otel_metrics_exponential_histogram otel_metrics_summary"

query() {
  docker compose exec -T clickhouse sh -c 'clickhouse client --user "$CLICKHOUSE_USER" --password "$CLICKHOUSE_PASSWORD" --multiquery' <<< "$1"
}

for table in $tables; do
  before=$(query "SELECT count() FROM otel.$table WHERE ResourceAttributes['juice.platform_tenant_id'] = '$from'")
  if [[ "$before" == 0 ]]; then
    echo "otel.$table: nothing stamped $from"
    continue
  fi
  # JuiceTenant is MATERIALIZED from the map: a mutation of the map recomputes it in the parts it rewrites.
  query "ALTER TABLE otel.$table UPDATE ResourceAttributes = mapUpdate(ResourceAttributes, map('juice.platform_tenant_id', '$to'))
    WHERE ResourceAttributes['juice.platform_tenant_id'] = '$from' SETTINGS mutations_sync = 2"
  left=$(query "SELECT count() FROM otel.$table WHERE ResourceAttributes['juice.platform_tenant_id'] = '$from' OR JuiceTenant = '$from'")
  if [[ "$left" != 0 ]]; then
    # A part whose column wasn't recomputed: materialize it again from the rewritten map.
    query "ALTER TABLE otel.$table MATERIALIZE COLUMN JuiceTenant SETTINGS mutations_sync = 2"
    left=$(query "SELECT count() FROM otel.$table WHERE ResourceAttributes['juice.platform_tenant_id'] = '$from' OR JuiceTenant = '$from'")
  fi
  if [[ "$left" != 0 ]]; then
    echo "otel.$table: $left rows still name $from" >&2
    exit 1
  fi
  echo "otel.$table: $before rows now stamped $to"
done
