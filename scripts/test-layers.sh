#!/usr/bin/env bash
# Runs each test layer and reports its time against its budget (docs/testing.md): a table in the GitHub job summary
# in CI, or on the terminal locally. A failing layer fails the script; a layer over budget is a warning to act on,
# not a failure, so a slow day never turns CI red by itself.
# Usage: scripts/test-layers.sh   (after a build; CONFIGURATION defaults to Release, as in CI)
set -uo pipefail

config=${CONFIGURATION:-Release}
summary=${GITHUB_STEP_SUMMARY:-/dev/stdout}
now() { python3 -c 'import time; print(time.time())'; }

# name | budget in seconds | command
layers=(
  "Unit (domain rules)|10|dotnet test --no-build -c $config --project tests/HomeBuild.Domain.Tests"
  "Web components and in-memory host (fake API)|20|dotnet test --no-build -c $config --project tests/HomeBuild.Web.Tests"
  "Integration (real PostgreSQL, includes boundary tests)|90|dotnet test --no-build -c $config --project tests/HomeBuild.Api.Tests"
  "Contract (OpenAPI documents unchanged)|10|git add -N src/HomeBuild.Api/openapi*.json && git diff --exit-code -- src/HomeBuild.Api/openapi*.json"
)

rows=()
status=0
for layer in "${layers[@]}"; do
  IFS='|' read -r name budget command <<< "$layer"
  echo "::group::$name"
  start=$(now)
  output=$(bash -c "$command" 2>&1)
  code=$?
  seconds=$(python3 -c "print(f'{$(now) - $start:.1f}')")
  echo "$output"
  echo "::endgroup::"
  # Strip colour codes: in CI the runner's lines start with one.
  tests=$(echo "$output" | sed -E 's/\x1b\[[0-9;]*m//g' | awk '/^ *total:/ {print $2}' | tail -1)
  if [ $code -ne 0 ]; then
    verdict="❌ failed"
    status=1
  elif python3 -c "import sys; sys.exit(0 if $seconds <= $budget else 1)"; then
    verdict="✅ within budget"
  else
    verdict="⚠️ over budget"
    echo "::warning title=Test budget::$name took ${seconds} s; its budget is ${budget} s (docs/testing.md)."
  fi
  rows+=("| $name | ${tests:--} | ${seconds} s | ${budget} s | $verdict |")
done

table() {
  echo "### Test layers ($config)"
  echo
  echo "| Layer | Tests | Time | Budget | Result |"
  echo "| --- | --- | --- | --- | --- |"
  printf '%s\n' "${rows[@]}"
}
table >> "$summary"
# Also in the log, which anyone who can see the run can read.
if [ "$summary" != /dev/stdout ]; then
  table
fi
exit $status
