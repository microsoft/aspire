#!/usr/bin/env bash

set -euo pipefail

show_help() {
    cat <<'EOF'
Usage: verify-startup-otel.sh [OPTIONS]

Runs the startup OTEL verification harness without PowerShell.

Options:
  --target-aspire-path PATH       Aspire CLI under test. Alias: --aspire-path
  --profiler-aspire-path PATH     Aspire CLI used to host/export from the dashboard.
                                  Alias: --dashboard-aspire-path
  --layout-path PATH              Aspire bundle layout path.
  --dcp-path PATH                 DCP directory or binary path override.
  --output-root PATH              Output root for harness artifacts.
  --post-start-delay SECONDS      Delay after AppHost start before stopping.
  --require-dcp-spans             Require exported DCP process/resource spans.
  --collect-dotnet-traces         Collect dotnet-trace .nettrace files for the CLI and child .NET processes.
                                  Also enables MSBuild .binlog collection.
  --collect-dotnet-binlogs        Collect MSBuild .binlog files for dotnet MSBuild commands.
  --skip-build                    Do not restore/build the local Aspire CLI or bundle layout.
  -h, --help                      Show this help.
EOF
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"

target_aspire_path=""
profiler_aspire_path=""
layout_path=""
layout_path_explicit=false
dcp_path=""
output_root=""
post_start_delay_seconds=0
require_dcp_spans=false
collect_dotnet_traces=false
collect_dotnet_binlogs=false
skip_build=false
dashboard_pid=""
dotnet_trace_dir=""
dotnet_trace_path=""
dotnet_binlog_dir=""
active_dotnet_trace_pids=""
active_dotnet_trace_target_pids=""

write_step() {
    echo "==> $1"
}

fail() {
    echo "error: $1" >&2
    exit 1
}

require_value() {
    local option="$1"
    local value="${2:-}"
    if [[ -z "$value" ]]; then
        fail "Option '$option' requires a non-empty value."
    fi
}

require_command() {
    local command_name="$1"
    if ! command -v "$command_name" >/dev/null 2>&1; then
        fail "Required command '$command_name' was not found on PATH."
    fi
}

resolve_dotnet_trace() {
    if command -v dotnet-trace >/dev/null 2>&1; then
        command -v dotnet-trace
    elif [[ -x "$HOME/.dotnet/tools/dotnet-trace" ]]; then
        printf '%s\n' "$HOME/.dotnet/tools/dotnet-trace"
    else
        fail "Required command 'dotnet-trace' was not found. Install it with: dotnet tool install -g dotnet-trace"
    fi
}

resolve_existing_path() {
    local path="$1"
    if [[ -d "$path" ]]; then
        (cd "$path" && pwd -P)
    else
        local directory
        local file_name
        directory="$(dirname "$path")"
        file_name="$(basename "$path")"
        (cd "$directory" && printf '%s/%s\n' "$(pwd -P)" "$file_name")
    fi
}

get_free_tcp_port() {
    node -e "const net = require('node:net'); const server = net.createServer(); server.listen(0, '127.0.0.1', () => { console.log(server.address().port); server.close(); });"
}

get_current_rid() {
    local os_name
    local architecture

    case "$(uname -s)" in
        Darwin)
            os_name="osx"
            ;;
        Linux)
            os_name="linux"
            ;;
        *)
            return 1
            ;;
    esac

    case "$(uname -m)" in
        arm64|aarch64)
            architecture="arm64"
            ;;
        x86_64|amd64)
            architecture="x64"
            ;;
        *)
            return 1
            ;;
    esac

    printf '%s-%s\n' "$os_name" "$architecture"
}

invoke_logged_command() {
    local name="$1"
    local working_directory="$2"
    shift 2

    local stdout_path="$logs_dir/$name.stdout.txt"
    local stderr_path="$logs_dir/$name.stderr.txt"
    local exit_code

    set +e
    (
        cd "$working_directory" && "$@"
    ) >"$stdout_path" 2>"$stderr_path"
    exit_code=$?
    set -e

    if [[ $exit_code -ne 0 ]]; then
        {
            echo "Command failed ($exit_code): $*"
            echo "stdout: $stdout_path"
            cat "$stdout_path"
            echo "stderr: $stderr_path"
            cat "$stderr_path"
        } >&2
        exit "$exit_code"
    fi

    printf '%s\n' "$stdout_path"
}

sanitize_file_name() {
    printf '%s' "$1" | tr -cs '[:alnum:]_.-' '-' | sed -e 's/^-//' -e 's/-$//' | cut -c 1-80
}

