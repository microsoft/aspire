#!/usr/bin/env bash

# Repeatedly runs a dotnet test command to validate flaky test fixes.
# Cleanup logic modeled on tests/helix/send-to-helix-inner.proj pre-commands.

set -u # Error on undefined variables

# ---------- defaults ----------
ITERATIONS=100
STOP_ON_FAILURE=true

# ---------- usage ----------
usage() {
    cat <<'EOF'
Usage: .github/skills/fix-flaky-test/run-test-repeatedly.sh [OPTIONS] -- <test command...>

Runs <test command> repeatedly to validate flaky test fixes.
Everything after '--' is executed verbatim each iteration.

Options:
  -n <count>    Number of iterations (default: 100)
  --run-all     Don't stop on first failure, run all iterations
  --help        Show this help message

Examples:
  .github/skills/fix-flaky-test/run-test-repeatedly.sh -- dotnet test tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj --no-build -- --filter-method "*.MyTest"
  .github/skills/fix-flaky-test/run-test-repeatedly.sh -n 50 --run-all -- dotnet test tests/Foo/Foo.csproj -- --filter-method "*.Bar"
EOF
    exit 0
}

# ---------- parse options ----------
TEST_CMD=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        -n)
            ITERATIONS="$2"
            shift 2
            ;;
        --run-all)
            STOP_ON_FAILURE=false
            shift
            ;;
        --help)
            usage
            ;;
        --)
            shift
            TEST_CMD=("$@")
            break
            ;;
        *)
            echo "Unknown option: $1" >&2
            echo "Run with --help for usage." >&2
            exit 1
            ;;
    esac
done

