#!/usr/bin/env bash

# verify-cli-archive.sh - Verify that a signed Aspire CLI archive produces a working binary.
#
# Usage: ./verify-cli-archive.sh <archive-path> [--dotnet-root <path>]
#
# This script:
#   1. Cleans ~/.aspire to ensure no stale state
#   2. Extracts the CLI archive to a temp location
#   3. Runs 'aspire --version' to validate the binary executes
#   4. Runs 'aspire new aspire-starter --name VerifyApp' to test bundle self-extraction + project creation
#   5. Runs 'aspire restore' on the created project to test NuGet restore via aspire-managed
#   6. Runs 'dotnet build' on the created project to validate the template compiles
#   7. Cleans up temp directories

set -euo pipefail

readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly YELLOW='\033[1;33m'
readonly CYAN='\033[0;36m'
readonly RESET='\033[0m'

ARCHIVE_PATH=""
DOTNET_ROOT=""
SKIP_BUILD=false

log_step() { echo -e "${CYAN}▶ $1${RESET}"; }
log_ok()   { echo -e "${GREEN}✅ $1${RESET}"; }
log_warn() { echo -e "${YELLOW}⚠️  $1${RESET}"; }
log_err()  { echo -e "${RED}❌ $1${RESET}"; }

show_help() {
    cat << 'EOF'
Aspire CLI Archive Verification Script

USAGE:
    verify-cli-archive.sh <archive-path> [OPTIONS]

ARGUMENTS:
    <archive-path>    Path to the CLI archive (.tar.gz or .zip)

OPTIONS:
    --dotnet-root     Path to the .NET SDK root (defaults to system dotnet)
    --skip-build      Skip the project build step (only verify extraction + version)
    -h, --help        Show this help message

DESCRIPTION:
    Verifies that a signed Aspire CLI archive produces a working binary by:
    1. Extracting the archive
    2. Running 'aspire --version'
    3. Creating a new project with 'aspire new'
    4. Restoring + building the created project
EOF
    exit 0
}

cleanup() {
    local exit_code=$?
    if [[ -n "${VERIFY_TMPDIR:-}" ]] && [[ -d "${VERIFY_TMPDIR}" ]]; then
        log_step "Cleaning up temp directory: ${VERIFY_TMPDIR}"
        rm -rf "${VERIFY_TMPDIR}"
    fi
    # Restore ~/.aspire if we backed it up
    if [[ -n "${ASPIRE_BACKUP:-}" ]] && [[ -d "${ASPIRE_BACKUP}" ]]; then
        if [[ -d "$HOME/.aspire" ]]; then
            rm -rf "$HOME/.aspire"
        fi
        mv "${ASPIRE_BACKUP}" "$HOME/.aspire"
        log_step "Restored original ~/.aspire"
    fi
    if [[ $exit_code -ne 0 ]]; then
        log_err "Verification FAILED (exit code: $exit_code)"
    fi
    exit $exit_code
}

trap cleanup EXIT

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --dotnet-root)
            DOTNET_ROOT="$2"
            shift 2
            ;;
        --skip-build)
            SKIP_BUILD=true
            shift
            ;;
        -h|--help)
            show_help
            ;;
        -*)
            echo "Unknown option: $1" >&2
            exit 1
            ;;
        *)
            if [[ -z "$ARCHIVE_PATH" ]]; then
                ARCHIVE_PATH="$1"
            else
                echo "Unexpected argument: $1" >&2
                exit 1
            fi
            shift
            ;;
    esac
done

if [[ -z "$ARCHIVE_PATH" ]]; then
    echo "Error: archive path is required." >&2
    echo "Usage: verify-cli-archive.sh <archive-path> [--dotnet-root <path>]" >&2
    exit 1
fi

if [[ ! -f "$ARCHIVE_PATH" ]]; then
    log_err "Archive not found: $ARCHIVE_PATH"
    exit 1
fi

# Set up dotnet if specified
if [[ -n "$DOTNET_ROOT" ]]; then
    export DOTNET_ROOT
    export PATH="$DOTNET_ROOT:$PATH"
fi

echo ""
echo "=========================================="
echo "  Aspire CLI Archive Verification"
echo "=========================================="
echo "  Archive: $ARCHIVE_PATH"
echo "  dotnet:  $(which dotnet 2>/dev/null || echo 'not found')"
echo "=========================================="
echo ""

# Step 0: Verify dotnet is available
log_step "Checking dotnet SDK availability..."
if ! command -v dotnet &>/dev/null; then
    log_err "dotnet command not found. Set --dotnet-root or ensure dotnet is in PATH."
    exit 1
fi
dotnet --version
log_ok "dotnet SDK available"

