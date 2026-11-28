#!/bin/sh
# The OTLP client settings come from the standard OpenTelemetry environment variables. The bearer token is read from the
# mounted file (the first token of otlp-tokens: its first word), so it is not in the compose file or the image.
set -eu
token=$(cut -d' ' -f1 "${SAMPLE_OTLP_TOKEN_FILE:-/run/otlp-tokens}" | head -n 1)
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer%20${token}"
exec opentelemetry-instrument python /app/app.py
