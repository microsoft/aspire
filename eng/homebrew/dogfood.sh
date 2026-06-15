#!/usr/bin/env bash
set -euo pipefail

# Installs the Aspire CLI Homebrew cask from a local artifact directory.
# This script is intended for dogfooding builds before they are published to Homebrew/homebrew-cask.
#
# Usage:
#   ./dogfood.sh                             # Auto-detects cask file and adjacent archives
#   ./dogfood.sh --archive-root ../artifacts # Installs from downloaded native archive artifacts
#   ./dogfood.sh aspire.rb                   # Explicit cask file path
#   ./dogfood.sh --uninstall                 # Uninstall a previously dogfooded cask

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TAP_NAME="local/aspire-dogfood"

usage() {
  cat <<EOF
Usage: $(basename "$0") [OPTIONS] [CASK_FILE]

Installs the Aspire CLI from a local Homebrew cask file for dogfooding.

Arguments:
  CASK_FILE               Path to the .rb cask file (default: auto-detect in script directory)

Options:
  --archive-root PATH     Root directory containing downloaded aspire-cli-osx-* archives
  --uninstall             Uninstall a previously dogfooded cask and remove the local tap
  --help                  Show this help message

Examples:
  $(basename "$0")                                   # Auto-detect cask and adjacent archives
  $(basename "$0") --archive-root ../native-archives # Install from downloaded archive artifacts
  $(basename "$0") ./aspire.rb                       # Install from specific cask file
  $(basename "$0") --uninstall                       # Clean up dogfood install
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

read_cask_version() {
  local caskFile="$1"
  awk -F'"' '/^[[:space:]]*version[[:space:]]+"/ { print $2; exit }' "$caskFile"
}

find_archive_if_present() {
  local archiveRoot="$1"
  local archiveName="$2"

  find "$archiveRoot" -type f -name "$archiveName" -print -quit 2>/dev/null || true
}

find_archive() {
  local archiveRoot="$1"
  local archiveName="$2"
  local matches=()
  local match

  while IFS= read -r match; do
    matches+=("$match")
  done < <(find "$archiveRoot" -type f -name "$archiveName" -print | LC_ALL=C sort)

  if [[ "${#matches[@]}" -eq 0 ]]; then
    echo "Error: Could not find $archiveName under $archiveRoot" >&2
    exit 1
  fi

  if [[ "${#matches[@]}" -gt 1 ]]; then
    echo "Error: Found multiple $archiveName archives under $archiveRoot:" >&2
    printf '  %s\n' "${matches[@]}" >&2
    exit 1
  fi

  printf '%s' "${matches[0]}"
}

detect_archive_root() {
  local version="$1"
  local candidate

  for candidate in "$SCRIPT_DIR" "$SCRIPT_DIR/.."; do
    if [[ -f "$(find_archive_if_present "$candidate" "aspire-cli-osx-arm64-$version.tar.gz")" &&
          -f "$(find_archive_if_present "$candidate" "aspire-cli-osx-x64-$version.tar.gz")" ]]; then
      printf '%s' "$(cd "$candidate" && pwd)"
      return
    fi
  done
}

rewrite_cask_for_local_archives() {
  local caskFile="$1"
  local archiveRoot="$2"
  local localArchiveDir="$3"
  local version="$4"

  local armArchive
  local x64Archive
  armArchive="$(find_archive "$archiveRoot" "aspire-cli-osx-arm64-$version.tar.gz")"
  x64Archive="$(find_archive "$archiveRoot" "aspire-cli-osx-x64-$version.tar.gz")"

  mkdir -p "$localArchiveDir"
  cp "$armArchive" "$localArchiveDir/aspire-cli-osx-arm64-$version.tar.gz"
  cp "$x64Archive" "$localArchiveDir/aspire-cli-osx-x64-$version.tar.gz"

  local localUrlPrefix="file://$localArchiveDir"

  ruby - "$caskFile" "$localUrlPrefix" <<'RUBY'
cask_path = ARGV[0]
local_url_prefix = ARGV[1]
lines = File.readlines(cask_path, chomp: true)
rewritten = []

# The cask has a 2-line url stanza:
#   url "https://github.com/microsoft/aspire/...",
#       verified: "github.com/microsoft/aspire/"
# For dogfood install, replace the url with a file:// path and drop the
# trailing verified line. file:// URLs are exempt from `audit_missing_verified`
# (brew's `file_url?` short-circuits the check), and dogfood.sh only runs
# `brew install` — not `brew audit` — so audit_no_match never fires either.
i = 0
while i < lines.length
  line = lines[i]
  stripped = line.strip
  if stripped.start_with?('url "https://github.com/microsoft/aspire/releases/download/')
    rewritten << "  url \"#{local_url_prefix}/aspire-cli-osx-\#{arch}-\#{version}.tar.gz\""
    i += 1
    if i < lines.length && lines[i].strip.start_with?('verified:')
      i += 1
    end
    next
  end

  rewritten << line
  i += 1
end

File.write(cask_path, rewritten.join("\n") + "\n")
RUBY
}

CASK_FILE=""
ARCHIVE_ROOT=""
UNINSTALL=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --archive-root) ARCHIVE_ROOT="$2"; shift 2 ;;
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

