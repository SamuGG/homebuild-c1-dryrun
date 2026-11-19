#!/usr/bin/env bash
# C9 Lab 7: build the notifications Lambda twice, into artifacts/:
#   notifications-aot.zip  Native AOT, compiled in the Amazon Linux 2023 arm64 container (build/lambda-aot.Dockerfile)
#   notifications-jit.zip  the same handler, self-contained with the JIT (for the cold-start comparison)
# Both are a single executable named bootstrap for the provided.al2023 runtime.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p artifacts
docker image inspect homebuild-lambda-aot >/dev/null 2>&1 || docker build --platform linux/arm64 -t homebuild-lambda-aot -f build/lambda-aot.Dockerfile build
# The repository is copied in without bin/ and obj/ (the Mac's restore files would not fit Linux). Restore is not
# locked here: the linux-arm64 native compiler package is added to the copy's lock file, never to the repository's.
docker run --rm --platform linux/arm64 -v "$PWD":/repo:ro -v "$PWD/artifacts":/out -v homebuild-lambda-nuget:/root/.nuget/packages homebuild-lambda-aot bash -c '
  set -euo pipefail
  tar -C /repo --exclude="*/bin" --exclude="*/obj" --exclude=node_modules --exclude=artifacts --exclude=.git -cf - \
      Directory.Build.props Directory.Packages.props global.json .editorconfig src/HomeBuild.Domain src/HomeBuild.Generators src/HomeBuild.Notifications.Lambda 2>/dev/null | tar -xf -
  for kind in aot jit; do
    # AOT comes from the project (PublishAot when a runtime is given); passing it globally would reach the analyzer
    # project too (NETSDK1207). The JIT build switches it off.
    jit=$([ $kind = jit ] && echo "-p:PublishAot=false" || echo "")
    rm -rf /tmp/$kind
    dotnet publish src/HomeBuild.Notifications.Lambda -c Release -r linux-arm64 $jit --self-contained \
      -p:RestoreLockedMode=false -o /tmp/$kind
    (cd /tmp/$kind && rm -f *.pdb *.dbg && ls -la bootstrap && rm -f /out/notifications-$kind.zip && zip -qr /out/notifications-$kind.zip .)
  done
  ls -la /out/*.zip
'
