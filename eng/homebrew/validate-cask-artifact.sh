#!/usr/bin/env bash
set -euo pipefail

# Validates a generated Aspire Homebrew cask. The script intentionally keeps
# upload/submission concerns out of validation so GitHub Actions and Azure
# DevOps can both use the same checks.
#
# Validation modes:
#   * LiveRelease  — Validates against the live GitHub release for the cask's
#                    version. Runs the full `brew audit --cask --online
#                    --signing` (the same gauntlet Homebrew/homebrew-cask's
#                    per-cask CI runs for a bump PR) plus a real
#                    `brew install`/`brew uninstall` cycle. Used by the
#                    release pipeline after `PublishReleaseAssetsJob` has
#                    uploaded the aspire-cli-osx-* archives to the GitHub
#                    release.
#   * LiveArchives — Validates against the cask file alone, without depending
#                    on the cask URL resolving (the GitHub release for the
#                    version-being-built doesn't exist yet at source-build
#                    time). Runs `brew audit --cask --no-signing` — all
#                    structural checks (style, syntax, naming, verified-vs-url
#                    consistency, version format, conflicts, depends_on
#                    rules). Drops `--online` because several `audit_*`
#                    methods (download, signing, rosetta, min_os) try to
#                    fetch the cask URL when --online is set, and that URL
#                    doesn't resolve yet. LiveRelease covers the --online
#                    checks on every released version.

usage() {
  cat <<EOF
Usage: $(basename "$0") --cask-file PATH [OPTIONS]

Required:
  --cask-file PATH           Path to the generated aspire.rb cask

Optional:
  --channel CHANNEL          Installer channel: stable or prerelease
  --validation-mode MODE     LiveRelease or LiveArchives (default: LiveRelease)
  --help                     Show this help message
EOF
  exit 1
}

CASK_FILE=""
CHANNEL="stable"
VALIDATION_MODE="LiveRelease"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --cask-file) CASK_FILE="$2"; shift 2 ;;
    --channel) CHANNEL="$2"; shift 2 ;;
    --validation-mode) VALIDATION_MODE="$2"; shift 2 ;;
    --help) usage ;;
    *) echo "Unknown option: $1" >&2; usage ;;
  esac
done

shopt -s nocasematch
case "$VALIDATION_MODE" in
  liverelease)  VALIDATION_MODE="LiveRelease" ;;
  livearchives) VALIDATION_MODE="LiveArchives" ;;
  *) echo "Error: --validation-mode must be LiveRelease or LiveArchives." >&2; exit 1 ;;
esac
shopt -u nocasematch

case "$CHANNEL" in
  stable|prerelease) ;;
  *) echo "Error: --channel must be 'stable' or 'prerelease'." >&2; exit 1 ;;
esac

if [[ -z "$CASK_FILE" ]]; then
  echo "Error: --cask-file is required." >&2
  usage
fi

if [[ ! -f "$CASK_FILE" ]]; then
  echo "Error: cask file not found: $CASK_FILE" >&2
  exit 1
fi

CASK_FILE="$(cd "$(dirname "$CASK_FILE")" && pwd)/$(basename "$CASK_FILE")"
CASK_NAME="$(basename "$CASK_FILE" .rb)"

if [[ "$CASK_NAME" != "aspire" ]]; then
  echo "Error: only the aspire cask is supported; got '$CASK_NAME'." >&2
  exit 1
fi

# Aspire CLI is distributed for macOS only via Homebrew cask, and the
# pipeline only submits version bumps for an already-merged upstream cask.
# Initial cask submissions to Homebrew/homebrew-cask are handled manually by
# a human contributor; this script intentionally does not include the
# `brew audit --new` checks that only apply to first-time submissions.

TAP_NAME="local/aspire"
TAP_ROOT=""

remove_tap() {
  local tap_name="$1"

  brew untap "$tap_name" >/dev/null 2>&1 || true

  local tap_org="${tap_name%%/*}"
  local tap_repo="${tap_name##*/}"
  local tap_root
  tap_root="$(brew --repository)/Library/Taps/$tap_org/homebrew-$tap_repo"
  rm -rf "$tap_root"
}

cleanup() {
  remove_tap "$TAP_NAME"
  remove_tap "local/aspire-test"
}

echo "Validating Ruby syntax..."
ruby -c "$CASK_FILE"
echo "ruby -c aspire.rb succeeded."

if ! command -v brew >/dev/null 2>&1; then
  if [[ "$VALIDATION_MODE" == "LiveRelease" ]]; then
    echo "Error: brew is required for LiveRelease validation mode, but it was not found in PATH." >&2
    exit 1
  fi

  echo "Warning: brew was not found in PATH; skipping Homebrew style and audit validation." >&2
  exit 0
fi

trap cleanup EXIT
remove_tap "$TAP_NAME"
brew tap-new --no-git "$TAP_NAME"

