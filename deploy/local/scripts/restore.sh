#!/usr/bin/env bash
# Restores a backup into a database (default: juice_observability_restore, never the live one unless named).
#   scripts/restore.sh backups/<file>.dump [database]
# To replace the live database: stop the API, restore into juice_observability, start the API.
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
file=$1
database=${2:-juice_observability_restore}
[[ "$database" =~ ^[a-z_][a-z0-9_]*$ ]] || { echo "invalid database name"; exit 1; }
docker compose exec -T postgres psql -U juice -d postgres -v ON_ERROR_STOP=1 -q \
  -c "DROP DATABASE IF EXISTS $database WITH (FORCE)" -c "CREATE DATABASE $database OWNER juice" < /dev/null
docker compose exec -T postgres pg_restore -U juice -d "$database" --no-owner --exit-on-error < "$file"
echo "restored $file into $database"