# Step 1: Back up and clean ~/.aspire
log_step "Cleaning ~/.aspire state..."
ASPIRE_BACKUP=""
if [[ -d "$HOME/.aspire" ]]; then
    ASPIRE_BACKUP="$(mktemp -d)"
    # Move existing .aspire to backup (preserve for restoration in cleanup)
    mv "$HOME/.aspire" "${ASPIRE_BACKUP}/.aspire"
    ASPIRE_BACKUP="${ASPIRE_BACKUP}/.aspire"
    log_step "Backed up existing ~/.aspire to ${ASPIRE_BACKUP}"
fi
log_ok "Clean ~/.aspire state"

# Step 2: Extract the archive
VERIFY_TMPDIR="$(mktemp -d)"
EXTRACT_DIR="${VERIFY_TMPDIR}/cli"
mkdir -p "$EXTRACT_DIR"

log_step "Extracting archive to ${EXTRACT_DIR}..."
if [[ "$ARCHIVE_PATH" == *.tar.gz ]]; then
    tar -xzf "$ARCHIVE_PATH" -C "$EXTRACT_DIR"
elif [[ "$ARCHIVE_PATH" == *.zip ]]; then
    unzip -q "$ARCHIVE_PATH" -d "$EXTRACT_DIR"
else
    log_err "Unsupported archive format: $ARCHIVE_PATH (expected .tar.gz or .zip)"
    exit 1
fi

# Find the aspire binary
ASPIRE_BIN=""
if [[ -f "$EXTRACT_DIR/aspire" ]]; then
    ASPIRE_BIN="$EXTRACT_DIR/aspire"
elif [[ -f "$EXTRACT_DIR/aspire.exe" ]]; then
    ASPIRE_BIN="$EXTRACT_DIR/aspire.exe"
else
    log_err "Could not find 'aspire' binary in extracted archive. Contents:"
    ls -la "$EXTRACT_DIR"
    exit 1
fi

chmod +x "$ASPIRE_BIN"
log_ok "Extracted CLI binary: $ASPIRE_BIN"

# Install the CLI to ~/.aspire/bin so self-extraction works correctly
log_step "Installing CLI to ~/.aspire/bin..."
mkdir -p "$HOME/.aspire/bin"
cp "$ASPIRE_BIN" "$HOME/.aspire/bin/"
ASPIRE_BIN="$HOME/.aspire/bin/$(basename "$ASPIRE_BIN")"
chmod +x "$ASPIRE_BIN"
export PATH="$HOME/.aspire/bin:$PATH"
log_ok "CLI installed to ~/.aspire/bin"

# Step 3: Verify aspire --version
log_step "Running 'aspire --version'..."
VERSION_OUTPUT=$("$ASPIRE_BIN" --version 2>&1) || {
    log_err "'aspire --version' failed with exit code $?"
    echo "Output: $VERSION_OUTPUT"
    exit 1
}
echo "  Version: $VERSION_OUTPUT"
log_ok "'aspire --version' succeeded"

# Step 4: Create a new project with aspire new
PROJECT_DIR="${VERIFY_TMPDIR}/VerifyApp"
mkdir -p "$PROJECT_DIR"

log_step "Running 'aspire new aspire-starter --name VerifyApp'..."
export DOTNET_CLI_TELEMETRY_OPTOUT=true
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false

# aspire new needs a working directory and creates the project there
(
    cd "$PROJECT_DIR"
    "$ASPIRE_BIN" new aspire-starter --name VerifyApp 2>&1
) || {
    log_err "'aspire new' failed"
    echo "Contents of project directory:"
    find "$PROJECT_DIR" -maxdepth 3 -type f 2>/dev/null || true
    exit 1
}

# Verify the project was actually created
if [[ ! -d "$PROJECT_DIR/VerifyApp.AppHost" ]]; then
    log_err "Expected project directory 'VerifyApp.AppHost' not found after 'aspire new'"
    echo "Contents of project directory:"
    ls -la "$PROJECT_DIR"
    exit 1
fi
log_ok "'aspire new' created project successfully"

if [[ "$SKIP_BUILD" == "true" ]]; then
    log_warn "Skipping build step (--skip-build)"
else
    # Step 5: Restore the project with aspire restore
    log_step "Running 'aspire restore' on the created project..."
    (
        cd "$PROJECT_DIR"
        "$ASPIRE_BIN" restore --apphost "$PROJECT_DIR/VerifyApp.AppHost/VerifyApp.AppHost.csproj" 2>&1
    ) || {
        log_err "'aspire restore' failed"
        exit 1
    }
    log_ok "'aspire restore' succeeded"

    # Step 6: Build the project with dotnet build
    log_step "Running 'dotnet build' on the created project..."
    (
        cd "$PROJECT_DIR"
        dotnet build VerifyApp.AppHost/VerifyApp.AppHost.csproj 2>&1
    ) || {
        log_err "'dotnet build' failed"
        exit 1
    }
    log_ok "'dotnet build' succeeded"
fi

echo ""
echo "=========================================="
echo -e "  ${GREEN}All verification checks passed!${RESET}"
echo "=========================================="
echo ""
