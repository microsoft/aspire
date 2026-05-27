#!/usr/bin/env bash
set -euo pipefail

# Prepares the Homebrew cask artifact for Aspire CLI builds.
# This script intentionally does not upload artifacts; each CI system owns that step.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  cat <<EOF
Usage: $(basename "$0") --channel CHANNEL --output-dir DIR [OPTIONS]

Required:
  --channel CHANNEL            Installer channel: stable or prerelease
  --output-dir DIR             Directory where the cask artifact is written

Optional:
  --version VERSION            Installer version in the cask and archive filename
  --archive-root PATH          Root directory containing locally built CLI archives
  --validation-mode MODE       LiveRelease or LiveArchives (default: LiveArchives)
  --help                       Show this help message
EOF
  exit 1
}

VERSION=""
CHANNEL=""
ARCHIVE_ROOT=""
OUTPUT_DIR=""
# LiveArchives is the default because prepare time means "the cask URL doesn't
# resolve yet — the GH release for v#{version} hasn't been published". Callers
# that want the full upstream-CI-equivalent audit + brew install/uninstall must
# do so against an already-published cask via validate-cask-artifact.sh
# directly, as HomebrewValidateJob does in release-publish-nuget.yml.
VALIDATION_MODE="LiveArchives"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --channel) CHANNEL="$2"; shift 2 ;;
    --archive-root) ARCHIVE_ROOT="$2"; shift 2 ;;
    --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
    --validation-mode) VALIDATION_MODE="$2"; shift 2 ;;
    --help) usage ;;
    *) echo "Unknown option: $1" >&2; usage ;;
  esac
done

case "$CHANNEL" in
  stable|prerelease) ;;
  "") echo "Error: --channel is required." >&2; usage ;;
  *) echo "Error: --channel must be 'stable' or 'prerelease'." >&2; exit 1 ;;
esac

shopt -s nocasematch
case "$VALIDATION_MODE" in
  liverelease)  VALIDATION_MODE="LiveRelease" ;;
  livearchives) VALIDATION_MODE="LiveArchives" ;;
  *) echo "Error: --validation-mode must be LiveRelease or LiveArchives." >&2; exit 1 ;;
esac
shopt -u nocasematch

if [[ -z "$OUTPUT_DIR" ]]; then
  echo "Error: --output-dir is required." >&2
  usage
fi

infer_version_from_archive() {
  local rid="$1"
  local prefix="aspire-cli-$rid-"
  local suffix=".tar.gz"
  local matches=()
  local archive_path

  if [[ -z "$ARCHIVE_ROOT" || ! -d "$ARCHIVE_ROOT" ]]; then
    echo "Error: --version is required when --archive-root is not specified." >&2
    exit 1
  fi

  while IFS= read -r archive_path; do
    matches+=("$archive_path")
  done < <(find "$ARCHIVE_ROOT" -type f -name "$prefix*$suffix" -print | LC_ALL=C sort)

  if [[ "${#matches[@]}" -eq 0 ]]; then
    echo "Error: Could not find archive '$prefix*$suffix' under '$ARCHIVE_ROOT' to infer the Aspire CLI version." >&2
    exit 1
  fi

  if [[ "${#matches[@]}" -gt 1 ]]; then
    echo "Error: Found multiple archives matching '$prefix*$suffix' under '$ARCHIVE_ROOT':" >&2
    printf '  %s\n' "${matches[@]}" >&2
    exit 1
  fi

  local filename="${matches[0]##*/}"
  filename="${filename#"$prefix"}"
  printf '%s' "${filename%"$suffix"}"
}

if [[ -z "$VERSION" ]]; then
  VERSION="$(infer_version_from_archive "osx-arm64")"
fi

mkdir -p "$OUTPUT_DIR"
OUTPUT_FILE="$OUTPUT_DIR/aspire.rb"

echo "Preparing Homebrew cask"
echo "  Version: $VERSION"
echo "  Channel: $CHANNEL"
echo "  Output dir: $OUTPUT_DIR"
echo "  Validation mode: $VALIDATION_MODE"

args=(
  --version "$VERSION"
  --output "$OUTPUT_FILE"
)

if [[ -n "$ARCHIVE_ROOT" ]]; then
  args+=(--archive-root "$ARCHIVE_ROOT")
fi

# In LiveRelease mode, generate-cask.sh fetches the published archive bytes
# from the github.com release URL over the network to compute SHA256. In any
# other mode, we either compute the SHA from a local archive (when
# --archive-root is set) or skip the SHA fetch and emit placeholders.
if [[ "$VALIDATION_MODE" != "LiveRelease" ]]; then
  args+=(--skip-url-validation)
fi

"$SCRIPT_DIR/generate-cask.sh" "${args[@]}"

echo ""
echo "Generated cask:"
cat "$OUTPUT_FILE"

validation_args=(
  --cask-file "$OUTPUT_FILE"
  --channel "$CHANNEL"
  --validation-mode "$VALIDATION_MODE"
)

"$SCRIPT_DIR/validate-cask-artifact.sh" "${validation_args[@]}"

cp "$SCRIPT_DIR/dogfood.sh" "$OUTPUT_DIR/dogfood.sh"
chmod +x "$OUTPUT_DIR/dogfood.sh"

echo "Homebrew cask artifact prepared at: $OUTPUT_DIR"