contains_value() {
    local value="$1"
    shift

    local item
    for item in "$@"; do
        if [[ "$item" == "$value" ]]; then
            return 0
        fi
    done

    return 1
}

stop_active_dotnet_traces() {
    if [[ -z "$active_dotnet_trace_pids" ]]; then
        return
    fi

    local trace_pid
    for trace_pid in $active_dotnet_trace_pids; do
        if [[ -n "$trace_pid" ]] && kill -0 "$trace_pid" 2>/dev/null; then
            kill -TERM "$trace_pid" 2>/dev/null || true
        fi
    done

    for trace_pid in $active_dotnet_trace_pids; do
        if [[ -n "$trace_pid" ]]; then
            wait "$trace_pid" 2>/dev/null || true
        fi
    done

    active_dotnet_trace_pids=""
    active_dotnet_trace_target_pids=""
}

start_dotnet_trace_for_pid() {
    local target_process_id="$1"
    local name="$2"

    if ! kill -0 "$target_process_id" 2>/dev/null; then
        return
    fi

    if contains_value "$target_process_id" $active_dotnet_trace_target_pids; then
        return
    fi

    mkdir -p "$dotnet_trace_dir"

    local sanitized_name
    sanitized_name="$(sanitize_file_name "$name")"
    if [[ -z "$sanitized_name" ]]; then
        sanitized_name="process"
    fi

    local trace_path="$dotnet_trace_dir/$sanitized_name-$target_process_id.nettrace"
    local stdout_path="$logs_dir/dotnet-trace-$sanitized_name-$target_process_id.stdout.txt"
    local stderr_path="$logs_dir/dotnet-trace-$sanitized_name-$target_process_id.stderr.txt"

    "$dotnet_trace_path" collect \
        --process-id "$target_process_id" \
        --profile dotnet-sampled-thread-time \
        --format nettrace \
        --output "$trace_path" \
        >"$stdout_path" \
        2>"$stderr_path" &

    active_dotnet_trace_pids="${active_dotnet_trace_pids:+$active_dotnet_trace_pids }$!"
    active_dotnet_trace_target_pids="${active_dotnet_trace_target_pids:+$active_dotnet_trace_target_pids }$target_process_id"
}

is_traceable_dotnet_process() {
    local process_id="$1"
    local command_name
    local command_line

    command_name="$(ps -p "$process_id" -o comm= 2>/dev/null || true)"
    command_line="$(ps -p "$process_id" -o command= 2>/dev/null || true)"

    case "$(basename "$command_name")" in
        dotnet|dotnet.exe|StartupOtelHarness|StartupOtelHarness.exe)
            return 0
            ;;
    esac

    [[ "$command_line" == *"dotnet"* || "$command_line" == *"StartupOtelHarness"* ]]
}

trace_descendant_dotnet_processes() {
    local root_process_id="$1"
    local process_id

    while IFS= read -r process_id; do
        if [[ -z "$process_id" ]]; then
            continue
        fi

        if is_traceable_dotnet_process "$process_id"; then
            local command_name
            command_name="$(ps -p "$process_id" -o comm= 2>/dev/null || true)"
            start_dotnet_trace_for_pid "$process_id" "child-$(basename "$command_name")"
        fi
    done < <(get_child_process_ids "$root_process_id")
}

invoke_start_with_dotnet_traces() {
    local stdout_path="$logs_dir/start.stdout.txt"
    local stderr_path="$logs_dir/start.stderr.txt"
    local exit_code

    dotnet_trace_dir="$run_root/dotnet-traces"
    mkdir -p "$dotnet_trace_dir"

    write_step "Collecting dotnet-trace files in $dotnet_trace_dir" >&2

    set +e
    (
        cd "$project_dir" && env "${startup_env[@]}" "$target_aspire_path" start --format Json --apphost "$apphost_path"
    ) >"$stdout_path" 2>"$stderr_path" &
    local start_process_id=$!
    set -e

    start_dotnet_trace_for_pid "$start_process_id" "aspire-start-cli"

    while kill -0 "$start_process_id" 2>/dev/null; do
        trace_descendant_dotnet_processes "$start_process_id"
        sleep 0.05
    done

    set +e
    wait "$start_process_id"
    exit_code=$?
    set -e

    if [[ $exit_code -ne 0 ]]; then
        stop_active_dotnet_traces
        {
            echo "Command failed ($exit_code): $target_aspire_path start --format Json --apphost $apphost_path"
            echo "stdout: $stdout_path"
            cat "$stdout_path"
            echo "stderr: $stderr_path"
            cat "$stderr_path"
        } >&2
        exit "$exit_code"
    fi

    local apphost_process_id
    apphost_process_id="$(node -e "const fs = require('node:fs'); const p = process.argv[1]; const value = JSON.parse(fs.readFileSync(p, 'utf8')).appHostPid; if (value) console.log(value);" "$stdout_path" 2>/dev/null || true)"
    if [[ -n "$apphost_process_id" ]] && kill -0 "$apphost_process_id" 2>/dev/null; then
        start_dotnet_trace_for_pid "$apphost_process_id" "apphost"
    fi

    start_stdout="$stdout_path"
}

