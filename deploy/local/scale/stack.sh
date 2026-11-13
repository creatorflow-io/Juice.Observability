#!/usr/bin/env bash
# The scale stack (scale/compose.scale.yml): the local stack under its own compose project, with the scale rules, SLOs and a webhook sink.
#   scale/stack.sh up        build and start it (refuses, exit 3, while the working stack holds the ports: stop that one first, its data stays)
#   scale/stack.sh down      stop and remove its containers; its volumes stay for the next run
#   scale/stack.sh destroy   also remove its volumes: the next up starts from empty, as on a new host
#   scale/stack.sh ps
set -euo pipefail
cd "$(dirname "$0")/.."
source scale/lib.sh
case "${1:-}" in
  up)
    working=$(docker compose -p "$COMPOSE_PROJECT_NAME" ps -q --status running 2>/dev/null | wc -l)
    if (( working > 0 )) && [[ "$COMPOSE_PROJECT_NAME" != "$SCALE_PROJECT" ]]; then
      echo "the working stack ($COMPOSE_PROJECT_NAME) is running and holds the same ports. Stop it first (its volumes stay):" >&2
      echo "  docker compose --profile platform --profile test --profile grafana --profile agent stop" >&2
      exit 3
    fi
    dc up -d --wait --build
    dc ps --format '{{.Service}}\t{{.Status}}' ;;
  down) dc down ;;
  destroy) dc down -v ;;
  ps) dc ps --format '{{.Service}}\t{{.Status}}' ;;
  *) sed -n 2,7p "$0"; exit 2 ;;
esac
