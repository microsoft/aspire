#!/usr/bin/env bash

# verify-cli-tool-nupkg.sh - Verify that a CLI dotnet-tool nupkg is valid.
#
# Usage: ./verify-cli-tool-nupkg.sh <packages-dir> <rid>
#
# This script:
#   1. Finds the RID-specific tool nupkg (Aspire.Cli.<rid>.*.nupkg)
#   2. Validates the nupkg size is above a minimum threshold
#   3. Extracts the package and verifies the aspire binary is present
#   4. On Unix: uses `file` command to verify the binary file type
#   5. Checks the primary pointer package (Aspire.Cli.*.nupkg) exists

set -euo pipefail

readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly CYAN='\033[0;36m'
readonly RESET='\033[0m'

log_step() { echo -e "${CYAN}▶ $1${RESET}"; }
log_ok()   { echo -e "${GREEN}✅ $1${RESET}"; }
log_err()  { echo -e "${RED}❌ $1${RESET}"; }

# Minimum expected nupkg size in bytes (5 MB — NativeAOT binary should be large)
readonly MIN_NUPKG_SIZE=5000000

show_help() {
    cat << 'EOF'
Aspire CLI Tool Nupkg Verification Script

USAGE:
    verify-cli-tool-nupkg.sh <packages-dir> <rid>

ARGUMENTS:
    <packages-dir>    Path to the directory containing nupkg files
    <rid>             Runtime identifier (e.g., linux-x64, osx-arm64)

DESCRIPTION:
    Verifies that the CLI dotnet-tool nupkg is valid by:
    1. Checking the RID-specific nupkg exists and meets size threshold
    2. Extracting and verifying the aspire binary is present
    3. Validating the binary file type matches the expected RID
    4. Verifying the primary pointer package exists
EOF
    exit 0
}

PACKAGES_DIR=""
RID=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        -h|--help) show_help ;;
        -*)
            echo "Unknown option: $1" >&2
            exit 1
            ;;
        *)
            if [[ -z "$PACKAGES_DIR" ]]; then
                PACKAGES_DIR="$1"
            elif [[ -z "$RID" ]]; then
                RID="$1"
            else
                echo "Unexpected argument: $1" >&2
                exit 1
            fi
            shift
            ;;
    esac
done

if [[ -z "$PACKAGES_DIR" ]] || [[ -z "$RID" ]]; then
    echo "Error: packages-dir and rid are required." >&2
    echo "Usage: verify-cli-tool-nupkg.sh <packages-dir> <rid>" >&2
    exit 1
fi

echo ""
echo "=========================================="
echo "  Aspire CLI Tool Nupkg Verification"
echo "=========================================="
echo "  Packages dir: $PACKAGES_DIR"
echo "  RID:          $RID"
echo "=========================================="
echo ""

# Step 1: Find the RID-specific nupkg
# The nupkg name format is: Aspire.Cli.<rid>.<version>.nupkg
# (dots in the RID are preserved, e.g., Aspire.Cli.linux-x64.10.0.0.nupkg)
log_step "Looking for RID-specific nupkg matching Aspire.Cli.${RID}.*.nupkg ..."
RID_NUPKG=$(find "$PACKAGES_DIR" -name "Aspire.Cli.${RID}.*.nupkg" -not -name "*.symbols.*" -type f 2>/dev/null | head -1)

if [[ -z "$RID_NUPKG" ]]; then
    log_err "No RID-specific nupkg found for ${RID} in ${PACKAGES_DIR}"
    echo "Contents of packages directory:"
    find "$PACKAGES_DIR" -name "*.nupkg" -type f 2>/dev/null || echo "  (empty)"
    exit 1
fi

log_ok "Found: $(basename "$RID_NUPKG")"

# Step 2: Check nupkg size
NUPKG_SIZE=$(stat -f%z "$RID_NUPKG" 2>/dev/null || stat --format=%s "$RID_NUPKG" 2>/dev/null)
log_step "Checking nupkg size: $(( NUPKG_SIZE / 1024 / 1024 )) MB ($NUPKG_SIZE bytes)"