wait_http_ready() {
    local url="$1"
    local timeout_seconds="${2:-60}"
    local deadline=$((SECONDS + timeout_seconds))

    while (( SECONDS < deadline )); do
        local status
        status="$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$url" 2>/dev/null || true)"
        if [[ "$status" =~ ^[0-9]+$ ]] && (( status >= 200 && status < 500 )); then
            return
        fi

        sleep 0.5
    done

    fail "Timed out waiting for $url."
}

get_child_process_ids() {
    local parent_process_id="$1"
    local child_process_id

    while IFS= read -r child_process_id; do
        if [[ -n "$child_process_id" ]]; then
            get_child_process_ids "$child_process_id"
            printf '%s\n' "$child_process_id"
        fi
    done < <(pgrep -P "$parent_process_id" 2>/dev/null || true)
}

stop_process_tree() {
    local process_id="$1"
    local process_ids=()
    local child_process_id

    while IFS= read -r child_process_id; do
        if [[ -n "$child_process_id" ]]; then
            process_ids+=("$child_process_id")
        fi
    done < <(get_child_process_ids "$process_id")

    process_ids+=("$process_id")

    for process_id in "${process_ids[@]}"; do
        if kill -0 "$process_id" 2>/dev/null; then
            kill "$process_id" 2>/dev/null || true
        fi
    done

    sleep 2

    for process_id in "${process_ids[@]}"; do
        if kill -0 "$process_id" 2>/dev/null; then
            kill -9 "$process_id" 2>/dev/null || true
        fi
    done
}

cleanup() {
    local exit_code=$?

    if [[ "$collect_dotnet_traces" == true ]]; then
        stop_active_dotnet_traces
    fi

    if [[ -n "${dashboard_pid:-}" ]] && kill -0 "$dashboard_pid" 2>/dev/null; then
        write_step "Stopping standalone dashboard"
        stop_process_tree "$dashboard_pid"
    fi

    exit "$exit_code"
}

trap cleanup EXIT

while [[ $# -gt 0 ]]; do
    case "$1" in
        --target-aspire-path|--aspire-path)
            require_value "$1" "${2:-}"
            target_aspire_path="$2"
            shift 2
            ;;
        --profiler-aspire-path|--dashboard-aspire-path)
            require_value "$1" "${2:-}"
            profiler_aspire_path="$2"
            shift 2
            ;;
        --layout-path)
            require_value "$1" "${2:-}"
            layout_path="$2"
            layout_path_explicit=true
            shift 2
            ;;
        --dcp-path)
            require_value "$1" "${2:-}"
            dcp_path="$2"
            shift 2
            ;;
        --output-root)
            require_value "$1" "${2:-}"
            output_root="$2"
            shift 2
            ;;
        --post-start-delay)
            require_value "$1" "${2:-}"
            post_start_delay_seconds="$2"
            shift 2
            ;;
        --require-dcp-spans)
            require_dcp_spans=true
            shift
            ;;
        --collect-dotnet-traces)
            collect_dotnet_traces=true
            shift
            ;;
        --collect-dotnet-binlogs)
            collect_dotnet_binlogs=true
            shift
            ;;
        --skip-build)
            skip_build=true
            shift
            ;;
        -h|--help)
            show_help
            exit 0
            ;;
        *)
            fail "Unknown option '$1'. Use --help for usage information."
            ;;
    esac
done

require_command curl
require_command dotnet
require_command node
require_command pgrep
require_command ps
require_command unzip

if [[ "$collect_dotnet_traces" == true ]]; then
    collect_dotnet_binlogs=true
    dotnet_trace_path="$(resolve_dotnet_trace)"
fi

if [[ ! "$post_start_delay_seconds" =~ ^[0-9]+$ ]]; then
    fail "--post-start-delay must be a non-negative integer."
