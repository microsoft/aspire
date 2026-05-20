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
  --artifact-version VERSION   Version segment used in the ci.dot.net artifact path
  --archive-root PATH          Root directory containing locally built CLI archives
  --validation-mode MODE       Full, Offline, or GenerateOnly (default: Full)
  --help                       Show this help message
EOF
  exit 1
}

VERSION=""
ARTIFACT_VERSION=""
CHANNEL=""
ARCHIVE_ROOT=""
OUTPUT_DIR=""
VALIDATION_MODE="Full"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --artifact-version) ARTIFACT_VERSION="$2"; shift 2 ;;
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

case "$VALIDATION_MODE" in
  Full|Offline|GenerateOnly) ;;
  full) VALIDATION_MODE="Full" ;;
  offline) VALIDATION_MODE="Offline" ;;
  generateonly|generate-only) VALIDATION_MODE="GenerateOnly" ;;
  *) echo "Error: --validation-mode must be Full, Offline, or GenerateOnly." >&2; exit 1 ;;
esac

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

if [[ -z "$ARTIFACT_VERSION" ]]; then
  ARTIFACT_VERSION="$VERSION"
fi

mkdir -p "$OUTPUT_DIR"
OUTPUT_FILE="$OUTPUT_DIR/aspire.rb"

echo "Preparing Homebrew cask"
echo "  Version: $VERSION"
echo "  Channel: $CHANNEL"
echo "  Artifact version: $ARTIFACT_VERSION"
echo "  Output dir: $OUTPUT_DIR"
echo "  Validation mode: $VALIDATION_MODE"

args=(
  --version "$VERSION"
  --artifact-version "$ARTIFACT_VERSION"
  --output "$OUTPUT_FILE"
)

if [[ -n "$ARCHIVE_ROOT" ]]; then
  args+=(--archive-root "$ARCHIVE_ROOT")
fi

if [[ "$VALIDATION_MODE" != "Full" ]]; then
  args+=(--skip-url-validation)
fi

"$SCRIPT_DIR/generate-cask.sh" "${args[@]}"

echo ""
echo "Generated cask:"
cat "$OUTPUT_FILE"

