#!/usr/bin/env bash

# verify-cli-archive.sh - Verify that an Aspire CLI flat-layout archive is valid and functional.
#
# Usage: ./verify-cli-archive.sh <archive-path>
#
# This script:
#   1. Validates archive compressed size (min 30 MB)
#   2. Extracts the CLI archive to a temp location
#   3. Validates flat layout structure (aspire + managed/ + dcp/)
#   4. Checks the binary is NativeAOT (file signature)
#   5. Verifies no bundle.tar.gz in archive (flat binary should NOT have embedded bundle)
#   6. Runs 'aspire --version' to validate the binary executes
#   7. Runs 'aspire new aspire-starter' to test full layout functionality
#   8. Cleans up temp directories

set -euo pipefail

readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly CYAN='\033[0;36m'
readonly RESET='\033[0m'

# Minimum compressed archive size in bytes (30 MB) — catches empty managed/ or dcp/
readonly MIN_ARCHIVE_SIZE=31457280

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
    Verifies that an Aspire CLI flat-layout archive is valid by:
    1. Checking archive compressed size (min 30 MB)
    2. Validating flat layout structure (aspire + managed/ + dcp/)
    3. Checking binary is NativeAOT (file signature)
    4. Verifying no embedded bundle in archive
    5. Running 'aspire --version' (LayoutDiscovery finds sibling dirs)
    6. Creating a new project with 'aspire new'
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

# Step 1: Check archive compressed size
log_step "Checking archive compressed size..."
ARCHIVE_SIZE=$(stat -f%z "$ARCHIVE_PATH" 2>/dev/null || stat -c%s "$ARCHIVE_PATH")
ARCHIVE_SIZE_MB=$(awk "BEGIN{printf \"%.1f\", $ARCHIVE_SIZE / 1048576}")
if [[ "$ARCHIVE_SIZE" -lt "$MIN_ARCHIVE_SIZE" ]]; then
    MIN_SIZE_MB=$(awk "BEGIN{printf \"%.0f\", $MIN_ARCHIVE_SIZE / 1048576}")
    log_err "Archive is too small (${ARCHIVE_SIZE_MB} MB, minimum ${MIN_SIZE_MB} MB). Layout may be incomplete."
    exit 1
fi
log_ok "Archive size check passed (${ARCHIVE_SIZE_MB} MB)"

VERIFY_TMPDIR="$(mktemp -d)"

# Step 2: Back up and clean ~/.aspire
log_step "Cleaning ~/.aspire state..."
ASPIRE_BACKUP=""
if [[ -d "$HOME/.aspire" ]]; then
    ASPIRE_BACKUP="${VERIFY_TMPDIR}/aspire-backup/.aspire"
    mkdir -p "${VERIFY_TMPDIR}/aspire-backup"
    mv "$HOME/.aspire" "${ASPIRE_BACKUP}"
    log_step "Backed up existing ~/.aspire to ${ASPIRE_BACKUP}"
fi
log_ok "Clean ~/.aspire state"

# Step 3: Extract the archive
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

# Step 4: Validate flat layout structure
log_step "Validating flat layout structure..."

# Find the aspire binary
ASPIRE_BIN=""
if [[ -f "$EXTRACT_DIR/aspire" ]]; then
    ASPIRE_BIN="$EXTRACT_DIR/aspire"
elif [[ -f "$EXTRACT_DIR/aspire.exe" ]]; then
    ASPIRE_BIN="$EXTRACT_DIR/aspire.exe"
else
    log_err "Could not find 'aspire' binary at archive root. Contents:"
    ls -la "$EXTRACT_DIR"
    exit 1
fi
chmod +x "$ASPIRE_BIN"
log_ok "Found aspire binary: $(basename "$ASPIRE_BIN")"

# Check managed/ directory
if [[ ! -d "$EXTRACT_DIR/managed" ]]; then
    log_err "managed/ directory not found in archive. Contents:"
    ls -la "$EXTRACT_DIR"
    exit 1
fi
log_ok "Found managed/ directory"

# Check managed/aspire-managed executable
MANAGED_BIN=""
if [[ -f "$EXTRACT_DIR/managed/aspire-managed" ]]; then
    MANAGED_BIN="$EXTRACT_DIR/managed/aspire-managed"
elif [[ -f "$EXTRACT_DIR/managed/aspire-managed.exe" ]]; then
    MANAGED_BIN="$EXTRACT_DIR/managed/aspire-managed.exe"
else
    log_err "aspire-managed executable not found in managed/. Contents:"
    ls -la "$EXTRACT_DIR/managed/"
    exit 1
fi
chmod +x "$MANAGED_BIN"
log_ok "Found managed/$(basename "$MANAGED_BIN")"

# Check dcp/ directory
if [[ ! -d "$EXTRACT_DIR/dcp" ]]; then
    log_err "dcp/ directory not found in archive. Contents:"
    ls -la "$EXTRACT_DIR"
    exit 1
fi

# Check dcp/ contains at least one file
DCP_FILE_COUNT=$(find "$EXTRACT_DIR/dcp" -type f | wc -l | tr -d ' ')
if [[ "$DCP_FILE_COUNT" -eq 0 ]]; then
    log_err "dcp/ directory is empty"
    exit 1
fi
log_ok "Found dcp/ directory ($DCP_FILE_COUNT files)"

# Step 5: Check binary is NativeAOT (file signature)
log_step "Checking binary file signature..."
FILE_OUTPUT=$(file -b "$ASPIRE_BIN")
if echo "$FILE_OUTPUT" | grep -qE 'ELF 64-bit|Mach-O 64-bit'; then
    log_ok "Binary is NativeAOT: $FILE_OUTPUT"
elif echo "$FILE_OUTPUT" | grep -qE 'PE32\+'; then
    log_ok "Binary is NativeAOT PE: $FILE_OUTPUT"
else
    log_err "Binary does not appear to be NativeAOT. file output: $FILE_OUTPUT"
    exit 1
fi

# Step 6: Verify no bundle.tar.gz in archive (flat binary should NOT have embedded bundle)
log_step "Checking archive does not contain bundle.tar.gz..."
if [[ "$ARCHIVE_PATH" == *.tar.gz ]]; then
    if tar -tzf "$ARCHIVE_PATH" | grep -q 'bundle\.tar\.gz'; then
        log_err "Archive contains bundle.tar.gz — this should be a flat layout archive without embedded bundle"
        exit 1
    fi
elif [[ "$ARCHIVE_PATH" == *.zip ]]; then
    if unzip -l "$ARCHIVE_PATH" | grep -q 'bundle\.tar\.gz'; then
        log_err "Archive contains bundle.tar.gz — this should be a flat layout archive without embedded bundle"
        exit 1
    fi
fi
log_ok "No bundle.tar.gz in archive (correct for flat layout)"

# Step 7: Verify aspire --version
# Run directly from extraction dir — LayoutDiscovery finds sibling managed/ and dcp/
log_step "Running 'aspire --version'..."
VERSION_OUTPUT=$("$ASPIRE_BIN" --version 2>&1) || {
    log_err "'aspire --version' failed with exit code $?"
    echo "Output: $VERSION_OUTPUT"
    exit 1
}
echo "  Version: $VERSION_OUTPUT"
log_ok "'aspire --version' succeeded"

# Step 8: Create a new project with aspire new
# This exercises aspire-managed (template search + download + scaffolding) via the flat layout
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