fi

current_rid="$(get_current_rid || true)"

if [[ -z "$output_root" ]]; then
    output_root="$repo_root/artifacts/tmp/startup-otel-harness"
fi

run_id="$(date +%Y%m%d-%H%M%S)"
run_root="$output_root/$run_id"
workspace="$run_root/workspace"
project_dir="$workspace/StartupOtelHarness"
logs_dir="$run_root/logs"
export_dir="$run_root/export"
export_zip="$run_root/startup-otel-export.zip"
span_summary_path="$run_root/span-summary.json"

mkdir -p "$workspace" "$logs_dir" "$export_dir"

if [[ "$collect_dotnet_binlogs" == true ]]; then
    dotnet_binlog_dir="$run_root/binlogs"
    mkdir -p "$dotnet_binlog_dir"
fi

if [[ -z "$target_aspire_path" ]]; then
    target_aspire_path="$repo_root/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire"
fi

if [[ -z "$profiler_aspire_path" ]]; then
    profiler_aspire_path="$target_aspire_path"
fi

if [[ -z "$layout_path" && -n "$current_rid" && -d "$repo_root/artifacts/bundle/$current_rid" ]]; then
    layout_path="$repo_root/artifacts/bundle/$current_rid"
fi

if [[ -z "$layout_path" && -n "$profiler_aspire_path" ]]; then
    profiler_aspire_directory="$(dirname "$profiler_aspire_path")"
    candidate_layout_path="$(cd "$profiler_aspire_directory/.." 2>/dev/null && pwd -P || true)"
    if [[ -n "$candidate_layout_path" && ( -d "$candidate_layout_path/bundle" || ( -d "$candidate_layout_path/managed" && -d "$candidate_layout_path/dcp" ) ) ]]; then
        layout_path="$candidate_layout_path"
    fi
fi

if [[ "$skip_build" == false ]]; then
    if [[ -z "$current_rid" ]]; then
        fail "Could not determine the current runtime identifier for bundle layout build."
    fi

    write_step "Building local Aspire CLI"
    invoke_logged_command "restore" "$repo_root" "$repo_root/restore.sh" >/dev/null
    invoke_logged_command "build-bundle-layout" "$repo_root" dotnet msbuild "$repo_root/eng/Bundle.proj" /t:Build /p:TargetRid="$current_rid" /p:SkipNativeBuild=true >/dev/null
    invoke_logged_command "build-aspire-cli" "$repo_root" dotnet build "$repo_root/src/Aspire.Cli/Aspire.Cli.csproj" --no-restore >/dev/null

    if [[ "$layout_path_explicit" == false && -d "$repo_root/artifacts/bundle/$current_rid" ]]; then
        layout_path="$repo_root/artifacts/bundle/$current_rid"
    fi
fi

if [[ ! -f "$target_aspire_path" ]]; then
    fail "Target Aspire CLI not found at $target_aspire_path."
fi

if [[ ! -f "$profiler_aspire_path" ]]; then
    fail "Profiler Aspire CLI not found at $profiler_aspire_path."
fi

target_aspire_path="$(resolve_existing_path "$target_aspire_path")"
profiler_aspire_path="$(resolve_existing_path "$profiler_aspire_path")"

if [[ -n "$layout_path" ]]; then
    layout_path="$(resolve_existing_path "$layout_path")"
    write_step "Using Aspire bundle layout at $layout_path"
fi

if [[ -n "$dcp_path" ]]; then
    dcp_path="$(resolve_existing_path "$dcp_path")"
    if [[ -f "$dcp_path" ]]; then
        dcp_path="$(dirname "$dcp_path")"
    fi

    dcp_binary_path="$dcp_path/dcp"
    if [[ ! -f "$dcp_binary_path" && -f "$dcp_binary_path.exe" ]]; then
        dcp_binary_path="$dcp_binary_path.exe"
    fi
    if [[ ! -f "$dcp_binary_path" ]]; then
        fail "DCP executable not found under $dcp_path."
    fi
fi

dashboard_port="$(get_free_tcp_port)"
otlp_grpc_port="$(get_free_tcp_port)"
otlp_http_port="$(get_free_tcp_port)"
dashboard_url="http://localhost:$dashboard_port"
otlp_grpc_url="http://localhost:$otlp_grpc_port"
otlp_http_url="http://localhost:$otlp_http_port"

