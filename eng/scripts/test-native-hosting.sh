#!/usr/bin/env bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"
rid="${1:-linux-x64}"
case "$rid" in
    linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
    *) echo "Unsupported native hosting RID: $rid" >&2; exit 1 ;;
esac
if [[ "$(node -p 'Number(process.versions.node.split(".")[0])')" -lt 24 ]]; then
    echo "Native hosting validation requires Node.js 24 or later." >&2
    exit 1
fi
docker info >/dev/null
docker compose version >/dev/null
if [[ "$rid" == osx-* && "$(xcode-select -p)" == */CommandLineTools ]]; then
    # NativeAOT otherwise probes xcodebuild to choose a linker. Command Line Tools
    # provide clang and the SDK but not xcodebuild; select their modern linker explicitly.
    export UseLdClassicXCodeLinker=false
fi

# Build outputs and evidence are confined to this lane's artifacts. Never borrow an existing
# published server, SDK or AppHost fixture: stale local artifacts can hide regressions.
work="$repo_root/artifacts/native-hosting/ci"
rm -rf "$work"
mkdir -p "$work/logs" "$work/runtime-sdk" "$work/cli-apphost/.aspire/modules"
# Keep transient IPC in the system temp directory: repository paths can exceed
# the platform's Unix-domain socket limit.
export PLAYWRIGHT_BROWSERS_PATH="$work/browser"
export MSBUILDTERMINALLOGGER=false
export DOTNET_NOLOGO=1
export DOTNET_ROOT="$repo_root/.dotnet"
export PATH="$repo_root/.dotnet:$PATH"

run_logged() {
    local name="$1"
    shift
    echo "=== $name ==="
    "$@" 2>&1 | tee "$work/logs/$name.log"
}

run_logged restore ./restore.sh

# The native core, server, build-time generator and AOT smoke host target net11.
# Keep the published executable on the same runtime as its regression suite.
run_logged cli-build dotnet build src/Aspire.Cli/Aspire.Cli.csproj -c Debug
run_logged managed-publish dotnet publish src/Aspire.Managed/Aspire.Managed.csproj \
    -c Debug -r "$rid" --self-contained true -o "$work/managed"
run_logged terminalhost-build dotnet build src/Aspire.TerminalHost/Aspire.TerminalHost.csproj -c Debug
run_logged dashboard-build dotnet build src/Aspire.Dashboard/Aspire.Dashboard.csproj -c Debug
run_logged dcp-build dotnet build "eng/dcppack/Aspire.Hosting.Orchestration.$rid.csproj" -c Debug
run_logged server-publish dotnet publish src/Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Server.csproj \
    -c Release -r "$rid" -o "$work/server"
run_logged smoke-publish dotnet publish tests/Aspire.Hosting.Native.Core.Tests/AotSmoke/Aspire.Hosting.Native.Core.AotSmoke.csproj \
    -c Release -r "$rid" -o "$work/smoke"
run_logged aot-smoke "$work/smoke/Aspire.Hosting.Native.Core.AotSmoke"
run_logged csharp-client-contract-tests env ASPIRE_NATIVE_CSHARP_TEST_SERVER="$work/server/Aspire.Hosting.Native.Server" \
    dotnet test --project tests/Aspire.Hosting.Native.Core.Tests/Aspire.Hosting.Native.Core.Tests.csproj --no-launch-profile -- \
    --filter-class '*.NativeCSharpClientTests' --filter-class '*.NativeSymbolicValueClientTests' \
    --filter-class '*.NativeProviderClientTests' \
    --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'