TAP_ROOT="$(brew --repository)/Library/Taps/local/homebrew-aspire"
mkdir -p "$TAP_ROOT/Casks"
TAPPED_CASK_PATH="$TAP_ROOT/Casks/aspire.rb"
cp "$CASK_FILE" "$TAPPED_CASK_PATH"

echo ""
echo "Applying Homebrew style fixes in local tap..."
brew style --fix "$TAPPED_CASK_PATH"
cp "$TAPPED_CASK_PATH" "$CASK_FILE"
brew style --cask "$TAPPED_CASK_PATH"
echo "brew style --fix reported no offenses after copying aspire.rb into a temporary local tap."

echo ""
# Tap-level syntax check: validates that every cask in the tap can be parsed
# on every supported platform. Upstream's syntax (macos-N) CI job runs the
# equivalent command and rejects casks that fail to evaluate on a non-host
# platform (e.g. a macOS-only cask with no `depends_on macos:` declared).
# Matches `Homebrew/homebrew-cask:.github/workflows/ci.yml` `brew test-bot
# --only-tap-syntax` step.
echo "Running brew test-bot --only-tap-syntax against local tap..."
brew test-bot --tap "$TAP_NAME" --only-tap-syntax
echo "brew test-bot --only-tap-syntax succeeded."

echo ""
# Match the audit arg set that Homebrew/homebrew-cask CI runs for an existing
# cask bump: `brew audit --cask --online --signing <cask>`. (`--new` is added
# upstream only when the cask is being submitted for the first time, which is
# a human-driven path not covered by this pipeline.)
#
# In LiveRelease mode `--online` is brew's depth flag that enables the
# network-requiring audit methods on top of the structural ones: archive
# download + SHA256 verify, livecheck resolution, github/gitlab repo probes,
# homepage redirect detection.
#
# In LiveArchives mode the cask URL points at a github.com/microsoft/aspire
# release that does not exist yet (the release pipeline uploads it later),
# so `--online` is *omitted*. Several `audit_*` methods download the cask
# archive — not just `audit_download` itself but also `audit_signing`,
# `audit_rosetta`, and `audit_min_os` (they call `extract_artifacts`, which
# fetches the cask URL). Excluding them individually with `--except` is
# brittle because any new `--online`-gated audit method that touches the
# archive in a future brew release would silently start failing.
# `--no-signing` is also used because PR-build / source-build archives are
# unsigned/ad-hoc-signed CI artifacts, not notarized release assets — even
# if we could fetch them, `audit_signing` would reject them.
#
# What we lose in LiveArchives by dropping `--online`:
#   * github/gitlab repo probes (e.g. "is microsoft/aspire archived?")
#   * homepage redirect / 404 detection against aspire.dev
#   * livecheck strategy resolution
# All of these run in LiveRelease in `HomebrewValidateJob` for every
# released version, so a regression in any of them surfaces there.
echo "Auditing cask via local tap..."
audit_args=(--cask --online --signing)
if [[ "$VALIDATION_MODE" == "LiveArchives" ]]; then
  audit_args=(--cask --no-signing)
fi
audit_args+=("$TAP_NAME/$CASK_NAME")
audit_command="brew audit ${audit_args[*]}"
brew audit "${audit_args[@]}"
echo "$audit_command worked successfully."

if [[ "$VALIDATION_MODE" == "LiveRelease" ]]; then
  echo ""
  echo "Testing cask install/uninstall: $CASK_FILE"

  if command -v aspire >/dev/null 2>&1; then
    echo "Error: aspire command is already available before install; test environment is not clean." >&2
    exit 1
  fi

  test_tap_name="local/aspire-test"
  test_tap_root="$(brew --repository)/Library/Taps/local/homebrew-aspire-test"
  test_cask_ref="$test_tap_name/$CASK_NAME"
  test_cask_installed=false

  cleanup_test_install() {
    if [[ "$test_cask_installed" == true ]]; then
      brew uninstall --cask "$test_cask_ref" >/dev/null 2>&1 || true
    fi
  }

  trap 'cleanup_test_install; cleanup' EXIT

  brew tap-new --no-git "$test_tap_name"
  mkdir -p "$test_tap_root/Casks"
  cp "$CASK_FILE" "$test_tap_root/Casks/aspire.rb"

  test_cask_installed=true
  HOMEBREW_NO_INSTALL_FROM_API=1 brew install --cask "$test_cask_ref"

  if ! command -v aspire >/dev/null 2>&1; then
    echo "Error: aspire command not found in PATH after install." >&2
    brew info --cask "$test_cask_ref" || true
    exit 1
  fi

  echo "  Path: $(command -v aspire)"
  aspire_version="$(aspire --version 2>&1)"
  echo "  Version: $aspire_version"

  brew uninstall --cask "$test_cask_ref"
  test_cask_installed=false

  if command -v aspire >/dev/null 2>&1; then
    echo "Error: aspire command still found in PATH after uninstall." >&2
    exit 1
  fi
fi

trap - EXIT
cleanup
