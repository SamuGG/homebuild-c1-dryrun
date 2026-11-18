#!/usr/bin/env bash
# C8 Lab 5: runs the race script against two local API instances over the compose database.
#   tools/RaceScript/race.sh A|D [code-dir]
# code-dir: the checkout whose API to run (default: this one; e.g. a worktree of main for the "before" run).
# Generates a throwaway signing key and webhook secret per run (never printed), gives the race buyers a tenant
# membership, resets Riverside P1, starts the API twice (5181, 5182), runs the scenario, prints what the database holds.
set -euo pipefail
scenario=${1:?A or D}
repo=$(cd "$(dirname "$0")/../.." && pwd)
code=$(cd "${2:-$repo}" && pwd)
unit=0198a000-0000-7000-8001-000000000001   # Riverside P1
tenant=0198a000-0000-7000-8000-00000000000a
psql() { docker compose -f "$repo/compose.yaml" exec -T postgres psql -q -t -A -U homebuild -d homebuild -v ON_ERROR_STOP=1 -c "$1"; }

export RACE_SIGNING_KEY=$(openssl rand -base64 32)
export RACE_WEBHOOK_SECRET="whsec_race_$(openssl rand -hex 16)"

values=$(for i in $(seq -w 1 20); do printf "('0198a000-0000-7000-9000-0000000b00%s','%s','{buyer}')," "$i" "$tenant"; done)
psql "INSERT INTO accounts.tenant_memberships (subject, tenant_id, roles) VALUES ${values%,} ON CONFLICT (subject) DO NOTHING;"
psql "DELETE FROM sales.reservations WHERE unit_id = '$unit'; UPDATE sales.units SET status = 'Available' WHERE id = '$unit';"

pids=()
cleanup() { for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done; }
trap cleanup EXIT
for port in 5181 5182; do
  ASPNETCORE_ENVIRONMENT=Development \
  Authentication__Schemes__Bearer__ValidIssuer=dotnet-user-jwts \
  Authentication__Schemes__Bearer__SigningKeys__0__Issuer=dotnet-user-jwts \
  Authentication__Schemes__Bearer__SigningKeys__0__Value="$RACE_SIGNING_KEY" \
  Authentication__Schemes__Bearer__SigningKeys__0__Length=32 \
  Tokens__AllowedClientIds__0=homebuild-race \
  Payments__WebhookSecret="$RACE_WEBHOOK_SECRET" \
  Logging__LogLevel__Default=Warning \
  dotnet run --no-build --no-launch-profile --project "$code/src/HomeBuild.Api" --urls "http://localhost:$port" > "${TMPDIR:-/tmp}/race-api-$port.log" 2>&1 &
  pids+=($!)
done
for port in 5181 5182; do
  for i in $(seq 1 60); do curl -sf -o /dev/null "http://localhost:$port/openapi/v1.json" && break; sleep 1; done
done
echo "API from: $(git -C "$code" log --oneline -1) ($(git -C "$code" status --porcelain | wc -l | tr -d " ") uncommitted changes)"

out=$(dotnet run --no-build --project "$repo/tools/RaceScript" -- "$scenario" --api http://localhost:5181,http://localhost:5182 --unit "$unit" --tenant "$tenant")
echo "$out" | grep -v '^SESSION='
case $scenario in
  A) echo "database: active reservations on P1: $(psql "SELECT count(*) FROM sales.reservations WHERE unit_id = '$unit' AND status IN ('held','confirmed');")" ;;
  D) session=$(echo "$out" | sed -n 's/^SESSION=//p')
     echo "database: deposits for $session: $(psql "SELECT count(*) FROM payments.deposits WHERE session_id = '$session';"); reservation: $(psql "SELECT split_part(state, '|', 1) FROM sales.reservations WHERE unit_id = '$unit' ORDER BY created_at DESC LIMIT 1;")" ;;
esac
