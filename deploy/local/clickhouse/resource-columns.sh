#!/bin/bash
# The resource attributes the platform reads, as columns of their own on the Collector's tables (Phase 11 R12, docs/scale-tests.md,
# Phase 11 fixes §8). Looking a key up in the ResourceAttributes map row by row was most of what the workers' queries cost; reading a
# LowCardinality column of the same value cut their ClickHouse CPU by about 72 %, for about 0.07 cores at insert and in merges at 1×.
# Each column is MATERIALIZED from the map: ClickHouse fills it on insert, so the Collector doesn't change, and a part written before
# the column existed computes it from the map when read (no rewrite of old data, the same answers). The platform's queries and
# Grafana's row policy (init-users.sh) read the columns, so they must exist before the platform or Grafana starts.
#
# The exporter creates its tables when the Collector starts (create_schema), after clickhouse-access has made its user, so this runs
# after it: the clickhouse-columns service in docker-compose.yml, once the Collector has started, waits for the tables, then adds what
# is missing. Every statement is idempotent (ADD COLUMN IF NOT EXISTS is a metadata change, quick on any amount of data).
# The same list is ResourceColumns in src/Juice.Observability.Discovery; a test fails if they differ. tests/verify.sh checks the tables.
set -euo pipefail

host=${CLICKHOUSE_HOST:-localhost}
client=(clickhouse client --host "$host" --user "$CLICKHOUSE_USER" --password "$CLICKHOUSE_PASSWORD")

# key column: the column holds ResourceAttributes['key'].
columns="juice.platform_tenant_id JuiceTenant
service.namespace JuiceServiceNamespace
deployment.environment.name JuiceEnvironment
service.instance.id JuiceServiceInstance
service.version JuiceServiceVersion
host.name JuiceHostName
host.id JuiceHostId
process.pid JuiceProcessPid
container.id JuiceContainerId
k8s.pod.uid JuicePodUid
k8s.namespace.name JuicePodNamespace
k8s.pod.name JuicePodName"

# The tables that carry resource attributes: the same list as init-users.sh's row policies.
tables="otel_traces otel_logs otel_metrics_gauge otel_metrics_sum otel_metrics_histogram otel_metrics_exponential_histogram otel_metrics_summary"

for table in $tables; do
  for attempt in $(seq 1 90); do
    [[ $("${client[@]}" -q "EXISTS TABLE otel.$table" 2>/dev/null) == 1 ]] && break
    (( attempt == 90 )) && { echo "otel.$table did not appear in 180 s: is the Collector running and able to write?" >&2; exit 1; }
    sleep 2
  done
done

alter=""
while read -r key column; do
  alter+="ADD COLUMN IF NOT EXISTS $column LowCardinality(String) MATERIALIZED ResourceAttributes['$key'],"
done <<< "$columns"
for table in $tables; do
  "${client[@]}" -q "ALTER TABLE otel.$table ${alter%,}"
done
echo "resource columns present on ${tables// /, }"