cli_dll="$(dotnet msbuild src/Aspire.Cli/Aspire.Cli.csproj -nologo -getProperty:TargetPath)"
cli="$(dirname "$cli_dll")/aspire"
export ASPIRE_NATIVE_DASHBOARD_DLL
ASPIRE_NATIVE_DASHBOARD_DLL="$(dotnet msbuild src/Aspire.Dashboard/Aspire.Dashboard.csproj -nologo -getProperty:TargetPath)"
dcp_intermediate="$(dotnet msbuild "eng/dcppack/Aspire.Hosting.Orchestration.$rid.csproj" -nologo -getProperty:IntermediateOutputPath)"
export ASPIRE_NATIVE_DCP_PATH="$dcp_intermediate/tools/dcp"
test -x "$cli"
test -x "$ASPIRE_NATIVE_DCP_PATH"
test -f "$ASPIRE_NATIVE_DASHBOARD_DLL"

ipc_environment=(
    "ASPIRE_NATIVE_SYMBOLIC_TEST_SERVER=$work/server/Aspire.Hosting.Native.Server"
    "ASPIRE_NATIVE_SYMBOLIC_TEST_DCP=$ASPIRE_NATIVE_DCP_PATH"
    "ASPIRE_NATIVE_SYMBOLIC_TEST_NODE=$(command -v node)"
)
if [[ "$rid" == linux-* ]]; then
    ipc_environment+=("ASPIRE_NATIVE_CROSS_USER_TEST_PYTHON=$(command -v python3)")
fi
run_logged ipc-security-tests env "${ipc_environment[@]}" \
    dotnet test --project tests/Aspire.Hosting.Native.Core.Tests/Aspire.Hosting.Native.Core.Tests.csproj --no-launch-profile -- \
    --filter-class '*.NativeIpcSecurityTests' --filter-class '*.NativeCliProtocolTests' \
    --filter-class '*.NativeAuxiliaryBackchannelTests' --filter-class '*.NativeSocketIsolationTests' \
    --filter-class '*.SocketConnectionListenerTests' \
    --filter-class '*.DcpEnvironmentTests' --filter-class '*.NativeServerOptionsTests' \
    --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'

# Provider-backed publishing uses the bundle's real offline codegen worker. Keep
# all layout components genuine even though publishing must not start DCP,
# Dashboard or terminal host.
layout="$work/compose-layout"
mkdir -p "$layout/terminalhost"
ln -s "$work/managed" "$layout/managed"
ln -s "$(dirname "$ASPIRE_NATIVE_DASHBOARD_DLL")" "$layout/dashboard"
ln -s "$(dirname "$ASPIRE_NATIVE_DCP_PATH")" "$layout/dcp"
ln -s "$work/server" "$layout/apphost-server"
terminal_dll="$(dotnet msbuild src/Aspire.TerminalHost/Aspire.TerminalHost.csproj -nologo -getProperty:TargetPath)"
terminal_dir="$(dirname "$terminal_dll")"
terminal_library=libhex1binterop.so
if [[ "$rid" == osx-* ]]; then terminal_library=libhex1binterop.dylib; fi
ln -s "$terminal_dir/Aspire.TerminalHost" "$layout/terminalhost/Aspire.TerminalHost"
ln -s "$terminal_dir/runtimes/$rid/native/$terminal_library" "$layout/terminalhost/$terminal_library"

run_logged product-cli-tests env ASPIRE_NATIVE_PRODUCT_CLI="$cli" \
    ASPIRE_NATIVE_CSHARP_TEST_SERVER="$work/server/Aspire.Hosting.Native.Server" \
    dotnet test --project tests/Aspire.Hosting.Native.Core.Tests/Aspire.Hosting.Native.Core.Tests.csproj --no-build --no-launch-profile -- \
    --filter-class '*.NativeNamedPipeTests' --filter-class '*.NativeProductCliTests' \
    --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'