dashboard_stdout="$logs_dir/dashboard.stdout.txt"
dashboard_stderr="$logs_dir/dashboard.stderr.txt"
dashboard_env=()
layout_dcp_path=""
layout_managed_path=""
if [[ -n "$layout_path" ]]; then
    dashboard_env+=("ASPIRE_LAYOUT_PATH=$layout_path")

    if [[ -d "$layout_path/dcp" && -d "$layout_path/managed" ]]; then
        layout_dcp_path="$layout_path/dcp"
        layout_managed_path="$layout_path/managed/aspire-managed"
    elif [[ -d "$layout_path/bundle/dcp" && -d "$layout_path/bundle/managed" ]]; then
        layout_dcp_path="$layout_path/bundle/dcp"
        layout_managed_path="$layout_path/bundle/managed/aspire-managed"
    fi

    if [[ ! -f "$layout_managed_path" && -f "$layout_managed_path.exe" ]]; then
        layout_managed_path="$layout_managed_path.exe"
    fi
fi

write_step "Starting standalone dashboard at $dashboard_url"
pushd "$run_root" >/dev/null
env "${dashboard_env[@]}" "$profiler_aspire_path" \
    dashboard run \
    --frontend-url "$dashboard_url" \
    --otlp-grpc-url "$otlp_grpc_url" \
    --otlp-http-url "$otlp_http_url" \
    --allow-anonymous \
    >"$dashboard_stdout" \
    2>"$dashboard_stderr" &
dashboard_pid=$!
popd >/dev/null

wait_http_ready "$dashboard_url" 90

write_step "Configuring CLI diagnostic OTLP export to $otlp_grpc_url"
# Keep both forms: OTEL_* configures CLI/OpenTelemetry exporters, while ASPIRE_OTEL_* is
# projected into AppHost IConfiguration as OTEL_* by DistributedApplicationBuilder.
startup_env=(
    "${dashboard_env[@]}"
    "ASPIRE_CLI_TELEMETRY_OPTOUT=true"
    "ASPIRE_STARTUP_PROFILING_ENABLED=true"
    "OTEL_EXPORTER_OTLP_ENDPOINT=$otlp_grpc_url"
    "OTEL_EXPORTER_OTLP_PROTOCOL=grpc"
    "ASPIRE_OTEL_EXPORTER_OTLP_ENDPOINT=$otlp_grpc_url"
    "ASPIRE_OTEL_EXPORTER_OTLP_PROTOCOL=grpc"
)
if [[ -n "$dcp_path" ]]; then
    startup_env+=("ASPIRE_DCP_PATH=$dcp_path")
elif [[ -n "$layout_dcp_path" ]]; then
    startup_env+=("ASPIRE_DCP_PATH=$layout_dcp_path")
fi
if [[ -n "$layout_managed_path" && -f "$layout_managed_path" ]]; then
    startup_env+=("ASPIRE_DASHBOARD_PATH=$layout_managed_path")
fi
if [[ "$collect_dotnet_binlogs" == true ]]; then
    write_step "Collecting dotnet MSBuild binlogs in $dotnet_binlog_dir"
    startup_env+=("ASPIRE_CLI_DOTNET_BINLOG_DIR=$dotnet_binlog_dir")
fi

write_step "Creating C# AppHost fixture"
service_dir="$project_dir/service"
properties_dir="$project_dir/Properties"
mkdir -p "$project_dir" "$service_dir" "$properties_dir"
apphost_dashboard_port="$(get_free_tcp_port)"
apphost_otlp_grpc_port="$(get_free_tcp_port)"
apphost_resource_service_port="$(get_free_tcp_port)"

cat >"$properties_dir/launchSettings.json" <<EOF
{
  "profiles": {
    "https": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:$apphost_dashboard_port",
      "environmentVariables": {
        "ASPIRE_ALLOW_UNSECURED_TRANSPORT": "true",
        "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "http://localhost:$apphost_otlp_grpc_port",
        "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "http://localhost:$apphost_resource_service_port",
        "ASPIRE_STARTUP_PROFILING_ENABLED": "true",
        "OTEL_EXPORTER_OTLP_ENDPOINT": "$otlp_grpc_url",
        "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
        "ASPIRE_OTEL_EXPORTER_OTLP_ENDPOINT": "$otlp_grpc_url",
        "ASPIRE_OTEL_EXPORTER_OTLP_PROTOCOL": "grpc"
      }
    }
  }
}
EOF