if [[ ${#TEST_CMD[@]} -eq 0 ]]; then
    echo "Error: no test command provided. Use -- to separate options from the test command." >&2
    echo "Run with --help for usage." >&2
    exit 1
fi

# ---------- detect CI vs local ----------
DOCKER_CLEANUP="true"
if [[ -z "${CI:-}" ]] && [[ -z "${GITHUB_ACTIONS:-}" ]]; then
    if command -v docker &>/dev/null; then
        echo "⚠️  This script removes ALL Docker containers and volumes between iterations."
        read -p "Allow Docker cleanup? (y/N) " -r
        if [[ ! "$REPLY" =~ ^[Yy]$ ]]; then
            DOCKER_CLEANUP="false"
            echo "Docker cleanup disabled. Note: leftover containers may affect test results."
        fi
    fi
fi

# ---------- infer test assembly name from .csproj in args ----------
TEST_ASSEMBLY_NAME=""
TEST_PROJECT_DIR=""
for arg in "${TEST_CMD[@]}"; do
    if [[ "$arg" == *.csproj ]]; then
        TEST_ASSEMBLY_NAME="$(basename "$arg" .csproj)"
        TEST_PROJECT_DIR="$(dirname "$arg")"
        break
    fi
done

# ---------- setup ----------
RESULTS_DIR="/tmp/test-results-$(date +%Y%m%d-%H%M%S)"
LOG_FILE="$RESULTS_DIR/test-run.log"
mkdir -p "$RESULTS_DIR"

PASS_COUNT=0
FAIL_COUNT=0
TIMEOUT_COUNT=0

GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

# ---------- portable timeout wrapper ----------
# macOS doesn't ship GNU timeout; fall back to a bash implementation.
if command -v timeout &>/dev/null; then
    run_with_timeout() { timeout "$@"; }
else
    run_with_timeout() {
        local secs="$1"; shift
        "$@" &
        local pid=$!
        ( sleep "$secs" && kill -9 "$pid" 2>/dev/null ) &
        local watcher=$!
        wait "$pid" 2>/dev/null
        local rc=$?
        kill "$watcher" 2>/dev/null
        wait "$watcher" 2>/dev/null
        # If the process was killed by our watcher, simulate timeout exit code 124.
        if [[ $rc -eq 137 ]]; then
            return 124
        fi
        return $rc
    }
fi

# ---------- cleanup ----------
clean_environment() {
    # Kill dcp / dcpctrl processes using pgrep + while loop
    pgrep -lf "dcp" 2>/dev/null | grep -E "\bdcp(\.exe|ctl)?\b" | awk '{print $1}' | while read pid; do
        kill -9 "$pid" 2>/dev/null || true
    done

    # Kill dotnet-tests processes (actual test runner)
    pgrep -f "dotnet-tests" 2>/dev/null | while read pid; do
        kill -9 "$pid" 2>/dev/null || true
    done
    
    # Don't kill "dotnet test" as it may match our script's own command line args
    # The dotnet test launcher exits when tests complete, so no need to kill it

    # Kill test service processes (TestProject.Service*, TestProject.Worker*)
    # Be specific to avoid killing this script
    pgrep -f "TestProject\.Service" 2>/dev/null | while read pid; do
        kill -9 "$pid" 2>/dev/null || true
    done
    pgrep -f "TestProject\.Worker" 2>/dev/null | while read pid; do
        kill -9 "$pid" 2>/dev/null || true
    done

    # Brief wait for processes to die
    sleep 1

    # Docker cleanup — only if docker is available and cleanup is approved
    if command -v docker &>/dev/null && [[ "$DOCKER_CLEANUP" == "true" ]]; then
        local containers
        containers="$(docker ps -aq 2>/dev/null)"
        if [[ -n "$containers" ]]; then
            echo "$containers" | xargs docker stop 2>/dev/null || true
            echo "$containers" | xargs docker rm 2>/dev/null || true
        fi

        local volumes
        volumes="$(docker volume ls -q 2>/dev/null)"
        if [[ -n "$volumes" ]]; then
            echo "$volumes" | xargs docker volume rm 2>/dev/null || true
        fi

        docker network prune -f 2>/dev/null || true
    fi

    # Clean test result directories under the project path
    if [[ -n "$TEST_PROJECT_DIR" ]]; then
        rm -rf "$TEST_PROJECT_DIR/TestResults" 2>/dev/null || true
    fi
}

# ---------- state logging ----------
log_environment_state() {
    local iteration=$1
    local log_target="$2"

    {
        echo "--- Environment state before cleanup (iteration $iteration) ---"
        if command -v docker &>/dev/null; then
            echo ">> docker container ls --all"
            docker container ls --all 2>&1 || true
            echo ">> docker volume ls"
            docker volume ls 2>&1 || true
            echo ">> docker network ls"
            docker network ls 2>&1 || true
        fi
        echo ">> processes (dcp|dotnet)"
        pgrep -lf "dcp|dotnet" 2>&1 || echo "(none)"
        echo "--- end environment state ---"
    } >> "$log_target" 2>&1
}

# ---------- header ----------
{
    echo "========================================"
    echo "Test Verification Run — $ITERATIONS iterations"
    if $STOP_ON_FAILURE; then
        echo "Mode: STOP ON FIRST FAILURE"
    else
        echo "Mode: RUN ALL"
    fi
    echo "========================================"
    echo "Command: ${TEST_CMD[*]}"
    echo "Test assembly: ${TEST_ASSEMBLY_NAME:-(unknown)}"
    echo "Results: $RESULTS_DIR"
    echo "Started: $(date)"
    echo "Git commit: $(git rev-parse HEAD 2>/dev/null || echo 'N/A')"
    echo "========================================"
    echo ""
} | tee -a "$LOG_FILE"

# ---------- main loop ----------
FIRST_FAILURE_ITERATION=0

for i in $(seq 1 "$ITERATIONS"); do
    ITER_LOG="$RESULTS_DIR/iteration-$i.log"

    # Log environment state, then clean
    log_environment_state "$i" "$ITER_LOG"
    clean_environment

    # Print iteration header
    printf "Iteration %d/%d [%s]: " "$i" "$ITERATIONS" "$(date +%H:%M:%S)" | tee -a "$LOG_FILE"

    # Log exact command
    echo "Running: ${TEST_CMD[*]}" >> "$ITER_LOG"

    # Run test with 3-minute timeout
    run_with_timeout 180 "${TEST_CMD[@]}" >> "$ITER_LOG" 2>&1
    EXIT_CODE=$?

    # Detect zero-test runs (exit code 8 is masked by --ignore-exit-code 8 in Testing.props)
    if [[ $EXIT_CODE -eq 0 ]]; then
        if ! grep -qE "Total:\s*[1-9]" "$ITER_LOG" 2>/dev/null; then
            EXIT_CODE=8
        fi
    fi

    # Classify result
    if [[ $EXIT_CODE -eq 0 ]]; then
        echo -e "${GREEN}PASS${NC}" | tee -a "$LOG_FILE"
        PASS_COUNT=$((PASS_COUNT + 1))
    elif [[ $EXIT_CODE -eq 8 ]]; then
        echo -e "${YELLOW}ZERO TESTS${NC} (no tests matched filter — check quarantine/filter settings)" | tee -a "$LOG_FILE"
        FAIL_COUNT=$((FAIL_COUNT + 1))
        cp "$ITER_LOG" "$RESULTS_DIR/failure-$i.log"
    elif [[ $EXIT_CODE -eq 124 ]]; then
        echo -e "${YELLOW}TIMEOUT${NC}" | tee -a "$LOG_FILE"
        TIMEOUT_COUNT=$((TIMEOUT_COUNT + 1))
        cp "$ITER_LOG" "$RESULTS_DIR/timeout-$i.log"
    else
        echo -e "${RED}FAIL${NC} (exit $EXIT_CODE)" | tee -a "$LOG_FILE"
        FAIL_COUNT=$((FAIL_COUNT + 1))
        cp "$ITER_LOG" "$RESULTS_DIR/failure-$i.log"
    fi

    # Handle failure
    if [[ $EXIT_CODE -ne 0 ]]; then
        if [[ $FIRST_FAILURE_ITERATION -eq 0 ]]; then
            FIRST_FAILURE_ITERATION=$i
        fi
        if $STOP_ON_FAILURE; then
            echo "" | tee -a "$LOG_FILE"
            echo -e "${RED}Stopping at iteration $i due to failure.${NC}" | tee -a "$LOG_FILE"
            break
        fi
    fi

    # Progress every 10 iterations
    if [[ $((i % 10)) -eq 0 ]]; then
        echo -e "${BLUE}  Progress: $i/$ITERATIONS — Pass: $PASS_COUNT, Fail: $FAIL_COUNT, Timeout: $TIMEOUT_COUNT${NC}" | tee -a "$LOG_FILE"
    fi
done

# ---------- final cleanup ----------
clean_environment

# ---------- summary ----------
TOTAL=$((PASS_COUNT + FAIL_COUNT + TIMEOUT_COUNT))
pct() { awk "BEGIN {printf \"%.1f\", ($1/$2)*100}"; }

echo "" | tee -a "$LOG_FILE"
echo "========================================" | tee -a "$LOG_FILE"
echo "Summary" | tee -a "$LOG_FILE"
echo "========================================" | tee -a "$LOG_FILE"
if [[ $FIRST_FAILURE_ITERATION -gt 0 ]] && $STOP_ON_FAILURE; then
    echo -e "${RED}Stopped at iteration $FIRST_FAILURE_ITERATION${NC}" | tee -a "$LOG_FILE"
fi
echo "Completed: $TOTAL / $ITERATIONS" | tee -a "$LOG_FILE"
echo -e "  ${GREEN}Pass:    $PASS_COUNT${NC} ($(pct $PASS_COUNT $TOTAL)%)" | tee -a "$LOG_FILE"
echo -e "  ${RED}Fail:    $FAIL_COUNT${NC} ($(pct $FAIL_COUNT $TOTAL)%)" | tee -a "$LOG_FILE"
echo -e "  ${YELLOW}Timeout: $TIMEOUT_COUNT${NC} ($(pct $TIMEOUT_COUNT $TOTAL)%)" | tee -a "$LOG_FILE"
echo "========================================" | tee -a "$LOG_FILE"
echo "Finished: $(date)" | tee -a "$LOG_FILE"
echo "Results:  $RESULTS_DIR" | tee -a "$LOG_FILE"

if [[ $FAIL_COUNT -eq 0 ]] && [[ $TIMEOUT_COUNT -eq 0 ]] && [[ $TOTAL -eq $ITERATIONS ]]; then
    echo "" | tee -a "$LOG_FILE"
    echo -e "${GREEN}All $ITERATIONS iterations passed.${NC}" | tee -a "$LOG_FILE"
    exit 0
else
    echo "" | tee -a "$LOG_FILE"
    echo "Failure logs: $RESULTS_DIR/failure-*.log" | tee -a "$LOG_FILE"
    echo "Timeout logs: $RESULTS_DIR/timeout-*.log" | tee -a "$LOG_FILE"
    exit 1
fi
