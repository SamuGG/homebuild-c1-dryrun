# C9 Lab 7: the Native AOT build environment for the notifications Lambda. Native AOT compiles for the machine it runs
# on, and the binary links against that machine's glibc, so it is built on Amazon Linux 2023 itself (the same OS as
# Lambda's provided.al2023 runtime) on arm64. Use:
#   docker build --platform linux/arm64 -t homebuild-lambda-aot -f build/lambda-aot.Dockerfile build
#   scripts/build-notifications-lambda.sh
FROM public.ecr.aws/amazonlinux/amazonlinux:2023

# clang and zlib for the native link; tar and gzip for the SDK; findutils and zip for the build script.
RUN dnf install -y clang lld zlib-devel tar gzip findutils zip libicu && dnf clean all

# The .NET 11 SDK from Microsoft's release tarball, checked against the SHA-512 in its release metadata.
ARG SDK_VERSION=11.0.100
ARG SDK_SHA512=cfa248bb1377606e868602fb5b4d66fa16b7546156593937d1982f7f2142e6ded76d71c4a26efdab99f87c46ab40c3c672ab1b57ef7fa25449efe5b140a55af4
RUN curl -fsSL -o /tmp/sdk.tar.gz https://builds.dotnet.microsoft.com/dotnet/Sdk/${SDK_VERSION}/dotnet-sdk-${SDK_VERSION}-linux-arm64.tar.gz \
    && echo "${SDK_SHA512}  /tmp/sdk.tar.gz" | sha512sum -c - \
    && mkdir -p /usr/share/dotnet && tar -xzf /tmp/sdk.tar.gz -C /usr/share/dotnet && rm /tmp/sdk.tar.gz \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /work