sdk="$work/runtime-sdk"
fixtures="$repo_root/tests/Aspire.Hosting.Native.Core.Tests/AotSmoke"
generated="$repo_root/artifacts/obj/Aspire.Hosting.Native.Server/Release/native-ats"
cp "$generated/"*.mts "$sdk/"
cp "$generated/contract.json" "$sdk/"
cp "$fixtures/"*.mts "$sdk/"
cp playground/NativeIntegrations/TypeScript/DockerCompose/docker-compose*.mts "$sdk/"
# Install only the smoke dependencies before copying the template manifest, so its
# unrelated development tools are not installed. No repository manifest/lock is edited.
run_logged npm-install npm install --prefix "$sdk" --no-save --package-lock=false \
    typescript@5.9.3 @types/node@24 vscode-jsonrpc@8.2.1 pg@8.16.3 @types/pg@8.15.5 playwright@1.58.2
cp src/Aspire.Hosting.CodeGeneration.TypeScript/Resources/package.json "$sdk/package.json"
browser_args=(install chromium)
if [[ "$rid" == linux-* ]]; then
    browser_args=(install --with-deps chromium)
fi
run_logged browser-install "$sdk/node_modules/.bin/playwright" "${browser_args[@]}"
run_logged sdk-compile "$sdk/node_modules/.bin/tsc" --module NodeNext --moduleResolution NodeNext \
    --target ES2022 --strict --skipLibCheck --esModuleInterop --outDir "$sdk/out" "$sdk/"*.mts
run_logged typescript-compose-provider-tests node --test "$sdk/out/docker-compose.test.mjs"
run_logged typescript-pipeline-tests node --test "$sdk/out/native-pipeline.test.mjs"
run_logged csharp-compose-apphost-build dotnet build \
    playground/NativeIntegrations/CSharp/DockerComposeAppHost/NativeIntegrations.DockerCompose.AppHost.Sample.csproj -c Debug
csharp_compose_dll="$(dotnet msbuild playground/NativeIntegrations/CSharp/DockerComposeAppHost/NativeIntegrations.DockerCompose.AppHost.Sample.csproj -nologo -getProperty:TargetPath)"
run_logged csharp-compose-comparison-tests env \
    ASPIRE_NATIVE_CSHARP_TEST_SERVER="$work/server/Aspire.Hosting.Native.Server" \
    ASPIRE_NATIVE_COMPOSE_TYPESCRIPT="$sdk/out/NativeComposeComparison.mjs" \
    ASPIRE_NATIVE_COMPOSE_CSHARP_APPHOST="$(dirname "$csharp_compose_dll")/NativeIntegrations.DockerCompose.AppHost.Sample" \
    ASPIRE_NATIVE_COMPOSE_SAMPLE_DIRECTORY="$repo_root/playground/NativeIntegrations/TypeScript/DockerCompose/AppHost" \
    ASPIRE_NATIVE_SYMBOLIC_TEST_NODE="$(command -v node)" \
    dotnet test --project tests/Aspire.Hosting.Native.Core.Tests/Aspire.Hosting.Native.Core.Tests.csproj --no-launch-profile -- \
    --filter-class '*.NativeComposeCSharpTests' --filter-class '*.NativeComposeCSharpDockerTests' \
    --filter-class '*.NativeBuilderAuthoringTests' \
    --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'
run_logged compose-publish-deploy-smoke env ASPIRE_LAYOUT_PATH="$layout" \
    node "$sdk/out/NativeComposeSmoke.mjs" "$cli" "$work/server/Aspire.Hosting.Native.Server" "$repo_root" "$work/compose-apphosts"
run_logged rpc-authoring-projection-tests env ASPIRE_NATIVE_CSHARP_TEST_SERVER="$work/server/Aspire.Hosting.Native.Server" \
    ASPIRE_NATIVE_RPC_AUTHORING_TYPESCRIPT="$sdk/out/NativeRpcAuthoringSmoke.mjs" \
    ASPIRE_NATIVE_SYMBOLIC_TEST_NODE="$(command -v node)" \
    dotnet test --project tests/Aspire.Hosting.Native.Core.Tests/Aspire.Hosting.Native.Core.Tests.csproj --no-build --no-launch-profile -- \
    --filter-class '*.NativeRpcAuthoringTests' --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'
