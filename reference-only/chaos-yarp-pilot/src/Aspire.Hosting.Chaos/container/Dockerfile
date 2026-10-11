# syntax=docker/dockerfile:1
# Chaos proxy container - YARP-as-library + chaos middleware compiled in.
# Built locally by Aspire's WithDockerfile during in-house incubation (D2). Each proxy edge builds
# its own image (distinct image name), but every edge's restore/publish/ReadyToRun/cert-bake layers
# are byte-identical, so the container layer cache serves builds 2..N cheaply — only the first edge
# of a multi-edge mesh pays the cold build. Switches to a published image at M4.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
# RID for the framework-dependent ReadyToRun publish. Defaults to linux-x64 (the dev runtime on
# amd64 podman/docker); override with --build-arg TARGETRID=linux-arm64 on arm64 hosts.
ARG TARGETRID=linux-x64
# The built-in package source is the corp-approved nuget.org mirror, and it is the source used on
# the first and only restore attempt. Package-source policy requires restoring from an approved
# source from the first attempt, so there is deliberately no public-first attempt and no fallback
# retry: reaching an unapproved feed first is not made compliant by retrying an approved one after
# it fails. AppHost operators who must restore from a different approved source can declare
# CHAOS_PROXY_NUGET_SOURCE before graph construction; Aspire forwards it as NUGET_SOURCE and it
# then replaces this default and is used alone. A declared source is an operator's assertion that
# the feed is approved for their environment — it is not a license to point the build at an
# unapproved feed.
ARG NUGET_SOURCE=https://packagefeedproxy.microsoft.io/nuget/v3/index.json
WORKDIR /src

# Copy csproj first for restore caching. Restore for the target RID AND with ReadyToRun on so
# the crossgen2 + runtime packs are restored — the --no-restore R2R publish below needs them.
#
# The restore is loud and fail-closed: it names the source it used, runs exactly one
# `dotnet restore --source`, and under `set -e` with no success-shaping suffix a failed restore
# fails this layer. There is no success-shaped degraded path and no second source to fall back to.
COPY ChaosProxy.Container.csproj ./
RUN set -e; \
    echo "chaos-proxy restore: using $NUGET_SOURCE"; \
    dotnet restore -r "$TARGETRID" -p:PublishReadyToRun=true --source "$NUGET_SOURCE"

# Copy source and publish. ReadyToRun (AOT-precompiled IL) makes each container's cold .NET
# start faster — it matters because the mesh runs many proxy containers built from this Dockerfile.
# Framework-dependent (--self-contained false) keeps the runtime image small.
COPY . ./
RUN dotnet publish ChaosProxy.Container.csproj -c Release -o /app/publish --no-restore \
    -r $TARGETRID --self-contained false -p:PublishReadyToRun=true

# Bake the self-signed dev HTTPS cert into the image at build time so each container START loads
# it instead of doing an RSA-2048 keygen per start (a mesh bring-up used to = one keygen per
# proxy). The app generates it itself (--generate-dev-cert), so it's guaranteed loadable by the
# same runtime. Dev-only: the 8443 listener is accept-any-cert; never use this cert in prod.
RUN dotnet /app/publish/ChaosProxy.Container.dll --generate-dev-cert /app/publish/devcert.pfx

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Ports + TLS are configured in Program.cs via ConfigureKestrel (HTTP 8080 + HTTPS 8443, with
# the baked /app/devcert.pfx loaded onto the https listener), so ASPNETCORE_URLS is intentionally
# NOT set — setting it would override the Kestrel Listen calls and drop the HTTPS listener.
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
EXPOSE 8443

ENTRYPOINT ["dotnet", "ChaosProxy.Container.dll"]
