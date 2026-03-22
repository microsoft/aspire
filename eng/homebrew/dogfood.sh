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

uninstall() {
  echo "Uninstalling dogfooded Aspire CLI..."

  # Determine which cask is installed
  for caskName in "aspire" "aspire@prerelease"; do
    if brew list --cask "$TAP_NAME/$caskName" &>/dev/null; then
      echo "  Uninstalling $TAP_NAME/$caskName..."
      brew uninstall --cask "$TAP_NAME/$caskName"
      echo "  Uninstalled."
    fi
  done

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
  for candidate in "$SCRIPT_DIR/aspire.rb" "$SCRIPT_DIR/aspire@prerelease.rb"; do
    if [[ -f "$candidate" ]]; then
      CASK_FILE="$candidate"
      break
    fi
  done

  if [[ -z "$CASK_FILE" ]]; then
    echo "Error: No cask file found in $SCRIPT_DIR"
    echo "Expected aspire.rb or aspire@prerelease.rb"
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

echo "Aspire CLI Homebrew Dogfood Installer"
echo "======================================"
echo "  Cask file: $CASK_FILE"
echo "  Cask name: $CASK_NAME"
echo ""

# Check for conflicts with official installs
for check in "aspire" "aspire@prerelease"; do
  if brew list --cask "$check" &>/dev/null; then
    echo "Error: '$check' is already installed from the official Homebrew tap."
    echo "Uninstall it first with: brew uninstall --cask $check"
    exit 1
  fi
done

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
  # Uninstall any casks from the old tap first
  for old in "aspire" "aspire@prerelease"; do
    if brew list --cask "$TAP_NAME/$old" &>/dev/null; then
      brew uninstall --cask "$TAP_NAME/$old" || true
    fi
  done
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