run_logged provider-shutdown-smoke node "$sdk/out/NativeProviderShutdownSmoke.mjs"
run_logged integration-host-smoke node "$sdk/out/IntegrationHostSmoke.mjs"
run_logged milestone-cycle-smoke env ASPIRE_NATIVE_DCP_PATH="$ASPIRE_NATIVE_DCP_PATH" \
    node "$sdk/out/NativeMilestoneSmoke.mjs" "$work/server/Aspire.Hosting.Native.Server"

# Real integration authoring examples are separate from transport/workload fixtures.
run_logged csharp-provider-build dotnet build playground/NativeIntegrations/CSharp/Provider/NativeIntegrations.Provider.csproj -c Debug
run_logged csharp-apphost-build dotnet build playground/NativeIntegrations/CSharp/AppHost/NativeIntegrations.AppHost.Sample.csproj -c Debug
integration_sdk="$work/integration-sdk"
mkdir -p "$integration_sdk"
cp "$generated/"*.mts "$integration_sdk/"
cp playground/NativeIntegrations/TypeScript/external-api.mts playground/NativeIntegrations/TypeScript/provider.mts "$integration_sdk/"
ln -s "$sdk/node_modules" "$integration_sdk/node_modules"
run_logged typescript-provider-compile "$sdk/node_modules/.bin/tsc" --module NodeNext --moduleResolution NodeNext \
    --target ES2022 --strict --skipLibCheck --esModuleInterop --outDir "$integration_sdk/out" "$integration_sdk/"*.mts
run_logged typescript-provider-metadata node "$integration_sdk/out/provider.mjs" --emit-contract "$integration_sdk/provider-contract.json"
provider_codegen="$(dotnet msbuild src/Aspire.Hosting.Native.CodeGeneration/Aspire.Hosting.Native.CodeGeneration.csproj -nologo -getProperty:TargetPath)"
run_logged typescript-provider-projection dotnet "$provider_codegen" --provider-ts "$integration_sdk/provider-contract.json" "$integration_sdk"
cp playground/NativeIntegrations/TypeScript/apphost.mts "$integration_sdk/"
run_logged typescript-apphost-compile "$sdk/node_modules/.bin/tsc" --module NodeNext --moduleResolution NodeNext \
    --target ES2022 --strict --skipLibCheck --esModuleInterop --outDir "$integration_sdk/out" "$integration_sdk/"*.mts
provider_dll="$(dotnet msbuild playground/NativeIntegrations/CSharp/Provider/NativeIntegrations.Provider.csproj -nologo -getProperty:TargetPath)"
provider_apphost_dll="$(dotnet msbuild playground/NativeIntegrations/CSharp/AppHost/NativeIntegrations.AppHost.Sample.csproj -nologo -getProperty:TargetPath)"
run_logged genuine-provider-apphosts env ASPIRE_NATIVE_CSHARP_TEST_SERVER="$work/server/Aspire.Hosting.Native.Server" \
    ASPIRE_NATIVE_PROVIDER_CSHARP="$(dirname "$provider_dll")/NativeIntegrations.Provider" \
    ASPIRE_NATIVE_PROVIDER_CSHARP_APPHOST="$(dirname "$provider_apphost_dll")/NativeIntegrations.AppHost.Sample" \
    ASPIRE_NATIVE_PROVIDER_TYPESCRIPT="$integration_sdk/out/provider.mjs" \
    ASPIRE_NATIVE_PROVIDER_TYPESCRIPT_APPHOST="$integration_sdk/out/apphost.mjs" \
    ASPIRE_NATIVE_SYMBOLIC_TEST_NODE="$(command -v node)" \
    dotnet test --project tests/Aspire.Hosting.Native.Core.Tests/Aspire.Hosting.Native.Core.Tests.csproj --no-build --no-launch-profile -- \
    --filter-class '*.NativeProviderAppHostTests' --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'

