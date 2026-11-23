#!/usr/bin/env bash
# C11 Lab 4: psql against the AppHost's PostgreSQL container (compose.yaml is gone). Runs as the container's superuser,
# with the password from the container's own environment, so no password appears here or in a process list.
# Usage: scripts/local-psql.sh [psql options...]   e.g. scripts/local-psql.sh -c "select 1"   or   ... < file.sql
set -euo pipefail
container=$(docker ps --filter "name=^postgres-" --filter status=running --format '{{.Names}}' | head -1)
[ -n "$container" ] || { echo "No running AppHost PostgreSQL container (start it with: dotnet aspire run)." >&2; exit 1; }
exec docker exec -i -e PGDATABASE="${PGDATABASE:-homebuild}" "$container" sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" exec psql -h localhost -U postgres -d "$PGDATABASE" -v ON_ERROR_STOP=1 "$@"' psql "$@"