if [[ "$NUPKG_SIZE" -lt "$MIN_NUPKG_SIZE" ]]; then
    log_err "Nupkg is too small ($NUPKG_SIZE bytes, minimum $MIN_NUPKG_SIZE bytes). The NativeAOT binary may be missing."
    exit 1
fi
log_ok "Size check passed ($(( NUPKG_SIZE / 1024 / 1024 )) MB)"

# Step 3: Extract and verify the binary
EXTRACT_DIR=$(mktemp -d)
trap "rm -rf '$EXTRACT_DIR'" EXIT

log_step "Extracting nupkg to verify contents..."
unzip -q "$RID_NUPKG" -d "$EXTRACT_DIR"

# The tool binary is at tools/net10.0/<rid>/aspire (or aspire.exe on Windows)
BINARY_NAME="aspire"
if [[ "$RID" == win-* ]]; then
    BINARY_NAME="aspire.exe"
fi

TOOL_BINARY=$(find "$EXTRACT_DIR" -name "$BINARY_NAME" -type f 2>/dev/null | head -1)

if [[ -z "$TOOL_BINARY" ]]; then
    log_err "Could not find '$BINARY_NAME' inside the nupkg"
    echo "Nupkg contents:"
    find "$EXTRACT_DIR" -type f | head -20
    exit 1
fi
log_ok "Found binary: ${TOOL_BINARY#$EXTRACT_DIR/}"

# Step 4: Verify binary file type on Unix
if [[ "$RID" != win-* ]] && command -v file &>/dev/null; then
    log_step "Checking binary file type..."
    FILE_OUTPUT=$(file -b "$TOOL_BINARY")
    echo "  file output: $FILE_OUTPUT"

    # Verify it's a native executable (not a .NET managed assembly)
    case "$RID" in
        linux-x64)
            if ! echo "$FILE_OUTPUT" | grep -qi "ELF.*x86-64"; then
                log_err "Expected ELF x86-64 binary for $RID, got: $FILE_OUTPUT"
                exit 1
            fi
            ;;
        linux-arm64)
            if ! echo "$FILE_OUTPUT" | grep -qi "ELF.*aarch64\|ELF.*ARM aarch64"; then
                log_err "Expected ELF aarch64 binary for $RID, got: $FILE_OUTPUT"
                exit 1
            fi
            ;;
        linux-musl-x64)
            if ! echo "$FILE_OUTPUT" | grep -qi "ELF.*x86-64"; then
                log_err "Expected ELF x86-64 binary for $RID, got: $FILE_OUTPUT"
                exit 1
            fi
            ;;
        osx-x64)
            if ! echo "$FILE_OUTPUT" | grep -qi "Mach-O.*x86_64"; then
                log_err "Expected Mach-O x86_64 binary for $RID, got: $FILE_OUTPUT"
                exit 1
            fi
            ;;
        osx-arm64)
            if ! echo "$FILE_OUTPUT" | grep -qi "Mach-O.*arm64"; then
                log_err "Expected Mach-O arm64 binary for $RID, got: $FILE_OUTPUT"
                exit 1
            fi
            ;;
        *)
            echo "  (no file-type check configured for $RID, skipping)"
            ;;
    esac
    log_ok "Binary file type matches $RID"
fi

# Step 5: Find the primary pointer package
log_step "Looking for primary pointer package (Aspire.Cli.*.nupkg, not RID-specific)..."
PRIMARY_NUPKG=$(find "$PACKAGES_DIR" -name "Aspire.Cli.*.nupkg" -not -name "*.symbols.*" -not -name "Aspire.Cli.win-*" -not -name "Aspire.Cli.linux-*" -not -name "Aspire.Cli.osx-*" -type f 2>/dev/null | head -1)

if [[ -z "$PRIMARY_NUPKG" ]]; then
    log_err "No primary pointer package found (Aspire.Cli.*.nupkg without RID prefix)"
    echo "All Aspire.Cli nupkgs:"
    find "$PACKAGES_DIR" -name "Aspire.Cli*.nupkg" -type f 2>/dev/null || echo "  (none)"
    exit 1
fi
log_ok "Found primary: $(basename "$PRIMARY_NUPKG")"

echo ""
echo "=========================================="
echo -e "  ${GREEN}All tool nupkg checks passed!${RESET}"
echo "=========================================="
echo ""