run_logged csharp-redis-provider-build dotnet build playground/NativeIntegrations/CSharp/RedisProvider/NativeIntegrations.Redis.Provider.csproj
run_logged csharp-redis-apphost-build dotnet build playground/NativeIntegrations/CSharp/RedisAppHost/NativeIntegrations.Redis.AppHost.Sample.csproj
run_logged csharp-redis-validation-build dotnet build playground/NativeIntegrations/CSharp/RedisValidation/NativeIntegrations.Redis.Validation.csproj
redis_sdk="$work/redis-integration-sdk"
mkdir -p "$redis_sdk"
cp "$generated/"*.mts "$redis_sdk/"
cp playground/NativeIntegrations/TypeScript/Redis/redis.mts playground/NativeIntegrations/TypeScript/Redis/redis-protocol.mts \
    playground/NativeIntegrations/TypeScript/Redis/redis-provider.mts \
    playground/NativeIntegrations/TypeScript/Redis/redis-authoring.mts "$redis_sdk/"
ln -s "$sdk/node_modules" "$redis_sdk/node_modules"
run_logged typescript-redis-provider-compile "$sdk/node_modules/.bin/tsc" --module NodeNext --moduleResolution NodeNext \
    --target ES2022 --strict --skipLibCheck --esModuleInterop --outDir "$redis_sdk/out" "$redis_sdk/"*.mts
run_logged typescript-redis-provider-metadata node "$redis_sdk/out/redis-provider.mjs" --emit-contract "$redis_sdk/provider-contract.json"
run_logged typescript-redis-provider-projection dotnet "$provider_codegen" --provider-ts "$redis_sdk/provider-contract.json" "$redis_sdk"
cp playground/NativeIntegrations/TypeScript/Redis/redis-apphost.mts \
    playground/NativeIntegrations/TypeScript/Redis/redis-builder.mts \
    playground/NativeIntegrations/TypeScript/Redis/redis-builder.test.mts \
    playground/NativeIntegrations/TypeScript/Redis/redis-validation.mts "$redis_sdk/"
run_logged typescript-redis-apphost-compile "$sdk/node_modules/.bin/tsc" --module NodeNext --moduleResolution NodeNext \
    --target ES2022 --strict --skipLibCheck --esModuleInterop --outDir "$redis_sdk/out" "$redis_sdk/"*.mts
run_logged typescript-builder-shape-tests node --test "$redis_sdk/out/redis-builder.test.mjs"
redis_provider="$(dotnet msbuild playground/NativeIntegrations/CSharp/RedisProvider/NativeIntegrations.Redis.Provider.csproj -nologo -getProperty:TargetPath)"
redis_apphost="$(dotnet msbuild playground/NativeIntegrations/CSharp/RedisAppHost/NativeIntegrations.Redis.AppHost.Sample.csproj -nologo -getProperty:TargetPath)"
redis_validation="$(dotnet msbuild playground/NativeIntegrations/CSharp/RedisValidation/NativeIntegrations.Redis.Validation.csproj -nologo -getProperty:TargetPath)"
run_logged genuine-redis-apphosts env ASPIRE_NATIVE_SYMBOLIC_TEST_SERVER="$work/server/Aspire.Hosting.Native.Server" \
    ASPIRE_NATIVE_SYMBOLIC_TEST_DCP="$ASPIRE_NATIVE_DCP_PATH" ASPIRE_NATIVE_SYMBOLIC_TEST_NODE="$(command -v node)" \
    ASPIRE_NATIVE_REDIS_CSHARP_PROVIDER="$(dirname "$redis_provider")/NativeIntegrations.Redis.Provider" \
    ASPIRE_NATIVE_REDIS_CSHARP_APPHOST="$(dirname "$redis_apphost")/NativeIntegrations.Redis.AppHost.Sample" \
    ASPIRE_NATIVE_REDIS_CSHARP_VALIDATION="$(dirname "$redis_validation")/NativeIntegrations.Redis.Validation" \
    ASPIRE_NATIVE_REDIS_TYPESCRIPT_PROVIDER="$redis_sdk/out/redis-provider.mjs" \
    ASPIRE_NATIVE_REDIS_TYPESCRIPT_APPHOST="$redis_sdk/out/redis-apphost.mjs" \
    ASPIRE_NATIVE_REDIS_TYPESCRIPT_VALIDATION="$redis_sdk/out/redis-validation.mjs" \
    dotnet test --project tests/Aspire.Hosting.Native.Core.Tests/Aspire.Hosting.Native.Core.Tests.csproj --no-build --no-launch-profile -- \
    --filter-class '*.NativeRedisAppHostTests' --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'

