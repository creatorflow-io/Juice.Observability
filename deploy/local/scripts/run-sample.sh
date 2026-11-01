#!/usr/bin/env bash
# Runs the Juice observability sample (Juice repo, samples/Juice.Samples.Observability) against the local stack:
# OTLP over TLS with this stack's CA and token. Extra arguments go to the sample (for example --no-demo).
#   JUICE_REPO   path to the Juice repository (default: ../../../Juice, next to Juice.Observability)
set -euo pipefail
cd "$(dirname "$0")/.."

repo=${JUICE_REPO:-../../../Juice}
here=$(pwd -W 2>/dev/null || pwd)

export OTEL_EXPORTER_OTLP_ENDPOINT=https://localhost:4317
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer $(cut -d' ' -f1 secrets/otlp-tokens)"
export OTEL_EXPORTER_OTLP_CERTIFICATE="$here/secrets/tls/ca.crt"
export OTEL_METRIC_EXPORT_INTERVAL=${OTEL_METRIC_EXPORT_INTERVAL:-10000}
export OTEL_RESOURCE_ATTRIBUTES=${OTEL_RESOURCE_ATTRIBUTES:-deployment.environment.name=local}

exec dotnet run --project "$repo/samples/Juice.Samples.Observability" -- "$@"
