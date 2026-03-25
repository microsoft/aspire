#!/usr/bin/env bash

# Measures .NET Aspire application startup performance by collecting traces.
#
# This script runs an Aspire application, collects a performance trace
# using dotnet-trace, and computes the startup time from AspireEventSource events.
#
# Requires:
#   - bash 4+
#   - dotnet-trace global tool (dotnet tool install -g dotnet-trace)
#   - .NET SDK
#   - python3 (for JSON parsing of launchSettings.json)
#
# Usage:
#   ./measure-startup-performance.sh [options]
#
# Examples:
#   ./measure-startup-performance.sh
#   ./measure-startup-performance.sh --iterations 5
#   ./measure-startup-performance.sh --project-path path/to/MyApp.AppHost.csproj --iterations 3
#   ./measure-startup-performance.sh --iterations 3 --preserve-traces --trace-output-directory /tmp/traces
#   ./measure-startup-performance.sh --verbose

set -euo pipefail

# Constants
EVENT_SOURCE_NAME="Microsoft-Aspire-Hosting"

# Script directory and repo root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

# Defaults
PROJECT_PATH=""
ITERATIONS=1
PRESERVE_TRACES=false
TRACE_OUTPUT_DIRECTORY=""
SKIP_BUILD=false
TRACE_DURATION_SECONDS=60
PAUSE_BETWEEN_ITERATIONS_SECONDS=45
VERBOSE=false

# TraceAnalyzer paths
TRACE_ANALYZER_DIR="$SCRIPT_DIR/TraceAnalyzer"
TRACE_ANALYZER_PROJECT="$TRACE_ANALYZER_DIR/TraceAnalyzer.csproj"

# ANSI color codes
CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Tracked PIDs for cleanup
APP_PID=""
TRACE_PID=""
OUTPUT_DIRECTORY=""
SHOULD_CLEANUP_DIRECTORY=false

# Temp files to clean up
APP_STDOUT_FILE=""
APP_STDERR_FILE=""
TRACE_STDOUT_FILE=""
TRACE_STDERR_FILE=""

# Diagnostic port socket path (for startup trace capture)
DIAG_PORT_PATH=""

# Graceful application host shutdown timeout in seconds
GRACEFUL_SHUTDOWN_TIMEOUT=15

# --------------------------------------------------------------------------
# Utility functions
# --------------------------------------------------------------------------

log_info() {
    echo -e "${CYAN}$*${NC}" >&2
}

log_success() {
    echo -e "${GREEN}$*${NC}" >&2
}

log_warn() {
    echo -e "${YELLOW}WARNING: $*${NC}" >&2
}

log_error() {
    echo -e "${RED}ERROR: $*${NC}" >&2
}

log_verbose() {
    if $VERBOSE; then
        echo "  [VERBOSE] $*" >&2
    fi
}

show_usage() {
    cat <<EOF
Usage: $(basename "$0") [options]

Measures .NET Aspire application startup performance by collecting dotnet-trace
traces and computing the DcpModelCreation duration.

Options:
  --project-path PATH                Path to the AppHost .csproj to measure.
                                     Defaults to TestShop.AppHost in the playground folder.
  --iterations N                     Number of measurement runs (1-100). Default: 1.
  --preserve-traces                  Keep .nettrace files after analysis.
  --trace-output-directory PATH      Directory for preserved trace files.
  --skip-build                       Skip dotnet build before running.
  --trace-duration-seconds N         Maximum trace collection time (1-86400). Default: 60.
  --pause-between-iterations-seconds N
                                     Pause between iterations (0-3600). Default: 45.
  --verbose                          Show detailed output.
  -h, --help                         Show this help message.

Examples:
  $(basename "$0")
  $(basename "$0") --iterations 5
  $(basename "$0") --project-path path/to/MyApp.AppHost.csproj --iterations 3
  $(basename "$0") --iterations 3 --preserve-traces --trace-output-directory /tmp/traces
  $(basename "$0") --verbose
EOF
}

# --------------------------------------------------------------------------
# Argument parsing
# --------------------------------------------------------------------------