workspace="$work/cli-apphost"
cp "$fixtures/CliAppHost/"* "$workspace/"
cp tests/PolyglotAppHosts/Aspire.Hosting/TypeScript/package.json "$workspace/package.json"
cp src/Aspire.Cli/Templating/Templates/ts-starter/tsconfig.apphost.json "$workspace/"
cp "$generated/"*.mts "$workspace/.aspire/modules/"
# CLI's real npm install owns its node_modules. Resolve the integration worker from
# the SDK workspace so that install cannot prune its pg/playwright smoke dependencies.
ln -s "$sdk/out" "$workspace/out"
server="$work/server/Aspire.Hosting.Native.Server"
run_logged symbolic-value-contract-smoke env ASPIRE_NATIVE_SYMBOLIC_TEST_SERVER="$server" \
    ASPIRE_NATIVE_SYMBOLIC_TEST_DCP="$ASPIRE_NATIVE_DCP_PATH" ASPIRE_NATIVE_SYMBOLIC_TEST_NODE="$(command -v node)" \
    dotnet test --project tests/Aspire.Hosting.Native.Core.Tests/Aspire.Hosting.Native.Core.Tests.csproj --no-build --no-launch-profile -- \
    --filter-class '*.NativeSymbolicWorkloadSocketTests' --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'
run_logged typescript-symbolic-value-contract-smoke env -u ASPIRE_NATIVE_DCP_PATH \
    node "$sdk/out/NativeSymbolicValueSmoke.mjs" "$server"

# The RPC fixture exercises AotSmoke's line-delimited --rpc host, while the runtime
# fixture below exercises the production server's authenticated socket transport.
run_logged rpc-smoke env -u ASPIRE_NATIVE_DCP_PATH node "$sdk/out/NativeRpcSmoke.mjs" \
    "$work/smoke/Aspire.Hosting.Native.Core.AotSmoke"
run_logged runtime-smoke env -u ASPIRE_NATIVE_DCP_PATH node "$sdk/out/NativeRuntimeSmoke.mjs" "$server"
run_logged baseline-smoke node "$sdk/out/NativeBaselineSmoke.mjs" "$server"
run_logged workload-dashboard-smoke node "$sdk/out/NativeWorkloadSmoke.mjs" "$server"
run_logged revision-dashboard-smoke node "$sdk/out/NativeRevisionSmoke.mjs" "$server"
run_logged cli-dashboard-smoke node "$sdk/out/NativeCliSmoke.mjs" "$cli" "$server" "$workspace"
# The auxiliary fixture responds to confirmations itself; keep the real Dashboard
# running via the CLI but do not ask a browser to handle this scenario's confirmation.
run_logged auxiliary-smoke env -u ASPIRE_NATIVE_DASHBOARD_DLL \
    ASPIRE_DASHBOARD_PATH="$ASPIRE_NATIVE_DASHBOARD_DLL" \
    node "$sdk/out/NativeAuxiliarySmoke.mjs" "$cli" "$server" "$workspace"
echo "Native hosting validation passed, including the suites' owned-resource cleanup assertions."
