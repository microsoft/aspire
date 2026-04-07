#!/usr/bin/env bash
set -euo pipefail

# Installs the Aspire CLI Homebrew cask from a local artifact directory.
# This script is intended for dogfooding builds before they are published to Homebrew/homebrew-cask.
#
# Usage:
#   ./dogfood.sh                  # Auto-detects cask file in the same directory
#   ./dogfood.sh aspire.rb        # Explicit cask file path
#   ./dogfood.sh --uninstall      # Uninstall a previously dogfooded cask

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TAP_NAME="local/aspire-dogfood"

usage() {
  cat <<EOF
Usage: $(basename "$0") [OPTIONS] [CASK_FILE]

Installs the Aspire CLI from a local Homebrew cask file for dogfooding.

Arguments:
  CASK_FILE               Path to the .rb cask file (default: auto-detect in script directory)

Options:
  --uninstall             Uninstall a previously dogfooded cask and remove the local tap
  --help                  Show this help message

Examples:
  $(basename "$0")                         # Auto-detect and install
  $(basename "$0") ./aspire.rb             # Install from specific cask file
  $(basename "$0") --uninstall             # Clean up dogfood install
EOF
  exit 0
}

is_cask_installed() {
  local caskName="$1"

  brew list --cask --versions 2>/dev/null | awk '{print $1}' | grep -Fx -- "$caskName" >/dev/null
}

uninstall() {
  echo "Uninstalling dogfooded Aspire CLI..."

  if brew list --cask "$TAP_NAME/aspire" &>/dev/null; then
    echo "  Uninstalling $TAP_NAME/aspire..."
    brew uninstall --cask "$TAP_NAME/aspire"
    echo "  Uninstalled."
  fi

  if brew tap-info "$TAP_NAME" &>/dev/null; then
    echo "  Removing tap $TAP_NAME..."
    brew untap "$TAP_NAME"
    echo "  Removed."
  fi

  echo ""
  echo "Done. Dogfood install removed."
  exit 0
}

CASK_FILE=""
UNINSTALL=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall)  UNINSTALL=true; shift ;;
    --help)       usage ;;
    -*)           echo "Unknown option: $1"; usage ;;
    *)            CASK_FILE="$1"; shift ;;
  esac
done

if [[ "$UNINSTALL" == true ]]; then
  uninstall
fi

# Auto-detect cask file if not specified
if [[ -z "$CASK_FILE" ]]; then
  candidate="$SCRIPT_DIR/aspire.rb"
  if [[ -f "$candidate" ]]; then
    CASK_FILE="$candidate"
  fi

  if [[ -z "$CASK_FILE" ]]; then
    echo "Error: No cask file found in $SCRIPT_DIR"
    echo "Expected aspire.rb"
    exit 1
  fi
fi

if [[ ! -f "$CASK_FILE" ]]; then
  echo "Error: Cask file not found: $CASK_FILE"
  exit 1
fi

CASK_FILE="$(cd "$(dirname "$CASK_FILE")" && pwd)/$(basename "$CASK_FILE")"
CASK_FILENAME="$(basename "$CASK_FILE")"
CASK_NAME="${CASK_FILENAME%.rb}"

if [[ "$CASK_NAME" != "aspire" ]]; then
  echo "Error: Only the stable Homebrew cask is supported."
  echo "Expected aspire.rb"
  exit 1
fi

echo "Aspire CLI Homebrew Dogfood Installer"
echo "======================================"
echo "  Cask file: $CASK_FILE"
echo "  Cask name: $CASK_NAME"
echo ""

if is_cask_installed "aspire"; then
  echo "Error: 'aspire' is already installed."
  echo "If this is a previous dogfood install, remove it with: $(basename "$0") --uninstall"
  echo "Otherwise uninstall it first with: brew uninstall --cask aspire"
  exit 1
fi

# Check for leftover local/aspire tap from pipeline testing
if brew tap-info "local/aspire" &>/dev/null 2>&1; then
  echo "Error: A 'local/aspire' tap already exists (likely from a pipeline test run)."
  echo "Remove it first with: brew untap local/aspire"
  exit 1
fi

if brew tap-info "local/aspire-test" &>/dev/null 2>&1; then
  echo "Error: A 'local/aspire-test' tap already exists (likely from a pipeline test run)."
  echo "Remove it first with: brew untap local/aspire-test"
  exit 1
fi

# Clean up any previous dogfood tap
if brew tap-info "$TAP_NAME" &>/dev/null 2>&1; then
  echo "Removing previous dogfood tap..."
  if is_cask_installed "aspire"; then
    brew uninstall --cask "aspire" || true
  fi
  brew untap "$TAP_NAME"
fi

# Set up local tap
echo "Setting up local tap ($TAP_NAME)..."
brew tap-new --no-git "$TAP_NAME"
tapOrg="${TAP_NAME%%/*}"
tapRepo="${TAP_NAME##*/}"
tapRoot="$(brew --repository)/Library/Taps/$tapOrg/homebrew-$tapRepo"
tapCaskDir="$tapRoot/Casks"
mkdir -p "$tapCaskDir"
cp "$CASK_FILE" "$tapCaskDir/$CASK_FILENAME"

# Install
echo ""
echo "Installing $CASK_NAME from local tap..."
# Disable auto-update during install — auto-update can re-index the tap before
# the cask file is picked up, causing a "cask unavailable" error.
HOMEBREW_NO_AUTO_UPDATE=1 brew install --cask "$TAP_NAME/$CASK_NAME"

# Verify
echo ""
if command -v aspire &>/dev/null; then
  echo "Installed successfully!"
  echo "  Path:    $(command -v aspire)"
  aspireVersion="$(aspire --version 2>&1)" || true
  echo "  Version: $aspireVersion"
else
  echo "Warning: aspire command not found in PATH after install."
  echo "You may need to restart your shell or add the install location to your PATH."
fi

echo ""
echo "To uninstall: $(basename "$0") --uninstall"
