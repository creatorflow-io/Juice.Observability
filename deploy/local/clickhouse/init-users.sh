#!/bin/bash
# Runs once, on the first start of an empty ClickHouse volume (docker-entrypoint-initdb.d).
# Least-privilege users (requirements §5.1, §5.2):
#   otel_writer     Collector exporter: creates the otel tables (create_schema) and inserts.
#   grafana_reader  Grafana data source: read-only, otel database only, bounded queries.
#   platform_reader Platform discovery/health/rules workers (Track B): read-only, otel database only, bounded queries.
set -euo pipefail

clickhouse client --user "$CLICKHOUSE_USER" --password "$CLICKHOUSE_PASSWORD" --multiquery <<SQL
CREATE DATABASE IF NOT EXISTS otel;

-- readonly = 2: no writes or DDL, but clients may set query settings (the Grafana plugin sets a timeout);
-- MAX caps every bound so a client cannot raise it.
CREATE SETTINGS PROFILE IF NOT EXISTS bounded_reader SETTINGS
    readonly = 2,
    max_execution_time = 60 MAX 60,
    max_result_rows = 1000000 MAX 1000000,
    max_memory_usage = 4000000000 MAX 4000000000;

CREATE USER IF NOT EXISTS otel_writer IDENTIFIED WITH sha256_password BY '${CLICKHOUSE_OTEL_PASSWORD}';
GRANT CREATE TABLE, CREATE DATABASE, ALTER TABLE, INSERT, SELECT ON otel.* TO otel_writer;

CREATE USER IF NOT EXISTS grafana_reader IDENTIFIED WITH sha256_password BY '${CLICKHOUSE_GRAFANA_PASSWORD}'
    SETTINGS PROFILE bounded_reader;
GRANT SELECT ON otel.* TO grafana_reader;

CREATE USER IF NOT EXISTS platform_reader IDENTIFIED WITH sha256_password BY '${CLICKHOUSE_PLATFORM_PASSWORD}'
    SETTINGS PROFILE bounded_reader;
GRANT SELECT ON otel.* TO platform_reader;
SQL
