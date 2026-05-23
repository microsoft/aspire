#!/usr/bin/env bash
# Smoke-tests the already-installed `aspire` CLI by scaffolding a starter
# project and running its restore. Assumes `aspire` is on PATH.
#
# Used by CI after a real installer run (Homebrew cask / WinGet manifest /
# dotnet-tool / archive script) to catch regressions that only show up once the
# installed bits actually launch — broken launcher resolution, missing layout
# assets, packaging-time PATH issues, etc.
set -euo pipefail

usage() {
  cat <<EOF
Usage: $(basename "$0") [OPTIONS]

Options:
  --work-dir PATH       Parent directory in which to create a fresh scaffold
                        subdirectory. A unique subdirectory is always created
                        inside it; nothing in PATH is removed.
                        Default: \${RUNNER_TEMP:-/tmp}
  --project-name NAME   Project name passed to 'aspire new'. Default: SmokeApp
  --log-level LEVEL     --log-level value passed to aspire commands. Default: trace
  --help                Show this help message
EOF
}

WORK_DIR=""
PROJECT_NAME="SmokeApp"
LOG_LEVEL="trace"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --work-dir) WORK_DIR="$2"; shift 2 ;;
    --project-name) PROJECT_NAME="$2"; shift 2 ;;
    --log-level) LOG_LEVEL="$2"; shift 2 ;;
    --help) usage; exit 0 ;;
    *) echo "Error: unknown option: $1" >&2; usage >&2; exit 1 ;;
  esac
done

PARENT_DIR="${WORK_DIR:-${RUNNER_TEMP:-/tmp}}"
mkdir -p "$PARENT_DIR"

# Always scaffold into a fresh subdirectory created with mktemp under
# $PARENT_DIR. This deliberately avoids ever rm -rf'ing a caller-provided
# path: even if --work-dir points at a sensitive directory, the worst case is
# a new empty aspire-cli-smoke.XXXXXX subdirectory being created underneath.
# CI tears down RUNNER_TEMP between jobs; local users can clean up whenever.
scaffold_dir="$(mktemp -d "$PARENT_DIR/aspire-cli-smoke.XXXXXX")"
echo "Scaffolding into: $scaffold_dir"

aspire --version
cd "$scaffold_dir"

aspire --log-level "$LOG_LEVEL" new aspire-starter --name "$PROJECT_NAME" --output . --non-interactive --nologo --suppress-agent-init
aspire --log-level "$LOG_LEVEL" restore --non-interactive --nologo
