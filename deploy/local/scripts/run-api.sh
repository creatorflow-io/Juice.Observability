#!/usr/bin/env bash
# Runs the platform API (src/Juice.Observability.Host) against the local stack on http://localhost:5180.
# Applies migrations on start (Database:Migrate) and provisions the tenant from JUICE_PLATFORM_TENANT_ID.
set -euo pipefail
cd "$(dirname "$0")/.."
source .env

export ASPNETCORE_URLS=${ASPNETCORE_URLS:-http://localhost:5180}
export ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT:-Development}
# 127.0.0.1, not localhost: these ports are published on IPv4 only, and on Windows .NET waits ~2 s on ::1 first.
export ConnectionStrings__Platform="Host=127.0.0.1;Port=5433;Database=juice_observability;Username=juice;Password=$POSTGRES_PASSWORD"
export ClickHouse__ConnectionString="Host=127.0.0.1;Port=8123;Username=platform_reader;Password=$CLICKHOUSE_PLATFORM_PASSWORD;Database=otel"
export Platform__TenantKey=$JUICE_PLATFORM_TENANT_ID
export Auth__Authority=http://localhost:8080/realms/juice
export Auth__RequireHttpsMetadata=false   # local Keycloak serves plain HTTP
export Database__Migrate=true
export Rules__File="$(pwd -W 2>/dev/null || pwd)/rules.json"
export Webhooks__File="$(pwd -W 2>/dev/null || pwd)/webhooks.json"
export Integrations__Grafana__BaseUrl=http://localhost:3000

exec dotnet run --project ../../src/Juice.Observability.Host --no-launch-profile -- "$@"