parse_args() {
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --project-path)
                PROJECT_PATH="$2"
                shift 2
                ;;
            --iterations)
                ITERATIONS="$2"
                if ! [[ "$ITERATIONS" =~ ^[0-9]+$ ]] || (( ITERATIONS < 1 || ITERATIONS > 100 )); then
                    log_error "Iterations must be between 1 and 100"
                    exit 1
                fi
                shift 2
                ;;
            --preserve-traces)
                PRESERVE_TRACES=true
                shift
                ;;
            --trace-output-directory)
                TRACE_OUTPUT_DIRECTORY="$2"
                shift 2
                ;;
            --skip-build)
                SKIP_BUILD=true
                shift
                ;;
            --trace-duration-seconds)
                TRACE_DURATION_SECONDS="$2"
                if ! [[ "$TRACE_DURATION_SECONDS" =~ ^[0-9]+$ ]] || (( TRACE_DURATION_SECONDS < 1 || TRACE_DURATION_SECONDS > 86400 )); then
                    log_error "Trace duration must be between 1 and 86400 seconds"
                    exit 1
                fi
                shift 2
                ;;
            --pause-between-iterations-seconds)
                PAUSE_BETWEEN_ITERATIONS_SECONDS="$2"
                if ! [[ "$PAUSE_BETWEEN_ITERATIONS_SECONDS" =~ ^[0-9]+$ ]] || (( PAUSE_BETWEEN_ITERATIONS_SECONDS < 0 || PAUSE_BETWEEN_ITERATIONS_SECONDS > 3600 )); then
                    log_error "Pause between iterations must be between 0 and 3600 seconds"
                    exit 1
                fi
                shift 2
                ;;
            --verbose)
                VERBOSE=true
                shift
                ;;
            -h|--help)
                show_usage
                exit 0
                ;;
            *)
                log_error "Unknown option: $1"
                show_usage
                exit 1
                ;;
        esac
    done
}

# --------------------------------------------------------------------------
# Graceful AppHost shutdown
# --------------------------------------------------------------------------

# Stop the AppHost gracefully by sending SIGTERM, which triggers the .NET
# IHostApplicationLifetime shutdown pipeline. This lets the AppHost
# orchestrate a proper DCP shutdown and container cleanup.
# Note: SIGINT cannot be used because bash sets background processes to
# ignore SIGINT. SIGTERM is the standard Unix graceful shutdown signal and
# .NET handles it properly since .NET 6.
stop_apphost() {
    local pid=$1

    if [[ -z "$pid" ]] || ! kill -0 "$pid" 2>/dev/null; then
        return 0
    fi

    log_verbose "Stopping $APP_HOST_NAME gracefully (PID: $pid)..."

    # Send SIGTERM for graceful .NET shutdown
    kill -TERM "$pid" 2>/dev/null || true

    # Wait for graceful shutdown (AppHost needs time to shut down DCP and containers)
    local wait_count=0
    local max_wait=$(( GRACEFUL_SHUTDOWN_TIMEOUT * 10 ))  # tenths of a second
    while kill -0 "$pid" 2>/dev/null && (( wait_count < max_wait )); do
        sleep 0.1
        (( wait_count++ )) || true
    done

    # Last resort: SIGKILL
    if kill -0 "$pid" 2>/dev/null; then
        log_warn "AppHost did not stop within $GRACEFUL_SHUTDOWN_TIMEOUT seconds, sending SIGKILL..."
        kill -9 "$pid" 2>/dev/null || true
    fi

    wait "$pid" 2>/dev/null || true
}

# --------------------------------------------------------------------------
# Cleanup handler
# --------------------------------------------------------------------------

