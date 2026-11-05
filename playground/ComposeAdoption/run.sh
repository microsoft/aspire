#!/usr/bin/env bash
set -euo pipefail

sample="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$sample/../.." && pwd)"
cd "$repo"

dotnet="$repo/.dotnet/dotnet"
if [[ ! -x "$dotnet" ]]; then
    dotnet="$(command -v dotnet)"
fi
export DOTNET_ROOT="$(dirname "$dotnet")"
export PATH="$DOTNET_ROOT:$PATH"

mode="${1:-}"
if [[ $# -gt 1 || ( -n "$mode" && "$mode" != "--build-only" && "$mode" != "--no-build" ) ]]; then
    echo "Usage: bash run.sh [--build-only|--no-build]" >&2
    exit 2
fi
if [[ -n "${ASPIRE_DCP_PATH:-}" && ! -x "$ASPIRE_DCP_PATH/dcp" ]]; then
    echo "ASPIRE_DCP_PATH must identify a tools directory containing an executable dcp: $ASPIRE_DCP_PATH" >&2
    exit 2
fi
arguments=()
if [[ -n "${ASPIRE_DCP_PATH:-}" ]]; then
    arguments+=(--DcpPublisher:CliPath "$ASPIRE_DCP_PATH/dcp")
fi
if [[ "$mode" != "--no-build" ]]; then
    "$dotnet" publish "$sample/Api/ComposeAdoption.Api.csproj" -p:NuGetAudit=false \
        -c Debug -o "$repo/artifacts/compose-adoption/api"
    "$dotnet" publish "$sample/Gateway/ComposeAdoption.Gateway.csproj" -p:NuGetAudit=false \
        -c Debug -o "$repo/artifacts/compose-adoption/gateway"
    "$dotnet" build "$sample/AppHost/ComposeAdoption.AppHost.csproj" -p:NuGetAudit=false
fi
if [[ "$mode" == "--build-only" ]]; then
    exit 0
fi

# This local-only demonstration deliberately uses HTTP. Never expose these
# anonymous dashboard/resource-service endpoints on a public interface.
export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true
export DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true
export ASPNETCORE_URLS=http://localhost:18888
export ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL=http://localhost:18889
export ASPIRE_DASHBOARD_PATH="$repo/artifacts/bin/Aspire.Dashboard/Debug/net11.0/Aspire.Dashboard.dll"

# Invoking the built assembly avoids depending on an installed Aspire CLI bundle.
exec "$dotnet" "$repo/artifacts/bin/ComposeAdoption.AppHost/Debug/net10.0/ComposeAdoption.AppHost.dll" "${arguments[@]}"
