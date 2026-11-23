#!/usr/bin/env bash
# C11 Lab 2: the notifications function (C9 Lab 7) as two container images, for comparison:
#   homebuild-notifications-aot  Native AOT, compiled for linux-arm64 inside the C9 build container (Amazon Linux 2023:
#                                AOT needs a Linux host for a Linux binary), on runtime-deps:11.0-resolute-chiseled
#   homebuild-notifications-jit  the same handler with the JIT, framework-dependent, on aspnet:11.0-resolute-chiseled
#                                (the brief says the ASP.NET Core image; the runtime image would do)
# Both are published by the SDK (/t:PublishContainer) to archives that are then loaded into Docker.
# Symbols stay out of the images (CopyOutputSymbolsToPublishDirectory=false): bootstrap.dbg alone is 21.8 MB.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p artifacts
rev=$(git rev-parse HEAD)
docker image inspect homebuild-lambda-aot >/dev/null 2>&1 || docker build --platform linux/arm64 -t homebuild-lambda-aot -f build/lambda-aot.Dockerfile build

# Both builds run in the C9 build container on a copy of the sources, so the runtime-specific restore never rewrites
# the committed lock files (CI restores --locked-mode). Each goes to an archive that is then loaded into Docker.
docker run --rm --platform linux/arm64 -e REV="$rev" -v "$PWD":/repo:ro -v "$PWD/artifacts":/out -v homebuild-lambda-nuget:/root/.nuget/packages homebuild-lambda-aot bash -c '
  set -euo pipefail
  tar -C /repo --exclude="*/bin" --exclude="*/obj" --exclude=node_modules --exclude=artifacts --exclude=.git -cf - \
      Directory.Build.props Directory.Packages.props global.json .editorconfig src/HomeBuild.Domain src/HomeBuild.Generators src/HomeBuild.Notifications.Lambda 2>/dev/null | tar -xf -
  common="-c Release -r linux-arm64 -p:RestoreLockedMode=false /t:PublishContainer -p:HomeBuildRevision=$REV -p:CopyOutputSymbolsToPublishDirectory=false"
  dotnet publish src/HomeBuild.Notifications.Lambda $common \
    -p:ContainerBaseImage=mcr.microsoft.com/dotnet/runtime-deps:11.0-resolute-chiseled \
    -p:ContainerRepository=homebuild-notifications-aot -p:ContainerArchiveOutputPath=/out/notifications-aot-image.tar.gz
  dotnet publish src/HomeBuild.Notifications.Lambda $common --self-contained false -p:PublishAot=false \
    -p:ContainerBaseImage=mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled \
    -p:ContainerRepository=homebuild-notifications-jit -p:ContainerArchiveOutputPath=/out/notifications-jit-image.tar.gz
'
docker load -i artifacts/notifications-aot-image.tar.gz
docker load -i artifacts/notifications-jit-image.tar.gz
docker image ls --format '{{.Repository}}:{{.Tag}} {{.Size}}' | grep homebuild-notifications-