cleanup() {
    local exit_code=$?

    # Stop trace process if still running
    if [[ -n "$TRACE_PID" ]] && kill -0 "$TRACE_PID" 2>/dev/null; then
        log_verbose "Stopping trace collection (PID: $TRACE_PID)..."
        kill -TERM "$TRACE_PID" 2>/dev/null || true
        wait "$TRACE_PID" 2>/dev/null || true
    fi

    # Stop application process gracefully
    if [[ -n "$APP_PID" ]]; then
        stop_apphost "$APP_PID"
    fi

    # Clean up temp files
    local files_to_remove=()
    [[ -n "${APP_STDOUT_FILE:-}" ]] && files_to_remove+=("$APP_STDOUT_FILE")
    [[ -n "${APP_STDERR_FILE:-}" ]] && files_to_remove+=("$APP_STDERR_FILE")
    [[ -n "${TRACE_STDOUT_FILE:-}" ]] && files_to_remove+=("$TRACE_STDOUT_FILE")
    [[ -n "${TRACE_STDERR_FILE:-}" ]] && files_to_remove+=("$TRACE_STDERR_FILE")
    if [[ ${#files_to_remove[@]} -gt 0 ]]; then
        rm -f "${files_to_remove[@]}"
    fi

    # Clean up diagnostic port socket
    if [[ -n "$DIAG_PORT_PATH" ]]; then
        rm -f "$DIAG_PORT_PATH"
    fi

    # Clean up temporary trace directory if not preserving traces
    if $SHOULD_CLEANUP_DIRECTORY && [[ -n "$OUTPUT_DIRECTORY" ]] && [[ -d "$OUTPUT_DIRECTORY" ]]; then
        log_verbose "Cleaning up temporary trace directory: $OUTPUT_DIRECTORY"
        rm -rf "$OUTPUT_DIRECTORY"
    fi

    exit "$exit_code"
}

trap cleanup EXIT INT TERM

# --------------------------------------------------------------------------
# Prerequisites check
# --------------------------------------------------------------------------

check_prerequisites() {
    log_info "Checking prerequisites..."

    if ! command -v dotnet-trace &>/dev/null; then
        log_error "dotnet-trace is not installed. Install it with: dotnet tool install -g dotnet-trace"
        exit 1
    fi
    log_verbose "dotnet-trace found at: $(command -v dotnet-trace)"

    if [[ ! -f "$APP_HOST_PROJECT" ]]; then
        log_error "AppHost project not found at: $APP_HOST_PROJECT"
        exit 1
    fi
    log_verbose "AppHost project found at: $APP_HOST_PROJECT"

    log_success "Prerequisites check passed."
}

# --------------------------------------------------------------------------
# Build functions
# --------------------------------------------------------------------------

build_apphost() {
    log_info "Building $APP_HOST_NAME..."

    local build_output
    if build_output=$(cd "$APP_HOST_DIR" && dotnet build -c Release --nologo 2>&1); then
        log_verbose "$build_output"
        log_success "Build completed successfully."
    else
        echo "$build_output"
        log_error "Failed to build $APP_HOST_NAME"
        exit 1
    fi
}

build_trace_analyzer() {
    if [[ ! -f "$TRACE_ANALYZER_PROJECT" ]]; then
        log_warn "TraceAnalyzer project not found at: $TRACE_ANALYZER_PROJECT"
        return 1
    fi

    log_verbose "Building TraceAnalyzer tool..."
    local build_output
    if build_output=$(dotnet build "$TRACE_ANALYZER_PROJECT" -c Release --verbosity quiet 2>&1); then
        log_verbose "TraceAnalyzer built successfully"
        return 0
    else
        log_warn "Failed to build TraceAnalyzer: $build_output"
        return 1
    fi
}

# --------------------------------------------------------------------------
# Find the compiled executable
# --------------------------------------------------------------------------

find_executable() {
    local exe_path=""
    local dll_path=""

    # Search in multiple possible output locations:
    # 1. Arcade-style: artifacts/bin/<ProjectName>/Release/<tfm>/
    # 2. Traditional: <ProjectDir>/bin/Release/<tfm>/
    local search_paths=(
        "$REPO_ROOT/artifacts/bin/$APP_HOST_NAME/Release"
        "$APP_HOST_DIR/bin/Release"
    )

    for base_path in "${search_paths[@]}"; do
        if [[ ! -d "$base_path" ]]; then
            continue
        fi

        # Find TFM subdirectories (e.g., net8.0, net9.0, net10.0)
        for tfm_dir in "$base_path"/net*/; do
            if [[ ! -d "$tfm_dir" ]]; then
                continue
            fi

            local candidate_exe="$tfm_dir$APP_HOST_NAME"
            local candidate_dll="$tfm_dir$APP_HOST_NAME.dll"

            if [[ -x "$candidate_exe" ]]; then
                exe_path="$candidate_exe"
                log_verbose "Found executable at: $exe_path"
                break
            elif [[ -f "$candidate_dll" ]]; then
                dll_path="$candidate_dll"
                log_verbose "Found DLL at: $dll_path"
                break
            fi
        done

        if [[ -n "$exe_path" ]] || [[ -n "$dll_path" ]]; then
            break
        fi
    done

    if [[ -n "$exe_path" ]]; then
        echo "exe:$exe_path"
    elif [[ -n "$dll_path" ]]; then
        echo "dll:$dll_path"
    else
        local searched
        searched=$(printf "  - %s\n" "${search_paths[@]}")
        log_error "Could not find compiled executable or DLL. Searched in:"
        echo "$searched" >&2
        log_error "Please build the project first (without --skip-build)."
        return 1
    fi
}

# --------------------------------------------------------------------------
# Read launchSettings.json environment variables
# --------------------------------------------------------------------------

read_launch_settings() {
    local launch_settings_path="$APP_HOST_DIR/Properties/launchSettings.json"

    if [[ ! -f "$launch_settings_path" ]]; then
        log_verbose "No launchSettings.json found at: $launch_settings_path"
        return
    fi

    log_verbose "Reading launch settings from: $launch_settings_path"

    # Use python3 to parse launchSettings.json (handles comments and extracts env vars)
    if ! command -v python3 &>/dev/null; then
        log_warn "python3 not available; skipping launchSettings.json parsing"
        return
    fi

    local python_output
    python_output=$(python3 -c "
import json, re, sys

with open(sys.argv[1], 'r') as f:
    content = f.read()

# Strip // comments (only lines where // is the first non-whitespace)
lines = content.splitlines()
filtered = [l for l in lines if not re.match(r'^\s*//', l)]
content = '\n'.join(filtered)

try:
    data = json.loads(content)
except json.JSONDecodeError:
    sys.exit(0)

profiles = data.get('profiles', {})

# Prefer 'http', then 'https', then first profile with environmentVariables
profile = None
for name in ['http', 'https']:
    if name in profiles:
        profile = profiles[name]
        break

if profile is None:
    for name, p in profiles.items():
        if isinstance(p, dict) and p.get('environmentVariables'):
            profile = p
            break

if profile is None:
    sys.exit(0)

env_vars = profile.get('environmentVariables', {})
for k, v in env_vars.items():
    print(f'{k}={v}')

# Use applicationUrl to set ASPNETCORE_URLS if not already set
app_url = profile.get('applicationUrl', '')
if app_url and 'ASPNETCORE_URLS' not in env_vars:
    print(f'ASPNETCORE_URLS={app_url}')
" "$launch_settings_path" 2>/dev/null) || true

    if [[ -n "$python_output" ]]; then
        while IFS='=' read -r key value; do
            if [[ -n "$key" ]]; then
                export "$key=$value"
                log_verbose "  Environment: $key=$value"
            fi
        done <<< "$python_output"
    fi
}

# --------------------------------------------------------------------------
# Analyze trace with TraceAnalyzer
# --------------------------------------------------------------------------

analyze_trace() {
    local trace_path="$1"

    log_info "Analyzing trace: $trace_path"

    if [[ ! -f "$trace_path" ]]; then
        log_warn "Trace file not found: $trace_path"
        return 1
    fi

    local output
    if output=$(dotnet run --project "$TRACE_ANALYZER_PROJECT" -c Release --no-build -- "$trace_path" 2>&1); then
        local result
        result=$(echo "$output" | tail -n 1)
        if [[ "$result" == "null" ]]; then
            log_warn "Could not find DcpModelCreation events in the trace"
            return 1
        fi
        echo "$result"
        return 0
    else
        log_warn "TraceAnalyzer failed: $output"
        return 1
    fi
}

# --------------------------------------------------------------------------
# Run a single performance iteration
# --------------------------------------------------------------------------

run_iteration() {
    local iteration_number=$1
    local trace_output_path=$2
    local nettrace_path="${trace_output_path}.nettrace"

    echo "" >&2
    echo -e "${YELLOW}Iteration $iteration_number${NC}" >&2
    echo -e "${YELLOW}$(printf '%0.s-' {1..40})${NC}" >&2

    # Reset tracked PIDs and temp files for this iteration
    APP_PID=""
    TRACE_PID=""
    APP_STDOUT_FILE=""
    APP_STDERR_FILE=""
    TRACE_STDOUT_FILE=""
    TRACE_STDERR_FILE=""
    DIAG_PORT_PATH=""

    # Find the compiled executable
    local found
    if ! found=$(find_executable); then
        return 1
    fi

    local exe_type="${found%%:*}"
    local exe_path="${found#*:}"

    # Read launch settings (exports env vars into current shell)
    read_launch_settings

    # Always ensure Development environment is set
    export DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Development}"
    export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"

    # Convert TraceDurationSeconds to dd:hh:mm:ss format
    local days hours minutes seconds
    days=$(( TRACE_DURATION_SECONDS / 86400 ))
    hours=$(( (TRACE_DURATION_SECONDS % 86400) / 3600 ))
    minutes=$(( (TRACE_DURATION_SECONDS % 3600) / 60 ))
    seconds=$(( TRACE_DURATION_SECONDS % 60 ))
    local trace_duration
    trace_duration=$(printf '%02d:%02d:%02d:%02d' "$days" "$hours" "$minutes" "$seconds")

    # Use a diagnostic port so dotnet-trace captures ALL startup events.
    # Flow: dotnet-trace listens on a socket -> AppHost starts and connects ->
    # runtime suspends -> dotnet-trace creates EventPipe session and resumes
    # the runtime -> all startup events (including DcpModelCreation) are captured.
    # Note: macOS mktemp requires X's at the end of the template (no suffix allowed),
    # so we generate the path without a .sock extension.
    DIAG_PORT_PATH=$(mktemp -u "${TMPDIR:-/tmp}/aspire-diag-XXXXXXXX")

    # Start dotnet-trace first — it listens on the diagnostic port socket
    log_info "Starting trace collection on diagnostic port..."

    local trace_args=(
        collect
        --diagnostic-port "$DIAG_PORT_PATH"
        --providers "$EVENT_SOURCE_NAME"
        --output "$nettrace_path"
        --format nettrace
        --duration "$trace_duration"
        --buffersize 8192
    )

    log_verbose "dotnet-trace arguments: ${trace_args[*]}"

    TRACE_STDOUT_FILE=$(mktemp)
    TRACE_STDERR_FILE=$(mktemp)

    dotnet-trace "${trace_args[@]}" > "$TRACE_STDOUT_FILE" 2> "$TRACE_STDERR_FILE" &
    TRACE_PID=$!

    # Wait for dotnet-trace to create the diagnostic port socket
    local wait_count=0
    while [[ ! -S "$DIAG_PORT_PATH" ]] && (( wait_count < 50 )); do
        sleep 0.1
        (( wait_count++ )) || true
        # Check that dotnet-trace is still running
        if ! kill -0 "$TRACE_PID" 2>/dev/null; then
            local trace_error
            trace_error=$(cat "$TRACE_STDERR_FILE" 2>/dev/null || true)
            log_error "dotnet-trace exited before creating diagnostic port."
            [[ -n "$trace_error" ]] && log_verbose "dotnet-trace stderr: $trace_error"
            rm -f "$TRACE_STDOUT_FILE" "$TRACE_STDERR_FILE" "$DIAG_PORT_PATH"
            TRACE_PID=""
            TRACE_STDOUT_FILE=""
            TRACE_STDERR_FILE=""
            DIAG_PORT_PATH=""
            return 1
        fi
    done

    if [[ ! -S "$DIAG_PORT_PATH" ]]; then
        log_error "Timed out waiting for diagnostic port socket at: $DIAG_PORT_PATH"
        kill -TERM "$TRACE_PID" 2>/dev/null || true
        wait "$TRACE_PID" 2>/dev/null || true
        rm -f "$TRACE_STDOUT_FILE" "$TRACE_STDERR_FILE" "$DIAG_PORT_PATH"
        TRACE_PID=""
        TRACE_STDOUT_FILE=""
        TRACE_STDERR_FILE=""
        DIAG_PORT_PATH=""
        return 1
    fi

    log_verbose "Diagnostic port ready at: $DIAG_PORT_PATH"

    # Now start the AppHost — the runtime will connect to the diagnostic port,
    # suspend until dotnet-trace sets up the EventPipe session, then resume.
    log_info "Starting $APP_HOST_NAME..."

    APP_STDOUT_FILE=$(mktemp)
    APP_STDERR_FILE=$(mktemp)

    if [[ "$exe_type" == "exe" ]]; then
        DOTNET_DiagnosticPorts="$DIAG_PORT_PATH" "$exe_path" > "$APP_STDOUT_FILE" 2> "$APP_STDERR_FILE" &
    else
        DOTNET_DiagnosticPorts="$DIAG_PORT_PATH" dotnet "$exe_path" > "$APP_STDOUT_FILE" 2> "$APP_STDERR_FILE" &
    fi
    APP_PID=$!

    log_verbose "$APP_HOST_NAME started with PID: $APP_PID"

    log_info "Collecting performance trace..."

    # Wait for trace to complete
    local trace_exit_code=0
    wait "$TRACE_PID" || trace_exit_code=$?
    TRACE_PID=""

    local trace_output trace_error
    trace_output=$(cat "$TRACE_STDOUT_FILE" 2>/dev/null || true)
    trace_error=$(cat "$TRACE_STDERR_FILE" 2>/dev/null || true)
    rm -f "$TRACE_STDOUT_FILE" "$TRACE_STDERR_FILE"
    TRACE_STDOUT_FILE=""
    TRACE_STDERR_FILE=""

    [[ -n "$trace_output" ]] && log_verbose "dotnet-trace output: $trace_output"
    [[ -n "$trace_error" ]] && log_verbose "dotnet-trace stderr: $trace_error"

    if [[ $trace_exit_code -ne 0 ]]; then
        if [[ -f "$nettrace_path" ]]; then
            log_warn "dotnet-trace exited with code $trace_exit_code, but trace file was created. Attempting to analyze."
        else
            log_warn "dotnet-trace exited with code $trace_exit_code and no trace file was created."
            # Gracefully stop the AppHost
            stop_apphost "$APP_PID"
            rm -f "$APP_STDOUT_FILE" "$APP_STDERR_FILE" "$DIAG_PORT_PATH"
            APP_PID=""
            APP_STDOUT_FILE=""
            APP_STDERR_FILE=""
            DIAG_PORT_PATH=""
            echo ""
            return 0
        fi
    fi

    log_success "Trace collection completed."

    # Read app output for verbose logging
    if $VERBOSE; then
        local app_stdout app_stderr
        app_stdout=$(cat "$APP_STDOUT_FILE" 2>/dev/null || true)
        app_stderr=$(cat "$APP_STDERR_FILE" 2>/dev/null || true)
        [[ -n "$app_stdout" ]] && log_verbose "Application stdout: $app_stdout"
        [[ -n "$app_stderr" ]] && log_verbose "Application stderr: $app_stderr"
    fi

    # Gracefully stop the AppHost (SIGTERM -> wait for DCP cleanup -> SIGKILL)
    stop_apphost "$APP_PID"
    rm -f "$APP_STDOUT_FILE" "$APP_STDERR_FILE" "$DIAG_PORT_PATH"
    APP_PID=""
    APP_STDOUT_FILE=""
    APP_STDERR_FILE=""
    DIAG_PORT_PATH=""

    if [[ -f "$nettrace_path" ]]; then
        echo "$nettrace_path"
    else
        echo ""
    fi
}

# --------------------------------------------------------------------------
# Compute and display statistics
# --------------------------------------------------------------------------

print_statistics() {
    local -a times=("$@")
    local count=${#times[@]}

    if (( count == 0 )); then
        return
    fi

    # Use awk for all statistics computation
    local stats
    stats=$(printf '%s\n' "${times[@]}" | awk '
    BEGIN { min = 999999999; max = 0; sum = 0; n = 0 }
    {
        val = $1 + 0
        sum += val
        if (val < min) min = val
        if (val > max) max = val
        values[n] = val
        n++
    }
    END {
        avg = sum / n
        if (n > 1) {
            sumsq = 0
            for (i = 0; i < n; i++) {
                sumsq += (values[i] - avg) ^ 2
            }
            stddev = sqrt(sumsq / n)
            printf "%.2f %.2f %.2f %.2f\n", min, max, avg, stddev
        } else {
            printf "%.2f %.2f %.2f -\n", min, max, avg
        }
    }')

    local min max avg stddev
    read -r min max avg stddev <<< "$stats"

    echo ""
    echo -e "${YELLOW}Statistics:${NC}"
    echo "  Successful iterations: $count / $ITERATIONS"
    echo "  Minimum: $min ms"
    echo "  Maximum: $max ms"
    echo "  Average: $avg ms"
    if [[ "$stddev" != "-" ]]; then
        echo "  Std Dev: $stddev ms"
    fi
}

# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------

main() {
    parse_args "$@"

    # Resolve project path
    if [[ -z "$PROJECT_PATH" ]]; then
        PROJECT_PATH="$REPO_ROOT/playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj"
    elif [[ "$PROJECT_PATH" != /* ]]; then
        PROJECT_PATH="$(cd "$(dirname "$PROJECT_PATH")" && pwd)/$(basename "$PROJECT_PATH")"
    fi

    APP_HOST_PROJECT="$PROJECT_PATH"
    APP_HOST_DIR="$(dirname "$APP_HOST_PROJECT")"
    APP_HOST_NAME="$(basename "$APP_HOST_PROJECT" .csproj)"

    # Determine output directory for traces
    if [[ -n "$TRACE_OUTPUT_DIRECTORY" ]]; then
        OUTPUT_DIRECTORY="$TRACE_OUTPUT_DIRECTORY"
    else
        OUTPUT_DIRECTORY="$(mktemp -d "${TMPDIR:-/tmp}/aspire-perf-XXXXXXXX")"
    fi

    # Only delete temp directory if not preserving traces and no custom directory was specified
    if ! $PRESERVE_TRACES && [[ -z "$TRACE_OUTPUT_DIRECTORY" ]]; then
        SHOULD_CLEANUP_DIRECTORY=true
    fi

    # Ensure output directory exists
    mkdir -p "$OUTPUT_DIRECTORY"

    # Print header
    echo -e "${CYAN}==================================================${NC}"
    echo -e "${CYAN} Aspire Startup Performance Measurement${NC}"
    echo -e "${CYAN}==================================================${NC}"
    echo ""
    echo "Project: $APP_HOST_NAME"
    echo "Project Path: $APP_HOST_PROJECT"
    echo "Iterations: $ITERATIONS"
    echo "Trace Duration: $TRACE_DURATION_SECONDS seconds"
    echo "Pause Between Iterations: $PAUSE_BETWEEN_ITERATIONS_SECONDS seconds"
    echo "Preserve Traces: $PRESERVE_TRACES"
    if $PRESERVE_TRACES || [[ -n "$TRACE_OUTPUT_DIRECTORY" ]]; then
        echo "Trace Directory: $OUTPUT_DIRECTORY"
    fi
    echo ""

    check_prerequisites

    # Build the TraceAnalyzer tool
    local trace_analyzer_available=false
    if build_trace_analyzer; then
        trace_analyzer_available=true
    fi

    if ! $SKIP_BUILD; then
        build_apphost
    else
        echo -e "${YELLOW}Skipping build (--skip-build flag set)${NC}"
    fi

    # Collect results: arrays of iteration numbers, trace paths, and startup times
    local -a result_iterations=()
    local -a result_traces=()
    local -a result_times=()
    local timestamp
    timestamp=$(date '+%Y%m%d_%H%M%S')

    for (( i = 1; i <= ITERATIONS; i++ )); do
        local trace_base_name="${APP_HOST_NAME}_startup_${timestamp}_iter${i}"
        local trace_output_path="$OUTPUT_DIRECTORY/$trace_base_name"

        local trace_path
        trace_path=$(run_iteration "$i" "$trace_output_path") || true

        if [[ -n "$trace_path" ]] && [[ -f "$trace_path" ]]; then
            local duration=""
            if $trace_analyzer_available; then
                duration=$(analyze_trace "$trace_path") || true
            fi

            result_iterations+=("$i")
            result_traces+=("$trace_path")

            if [[ -n "$duration" ]]; then
                # Round to 2 decimal places
                duration=$(printf '%.2f' "$duration")
                result_times+=("$duration")
                log_success "Startup time: $duration ms"
            else
                result_times+=("")
                log_success "Trace collected: $trace_path"
            fi
        else
            log_warn "No trace file generated for iteration $i"
        fi

        # Pause between iterations
        if (( i < ITERATIONS )) && (( PAUSE_BETWEEN_ITERATIONS_SECONDS > 0 )); then
            log_verbose "Pausing for $PAUSE_BETWEEN_ITERATIONS_SECONDS seconds before next iteration..."
            sleep "$PAUSE_BETWEEN_ITERATIONS_SECONDS"
        fi
    done

    # Summary
    echo ""
    echo -e "${CYAN}==================================================${NC}"
    echo -e "${CYAN} Results Summary${NC}"
    echo -e "${CYAN}==================================================${NC}"

    # Collect valid (non-empty) times
    local -a valid_times=()
    for t in "${result_times[@]}"; do
        if [[ -n "$t" ]]; then
            valid_times+=("$t")
        fi
    done

    if (( ${#valid_times[@]} > 0 )); then
        echo ""
        # Print results table
        if $PRESERVE_TRACES; then
            printf "%-10s %-15s %s\n" "Iteration" "StartupTimeMs" "TracePath"
            printf "%-10s %-15s %s\n" "---------" "-------------" "---------"
            for idx in "${!result_iterations[@]}"; do
                local time_val="${result_times[$idx]}"
                if [[ -z "$time_val" ]]; then time_val="-"; fi
                printf "%-10s %-15s %s\n" "${result_iterations[$idx]}" "$time_val" "${result_traces[$idx]}"
            done
        else
            printf "%-10s %s\n" "Iteration" "StartupTimeMs"
            printf "%-10s %s\n" "---------" "-------------"
            for idx in "${!result_iterations[@]}"; do
                local time_val="${result_times[$idx]}"
                if [[ -z "$time_val" ]]; then time_val="-"; fi
                printf "%-10s %s\n" "${result_iterations[$idx]}" "$time_val"
            done
        fi

        print_statistics "${valid_times[@]}"

        if $PRESERVE_TRACES; then
            echo ""
            log_info "Trace files saved to: $OUTPUT_DIRECTORY"
        fi
    elif (( ${#result_iterations[@]} > 0 )); then
        echo ""
        echo -e "${YELLOW}Collected ${#result_iterations[@]} trace(s) but could not extract timing.${NC}"
        if $PRESERVE_TRACES; then
            echo ""
            log_info "Trace files saved to: $OUTPUT_DIRECTORY"
            printf "%-10s %s\n" "Iteration" "TracePath"
            printf "%-10s %s\n" "---------" "---------"
            for idx in "${!result_iterations[@]}"; do
                printf "%-10s %s\n" "${result_iterations[$idx]}" "${result_traces[$idx]}"
            done
            echo ""
            echo -e "${YELLOW}Open traces in PerfView or Visual Studio to analyze startup timing.${NC}"
        fi
    else
        log_warn "No traces were collected."
    fi
}

main "$@"