caskVersion="$(read_cask_version "$CASK_FILE")"
if [[ -z "$caskVersion" ]]; then
  echo "Error: Could not read cask version from $CASK_FILE"
  exit 1
fi

if [[ -z "$ARCHIVE_ROOT" ]]; then
  ARCHIVE_ROOT="$(detect_archive_root "$caskVersion")"
fi

if [[ -n "$ARCHIVE_ROOT" ]]; then
  ARCHIVE_ROOT="$(cd "$ARCHIVE_ROOT" && pwd)"
  echo "Using local native archive artifacts from: $ARCHIVE_ROOT"
  rewrite_cask_for_local_archives "$tapCaskDir/$CASK_FILENAME" "$ARCHIVE_ROOT" "$tapRoot/LocalArtifacts" "$caskVersion"
else
  echo "No local native archive artifacts found; installing with URLs from the cask."
fi

# Install
echo ""
echo "Installing $CASK_NAME from local tap..."
# Disable auto-update during install — auto-update can re-index the tap before
# the cask file is picked up, causing a "cask unavailable" error.
HOMEBREW_NO_AUTO_UPDATE=1 brew install --cask "$TAP_NAME/$CASK_NAME"

caskRoot="$(brew --prefix)/Caskroom/$CASK_NAME/$caskVersion"
if [[ -d "$caskRoot" ]] && xattr -p com.apple.quarantine "$caskRoot/aspire" &>/dev/null; then
  # Local PR artifacts are unsigned ad-hoc signed binaries. Homebrew correctly
  # quarantines cask downloads, but macOS kills these dogfood binaries before
  # the CLI can print its version. Remove quarantine only from this local
  # dogfood install after Homebrew has finished installing it.
  xattr -dr com.apple.quarantine "$caskRoot"
fi

# Verify
echo ""
if ! command -v aspire &>/dev/null; then
  echo "Error: aspire command not found in PATH after install." >&2
  echo "You may need to restart your shell or add the install location to your PATH." >&2
  exit 1
fi

# Shadow check — Homebrew symlinks cask binaries into $(brew --prefix)/bin/, so
# the freshly-installed aspire must resolve under the brew prefix. An older
# aspire earlier on PATH (e.g. a dotnet-tool install, or a script install under
# ~/.aspire/bin) would otherwise silently shadow the cask and let this script
# report "Installed successfully!" against the wrong binary, defeating the
# whole point of the dogfood. The WinGet dogfood (eng/winget/dogfood.ps1) has
# the equivalent check via Find-AspireBinaryOnPath -ExpectedVersion.
# Skipped under ASPIRE_TEST_MODE because the test mock brew puts its fake
# aspire alongside the mock brew binary, not under $(brew --prefix)/bin/.
if [[ "${ASPIRE_TEST_MODE:-}" != "true" ]]; then
  aspirePath="$(command -v aspire)"
  brewPrefix="$(brew --prefix)"
  if [[ "$aspirePath" != "$brewPrefix"/* ]]; then
    echo "Error: 'aspire' resolved to '$aspirePath', not under the Homebrew prefix '$brewPrefix'." >&2
    echo "An older aspire earlier on PATH is shadowing the Homebrew cask install." >&2
    echo "Either reorder PATH so '$brewPrefix/bin' wins, or uninstall the older copy." >&2
    exit 1
  fi
fi

echo "Installed successfully!"
echo "  Path:    $(command -v aspire)"
if ! aspireVersion="$(aspire --version 2>&1)"; then
  echo "Error: aspire --version failed after install:" >&2
  echo "$aspireVersion" >&2
  exit 1
fi
echo "  Version: $aspireVersion"

echo ""
echo "To uninstall: $(basename "$0") --uninstall"
