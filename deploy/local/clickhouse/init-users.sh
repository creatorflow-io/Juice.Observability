#!/bin/bash
# Runs on the first start of an empty ClickHouse volume (docker-entrypoint-initdb.d), and again on every start of the stack
# (the clickhouse-access service in docker-compose.yml, against CLICKHOUSE_HOST), so an existing volume gets what is added
# here too. Every statement is idempotent.
# Least-privilege users (requirements §5.1, §5.2):
#   otel_writer     Collector exporter: creates the otel tables (create_schema) and inserts.
#   grafana_reader  Grafana data source: read-only, otel database only, bounded queries, this deployment's tenant only.
#   platform_reader Platform discovery/health/rules workers (Track B): read-only, otel database only, bounded queries.
#                   The platform filters by tenant in every query (the API's tenant scope); its row policy lets every row through.
set -euo pipefail
# The deployment tenant, for the Grafana row policy below. The same rule as scripts/init.sh, so only a plain key reaches the SQL.
tenant=${JUICE_PLATFORM_TENANT_ID:-}
if [[ ! "$tenant" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
  echo "JUICE_PLATFORM_TENANT_ID must be set to a plain key (a letter or digit, then letters, digits, . _ -), not '$tenant'" >&2
  exit 1
fi

host=${CLICKHOUSE_HOST:-localhost}
# From another container (clickhouse-access): on a fresh volume ClickHouse first runs this script on a temporary server that
# listens on 127.0.0.1 only, and its healthcheck (inside the container) can pass then. Wait until it answers on the network.
if [[ "$host" != localhost ]]; then
  for attempt in $(seq 1 60); do
    clickhouse client --host "$host" --user "$CLICKHOUSE_USER" --password "$CLICKHOUSE_PASSWORD" -q "SELECT 1" >/dev/null 2>&1 && break
    (( attempt == 60 )) && { echo "ClickHouse at $host did not answer in 120 s" >&2; exit 1; }
    sleep 2
  done
fi

# The tables that carry the Collector's tenant stamp (ResourceAttributes['juice.platform_tenant_id']), for both row policies
# below. A table the Collector adds later goes here; tests/verify.sh fails until it does.
stamped="otel.otel_traces otel.otel_logs otel.otel_metrics_gauge otel.otel_metrics_sum otel.otel_metrics_histogram
    otel.otel_metrics_exponential_histogram otel.otel_metrics_summary"

# Grafana's queries don't filter by tenant, and one ClickHouse can hold more than one tenant's telemetry (the local stack's test
# Collector writes local-test beside it). Each table that carries the Collector's tenant stamp gets a policy, so Grafana sees only
# this deployment's rows whatever it asks. A policy names its table (a database-wide one would fail on otel_traces_trace_id_ts,
# which has no ResourceAttributes). That index, the Collector's (trace ID, start, end) table, and its materialized view
# otel_traces_trace_id_ts_mv stay readable: Grafana's trace view reads a trace's time range from the index. They hold no span
# content, but Grafana can list the trace IDs and times of every tenant in this ClickHouse from them (docs/phase-10-console.md,
# accepted by the owner). tests/verify.sh fails if a table with the stamp has no policy, or a policy says something else. Taking a
# table out of the list leaves its old policies: DROP ROW POLICY them by hand.
# The other users get an explicit policy that lets every row through, so what they read doesn't hang on ClickHouse's rule for users
# without one (clickhouse/access.xml sets it to hide rows, so a missing policy fails closed).
# Each policy is created if missing, then ALTERed in place: CREATE ... OR REPLACE removes and re-adds it, a moment in which
# grafana_reader would have none. all_tenants goes first, so grafana_reader is never the only user left without one.
policies=""
for table in $stamped; do
  policies+="CREATE ROW POLICY IF NOT EXISTS all_tenants ON $table USING 1 TO otel_writer, platform_reader, ${CLICKHOUSE_USER};
ALTER ROW POLICY all_tenants ON $table USING 1 TO otel_writer, platform_reader, ${CLICKHOUSE_USER};
CREATE ROW POLICY IF NOT EXISTS grafana_tenant ON $table USING ResourceAttributes['juice.platform_tenant_id'] = '${tenant}' TO grafana_reader;
ALTER ROW POLICY grafana_tenant ON $table USING ResourceAttributes['juice.platform_tenant_id'] = '${tenant}' TO grafana_reader;
"
done

clickhouse client --host "$host" --user "$CLICKHOUSE_USER" --password "$CLICKHOUSE_PASSWORD" --multiquery <<SQL
CREATE DATABASE IF NOT EXISTS otel;

-- readonly = 2: no writes or DDL, but clients may set query settings (the Grafana plugin sets a timeout);
-- MAX caps every bound so a client cannot raise it.
-- CREATE ... IF NOT EXISTS leaves an existing profile or user as it was, so each one is then ALTERed to what this script says: a
-- changed cap, or a password rotated in .env, reaches an existing volume too. (OR REPLACE would recreate a user, dropping its grants
-- and row-policy assignments.) A GRANT removed from this script is not revoked: REVOKE it by hand.
CREATE SETTINGS PROFILE IF NOT EXISTS bounded_reader;
ALTER SETTINGS PROFILE bounded_reader SETTINGS
    readonly = 2,
    max_execution_time = 60 MAX 60,
    max_result_rows = 1000000 MAX 1000000,
    max_memory_usage = 4000000000 MAX 4000000000;

CREATE USER IF NOT EXISTS otel_writer IDENTIFIED WITH sha256_password BY '${CLICKHOUSE_OTEL_PASSWORD}';
ALTER USER otel_writer IDENTIFIED WITH sha256_password BY '${CLICKHOUSE_OTEL_PASSWORD}';
GRANT CREATE TABLE, CREATE DATABASE, ALTER TABLE, INSERT, SELECT ON otel.* TO otel_writer;

CREATE USER IF NOT EXISTS grafana_reader IDENTIFIED WITH sha256_password BY '${CLICKHOUSE_GRAFANA_PASSWORD}'
    SETTINGS PROFILE bounded_reader;
ALTER USER grafana_reader IDENTIFIED WITH sha256_password BY '${CLICKHOUSE_GRAFANA_PASSWORD}' SETTINGS PROFILE bounded_reader;
GRANT SELECT ON otel.* TO grafana_reader;

CREATE USER IF NOT EXISTS platform_reader IDENTIFIED WITH sha256_password BY '${CLICKHOUSE_PLATFORM_PASSWORD}'
    SETTINGS PROFILE bounded_reader;
ALTER USER platform_reader IDENTIFIED WITH sha256_password BY '${CLICKHOUSE_PLATFORM_PASSWORD}' SETTINGS PROFILE bounded_reader;
GRANT SELECT ON otel.* TO platform_reader;

${policies}
SQL
