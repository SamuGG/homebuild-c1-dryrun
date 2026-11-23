#!/usr/bin/env bash
# C11 Lab 4: the AppHost's "migrations" resource. Applies C3's migration bundles, one per schema, in CI's order, to the
# connection in $MIGRATIONS_CONNECTION (the AppHost passes the PostgreSQL superuser's). Bundles are built for this
# machine (osx-arm64 on a Mac) into artifacts/ the first time, or with --rebuild after adding a migration.
set -euo pipefail
cd "$(dirname "$0")/.."
: "${MIGRATIONS_CONNECTION:?set by the AppHost}"
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
contexts="Catalogue Sales Accounts Construction Payments Idempotency Extras Aftercare"
build() { # project, context, name
  echo "building artifacts/migrate-$3 ($rid)"
  dotnet ef migrations bundle --project "$1" --context "$2DbContext" --configuration Release --self-contained \
    --target-runtime "$rid" --output "artifacts/migrate-$3" --force >/dev/null
}
dotnet tool restore >/dev/null
for c in $contexts; do
  name=$(echo "$c" | tr '[:upper:]' '[:lower:]')
  [ "${1:-}" = --rebuild ] || [ ! -x "artifacts/migrate-$name" ] && build src/HomeBuild.Api "$c" "$name"
done
for c in Contracts Notifications; do
  name=$(echo "$c" | tr '[:upper:]' '[:lower:]')
  [ "${1:-}" = --rebuild ] || [ ! -x "artifacts/migrate-$name" ] && build src/HomeBuild.Workers "$c" "$name"
done
for name in catalogue sales accounts construction payments idempotency extras aftercare contracts notifications; do
  echo "applying migrate-$name"
  # The bundles log as JSON (the app's console format); keep the output, and fail if a bundle fails.
  if ! out=$("./artifacts/migrate-$name" --connection "$MIGRATIONS_CONNECTION" 2>&1); then echo "$out"; exit 1; fi
  echo "$out" | grep -oE '"Message":"[^"]*"' | sed 's/"Message":"//; s/"$//' | grep -E "Applying|No migrations|Done" || true
done
# The app role gets a login with the local-only password (C3's compose comment). No psql on the host: the client comes
# from the PostgreSQL image, reaching the published port through host.docker.internal.
kv() { echo "$MIGRATIONS_CONNECTION" | tr ';' '\n' | sed -n "s/^$1=//Ip" | head -1; }
docker run --rm -e PGPASSWORD="$(kv Password)" postgres:19 psql -q -h host.docker.internal -p "$(kv Port)" -U "$(kv Username)" -d "$(kv Database)" \
  -c "ALTER ROLE homebuild_app LOGIN PASSWORD 'homebuild-dev-app'"
echo "homebuild_app can log in"
echo "migrations done"
