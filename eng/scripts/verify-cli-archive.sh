#!/usr/bin/env bash

# verify-cli-archive.sh - Verify that a signed Aspire CLI archive produces a working binary.
#
# Usage: ./verify-cli-archive.sh <archive-path>
#
# This script:
#   1. Cleans ~/.aspire to ensure no stale state
#   2. Extracts the CLI archive to a temp location
#   3. Runs 'aspire --version' to validate the binary executes
#   4. Runs 'aspire new aspire-starter' to test bundle self-extraction + project creation
#   5. Cleans up temp directories

set -euo pipefail

readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly CYAN='\033[0;36m'
readonly RESET='\033[0m'

ARCHIVE_PATH=""

log_step() { echo -e "${CYAN}▶ $1${RESET}"; }
log_ok()   { echo -e "${GREEN}✅ $1${RESET}"; }
log_err()  { echo -e "${RED}❌ $1${RESET}"; }

show_help() {
    cat << 'EOF'
Aspire CLI Archive Verification Script

USAGE:
    verify-cli-archive.sh <archive-path>

ARGUMENTS:
    <archive-path>    Path to the CLI archive (.tar.gz or .zip)

OPTIONS:
    -h, --help        Show this help message

DESCRIPTION:
    Verifies that a signed Aspire CLI archive produces a working binary by:
    1. Extracting the archive
    2. Running 'aspire --version'
    3. Creating a new project with 'aspire new'
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
    echo "Usage: verify-cli-archive.sh <archive-path>" >&2
    exit 1
fi

if [[ ! -f "$ARCHIVE_PATH" ]]; then
    log_err "Archive not found: $ARCHIVE_PATH"
    exit 1
fi

echo ""
echo "=========================================="
echo "  Aspire CLI Archive Verification"
echo "=========================================="
echo "  Archive: $ARCHIVE_PATH"
echo "=========================================="
echo ""

# Suppress interactive prompts and telemetry
export ASPIRE_CLI_TELEMETRY_OPTOUT=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false

VERIFY_TMPDIR="$(mktemp -d)"

# Step 1: Back up and clean ~/.aspire
log_step "Cleaning ~/.aspire state..."
ASPIRE_BACKUP=""
if [[ -d "$HOME/.aspire" ]]; then
    ASPIRE_BACKUP="${VERIFY_TMPDIR}/aspire-backup/.aspire"
    mkdir -p "${VERIFY_TMPDIR}/aspire-backup"
    mv "$HOME/.aspire" "${ASPIRE_BACKUP}"
    log_step "Backed up existing ~/.aspire to ${ASPIRE_BACKUP}"
fi
log_ok "Clean ~/.aspire state"

# Step 2: Extract the archive
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
# This exercises bundle self-extraction and aspire-managed (template search + download + scaffolding)
PROJECT_DIR="${VERIFY_TMPDIR}/VerifyApp"
mkdir -p "$PROJECT_DIR"

log_step "Running 'aspire new aspire-starter --name VerifyApp --output $PROJECT_DIR'..."
"$ASPIRE_BIN" new aspire-starter --name VerifyApp --output "$PROJECT_DIR" --non-interactive --nologo 2>&1 || {
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

echo ""
echo "=========================================="
echo -e "  ${GREEN}All verification checks passed!${RESET}"
echo "=========================================="
echo ""