if [[ "$VALIDATION_MODE" != "GenerateOnly" ]]; then
  cask_file="aspire.rb"
  cask_name="aspire"
  first_letter="${cask_name:0:1}"
  target_path="Casks/$first_letter/$cask_file"
  tap_name="local/aspire"

  cleanup() {
    brew untap "$tap_name" >/dev/null 2>&1 || true
  }

  if ! command -v brew >/dev/null 2>&1; then
    if [[ "$VALIDATION_MODE" == "Full" ]]; then
      echo "Error: brew is required for Full validation mode, but it was not found in PATH." >&2
      exit 1
    fi

    echo "Warning: brew was not found in PATH; skipping offline cask validation." >&2
  else
    tap_root="$(brew --repository)/Library/Taps/local/homebrew-aspire"
    trap cleanup EXIT

    echo "Validating Ruby syntax..."
    ruby -c "$OUTPUT_FILE"
    echo "Ruby syntax OK"

    brew tap-new --no-git "$tap_name"
    mkdir -p "$tap_root/Casks"
    cp "$OUTPUT_FILE" "$tap_root/Casks/$cask_file"
    tapped_cask_path="$tap_root/Casks/$cask_file"

    echo ""
    echo "Applying Homebrew style fixes in local tap..."
    brew style --fix "$tapped_cask_path"
    cp "$tapped_cask_path" "$OUTPUT_FILE"
    echo "Homebrew style OK"

    echo ""
    echo "Auditing cask via local tap..."

    audit_args=(--cask)
    is_new_cask=false

    if [[ "$VALIDATION_MODE" == "Full" ]]; then
      upstream_status_code="$(curl -sS -o /dev/null -w "%{http_code}" "https://api.github.com/repos/Homebrew/homebrew-cask/contents/$target_path")"
      if [[ "$upstream_status_code" == "404" ]]; then
        is_new_cask=true
        echo "Detected new upstream cask; running new-cask audit."
        audit_args+=(--new)
      elif [[ "$upstream_status_code" == "200" ]]; then
        echo "Detected existing upstream cask; running standard audit."
      else
        echo "Error: Could not determine whether $target_path exists upstream (HTTP $upstream_status_code)" >&2
        exit 1
      fi
      echo "Including --online audit (URLs are expected to be valid)."
      audit_args+=(--online)
    else
      echo "Skipping upstream cask check and online audit (URLs are not expected to be valid for this build)."
    fi

    audit_args+=("$tap_name/$cask_name")
    audit_command="brew audit ${audit_args[*]}"

    audit_code=0
    brew audit "${audit_args[@]}" || audit_code=$?

    if [[ "$audit_code" -ne 0 ]]; then
      echo "Error: brew audit failed" >&2
      exit "$audit_code"
    fi
    echo "brew audit passed"

    if [[ "$VALIDATION_MODE" == "Full" ]]; then
      echo "Testing cask install/uninstall: $OUTPUT_FILE"

      echo "Verifying aspire is not already installed..."
      if command -v aspire &>/dev/null; then
        echo "Error: aspire command is already available before install - test environment is not clean" >&2
        exit 1
      fi
      echo "  Confirmed: aspire is not in PATH"

      test_tap_name="local/aspire-test"
      test_tap_root="$(brew --repository)/Library/Taps/local/homebrew-aspire-test"

      cleanup_test_tap() {
        brew untap "$test_tap_name" >/dev/null 2>&1 || true
      }

      test_cask_ref="$test_tap_name/$cask_name"
      test_cask_installed=false

      cleanup_test_install() {
        if [[ "$test_cask_installed" == true ]]; then
          brew uninstall --cask "$test_cask_ref" >/dev/null 2>&1 || true
        fi
      }

      trap 'cleanup_test_install; cleanup; cleanup_test_tap' EXIT

      echo "Setting up local tap..."
      brew tap-new --no-git "$test_tap_name"
      mkdir -p "$test_tap_root/Casks"
      cp "$OUTPUT_FILE" "$test_tap_root/Casks/$cask_file"

      echo "Installing aspire from local tap..."
      test_cask_installed=true
      HOMEBREW_NO_INSTALL_FROM_API=1 brew install --cask "$test_cask_ref"
      echo "Install succeeded"

      echo "Verifying aspire CLI is in PATH..."
      if ! command -v aspire &>/dev/null; then
        echo "Error: aspire command not found in PATH after install" >&2
        brew info --cask "$test_tap_name/$cask_name" || true
        exit 1
      fi

      echo "  Path: $(command -v aspire)"
      aspire_version="$(aspire --version 2>&1)"
      echo "  Version: $aspire_version"
      echo "aspire CLI verified"

      echo "Uninstalling aspire..."
      brew uninstall --cask "$test_cask_ref"
      test_cask_installed=false
      echo "Uninstall succeeded"

      cleanup_test_tap

      if command -v aspire &>/dev/null; then
        echo "Error: aspire command still found in PATH after uninstall" >&2
        exit 1
      else
        echo "  Confirmed: aspire is no longer in PATH"
      fi

      validation_summary="$OUTPUT_DIR/validation-summary.json"
      if [[ "$CHANNEL" == "stable" ]]; then
        is_stable_release=true
      else
        is_stable_release=false
      fi
      cat > "$validation_summary" <<EOF
{
  "schemaVersion": 1,
  "validatedByPreparePipeline": true,
  "isStableRelease": $is_stable_release,
  "isNewCask": $is_new_cask,
  "checks": {
    "rubySyntax": {
      "status": "passed",
      "details": "ruby -c aspire.rb"
    },
    "brewStyle": {
      "status": "passed",
      "details": "brew style --fix on a temporary local tap copy of aspire.rb"
    },
    "brewAudit": {
      "status": "passed",
      "details": "$audit_command"
    },
    "brewInstall": {
      "status": "passed",
      "details": "HOMEBREW_NO_INSTALL_FROM_API=1 brew install --cask local/aspire-test/aspire"
    },
    "brewUninstall": {
      "status": "passed",
      "details": "brew uninstall --cask local/aspire-test/aspire"
    }
  }
}
EOF

      echo "Wrote Homebrew validation summary to $validation_summary"
      cat "$validation_summary"
    fi

    trap - EXIT
    cleanup
  fi
fi

cp "$SCRIPT_DIR/dogfood.sh" "$OUTPUT_DIR/dogfood.sh"
chmod +x "$OUTPUT_DIR/dogfood.sh"

echo "Homebrew cask artifact prepared at: $OUTPUT_DIR"
