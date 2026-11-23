#!/usr/bin/env bash
# C11 Lab 2: start each notifications image N times and time "docker run" to the first message handled; then read the
# idle memory. The image's Lambda runtime loop talks to the Runtime Interface Emulator (tools/aws-lambda-rie-arm64,
# mounted as the entry point), which turns an HTTP POST into an invocation. Each handled message writes one
# notification to S3 (notifications/{tenant}/units/{unit}/{messageId}.json); the keys are printed for clean-up.
# Usage: AWS_PROFILE=claude scripts/measure-notifications-images.sh <bucket> [runs-per-image]
set -euo pipefail
cd "$(dirname "$0")/.."
bucket=$1; runs=${2:-5}
tenant=0198a000-0000-7000-8000-00000000000a; unit=0198a000-0000-7000-8001-000000000001
ms() { python3 -c 'import time; print(int(time.time()*1000))'; }
for kind in aot jit; do
  case $kind in aot) cmd=(/app/bootstrap);; jit) cmd=(/usr/bin/dotnet /app/bootstrap.dll);; esac
  for run in $(seq 1 "$runs"); do
    id=$(uuidgen | tr 'A-Z' 'a-z'); name=hb-notif-$kind
    body=$(printf '{"unitId":"%s","tenantId":"%s","milestone":"Roof","completedAt":"2026-11-23","version":3}' "$unit" "$tenant")
    event=$(python3 -c 'import json,sys; print(json.dumps({"Records":[{"messageId":"m-1","receiptHandle":"r","body":sys.argv[1],"attributes":{"ApproximateReceiveCount":"1"},"messageAttributes":{"type":{"stringValue":"MilestoneCompleted","dataType":"String"},"message_id":{"stringValue":sys.argv[2],"dataType":"String"}},"eventSource":"aws:sqs"}]}))' "$body" "$id")
    docker rm -f $name >/dev/null 2>&1 || true
    t0=$(ms)
    docker run -d --name $name -p 19000:8080 --env-file <(aws configure export-credentials --format env-no-export) \
      -e AWS_REGION=eu-west-2 -e NOTIFICATIONS_BUCKET="$bucket" -e PARTIAL_BATCH_RESPONSES=true \
      -v "$PWD/tools/aws-lambda-rie-arm64:/rie:ro" --entrypoint /rie homebuild-notifications-$kind:latest "${cmd[@]}" >/dev/null
    until out=$(curl -sf -XPOST localhost:19000/2015-03-31/functions/function/invocations -d "$event" 2>/dev/null); do sleep 0.05; done
    t1=$(ms)
    sleep 5
    mem=$(docker stats --no-stream --format '{{.MemUsage}}' $name | cut -d/ -f1 | tr -d ' ')
    echo "$kind run $run: first message handled after $((t1 - t0)) ms; idle memory $mem; response $out; key notifications/$tenant/units/$unit/$id.json"
    docker logs $name 2>&1 | grep -E "processed|failed" | sed 's/^/    /' | head -2
    docker rm -f $name >/dev/null
  done
done
