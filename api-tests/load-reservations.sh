#!/usr/bin/env bash
# Sends 12 reservation requests in a row from one client and prints each status.
# The 11th should be 429 with Retry-After (limit: 10 per minute per client IP).
# Usage: ./load-reservations.sh [base-url]   (default http://localhost:5104/v1)
set -u
base=${1:-http://localhost:5104/v1}
unit=0198a000-0000-7000-8001-000000000002
body='{"buyerId":"11111111-1111-1111-1111-111111111111","deposit":5000,"currency":"GBP"}'
for i in $(seq 1 12); do
  out=$(curl -s -D - -o /dev/null -X POST "$base/units/$unit/reservations" -H 'content-type: application/json' -d "$body")
  status=$(printf '%s' "$out" | head -1 | tr -d '\r')
  retry=$(printf '%s' "$out" | grep -i '^retry-after:' | tr -d '\r')
  printf '%2d  %s  %s\n' "$i" "$status" "$retry"
done