cat >"$project_dir/StartupOtelHarness.AppHost.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsAspireHost>true</IsAspireHost>
    <AspireHostingSDKVersion>13.4.0</AspireHostingSDKVersion>
    <UserSecretsId>startup-otel-harness</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repo_root/src/Aspire.Hosting.AppHost/Aspire.Hosting.AppHost.csproj" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>AppHostProjectPath</_Parameter1>
      <_Parameter2>\$(MSBuildProjectDirectory)</_Parameter2>
    </AssemblyAttribute>
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>AppHostProjectName</_Parameter1>
      <_Parameter2>\$(MSBuildProjectFile)</_Parameter2>
    </AssemblyAttribute>
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>AppHostProjectBaseIntermediateOutputPath</_Parameter1>
      <_Parameter2>\$(BaseIntermediateOutputPath)</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
EOF

cat >"$service_dir/server.js" <<'EOF'
import http from 'node:http';

const port = Number(process.env.PORT ?? '0');
const server = http.createServer((request, response) => {
    response.writeHead(200, { 'content-type': 'text/plain' });
    response.end('startup otel harness');
});

server.listen(port, '127.0.0.1', () => {
    console.log(`startup otel harness listening on ${port}`);
});

process.on('SIGTERM', () => {
    server.close(() => process.exit(0));
});
EOF

cat >"$project_dir/Program.cs" <<'EOF'
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var serviceDirectory = Path.Combine(builder.AppHostDirectory, "service");

var worker = builder.AddExecutable("worker", "node", serviceDirectory, "server.js")
    .WithHttpEndpoint(env: "PORT");

builder.AddExecutable("dependent", "node", serviceDirectory, "server.js")
    .WithHttpEndpoint(env: "PORT")
    .WaitFor(worker);

builder.Build().Run();
EOF

apphost_path="$project_dir/StartupOtelHarness.AppHost.csproj"

write_step "Starting C# AppHost with telemetry export enabled"
if [[ "$collect_dotnet_traces" == true ]]; then
    invoke_start_with_dotnet_traces
else
    start_stdout="$(invoke_logged_command "start" "$project_dir" env "${startup_env[@]}" "$target_aspire_path" start --format Json --apphost "$apphost_path")"
fi

if (( post_start_delay_seconds > 0 )); then
    write_step "Waiting ${post_start_delay_seconds}s for startup telemetry to flush"
    sleep "$post_start_delay_seconds"
fi

write_step "Stopping C# AppHost"
invoke_logged_command "stop" "$project_dir" env "${startup_env[@]}" "$target_aspire_path" stop --apphost "$apphost_path" >/dev/null

if [[ "$collect_dotnet_traces" == true ]]; then
    stop_active_dotnet_traces

    if ! compgen -G "$dotnet_trace_dir/*.nettrace" >/dev/null; then
        fail "No dotnet-trace files were collected under $dotnet_trace_dir."
    fi
fi

if [[ "$collect_dotnet_binlogs" == true ]]; then
    if ! compgen -G "$dotnet_binlog_dir/*.binlog" >/dev/null; then
        fail "No dotnet MSBuild binlogs were collected under $dotnet_binlog_dir."
    fi
fi

sleep 3

write_step "Exporting standalone dashboard telemetry"
invoke_logged_command "export" "$run_root" env "${startup_env[@]}" "$target_aspire_path" export --dashboard-url "$dashboard_url" --include-hidden --output "$export_zip" >/dev/null

if [[ ! -f "$export_zip" ]]; then
    fail "Export zip was not created: $export_zip."
fi

unzip -q "$export_zip" -d "$export_dir"

REQUIRE_DCP_SPANS="$require_dcp_spans" \
EXPORT_DIR="$export_dir" \
SPAN_SUMMARY_PATH="$span_summary_path" \
RUN_ROOT="$run_root" \
TARGET_ASPIRE_PATH="$target_aspire_path" \
PROFILER_ASPIRE_PATH="$profiler_aspire_path" \
LAYOUT_PATH="$layout_path" \
DCP_PATH="$dcp_path" \
POST_START_DELAY_SECONDS="$post_start_delay_seconds" \
DASHBOARD_URL="$dashboard_url" \
OTLP_GRPC_URL="$otlp_grpc_url" \
OTLP_HTTP_URL="$otlp_http_url" \
APPHOST_PATH="$apphost_path" \
START_JSON_PATH="$start_stdout" \
EXPORT_ZIP="$export_zip" \
DOTNET_TRACE_DIR="$dotnet_trace_dir" \
DOTNET_BINLOG_DIR="$dotnet_binlog_dir" \
node "$repo_root/eng/scripts/validate-startup-otel-export.mjs"

write_step "Startup OTEL harness passed"
