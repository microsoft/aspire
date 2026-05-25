#!/usr/bin/env bash
set -euo pipefail

# Validates a generated Aspire Homebrew cask. The script intentionally keeps
# upload/submission concerns out of validation so GitHub Actions and Azure
# DevOps can both use the same checks.

usage() {
  cat <<EOF
Usage: $(basename "$0") --cask-file PATH [OPTIONS]

Required:
  --cask-file PATH           Path to the generated aspire.rb cask

Optional:
  --channel CHANNEL          Installer channel: stable or prerelease
  --archive-root PATH        Root directory containing locally built CLI archives
  --validation-mode MODE     Full, Offline, or GenerateOnly (default: Full)
  --summary-path PATH        Write validation-summary.json for release submission
  --help                     Show this help message
EOF
  exit 1
}

CASK_FILE=""
CHANNEL="stable"
ARCHIVE_ROOT=""
VALIDATION_MODE="Full"
SUMMARY_PATH=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --cask-file) CASK_FILE="$2"; shift 2 ;;
    --channel) CHANNEL="$2"; shift 2 ;;
    --archive-root) ARCHIVE_ROOT="$2"; shift 2 ;;
    --validation-mode) VALIDATION_MODE="$2"; shift 2 ;;
    --summary-path) SUMMARY_PATH="$2"; shift 2 ;;
    --help) usage ;;
    *) echo "Unknown option: $1" >&2; usage ;;
  esac
done

case "$VALIDATION_MODE" in
  Full|Offline|GenerateOnly) ;;
  full) VALIDATION_MODE="Full" ;;
  offline) VALIDATION_MODE="Offline" ;;
  generateonly|generate-only) VALIDATION_MODE="GenerateOnly" ;;
  *) echo "Error: --validation-mode must be Full, Offline, or GenerateOnly." >&2; exit 1 ;;
esac

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

if [[ "$VALIDATION_MODE" == "GenerateOnly" ]]; then
  echo "Skipping Homebrew cask validation because validation mode is GenerateOnly."
  exit 0
fi

read_cask_version() {
  local cask_file="$1"
  awk -F'"' '/^[[:space:]]*version[[:space:]]+"/ { print $2; exit }' "$cask_file"
}

# Detects whether the Aspire cask already exists in the upstream
# Homebrew/homebrew-cask repository. Echoes 'true' if the cask is new (404 from
# the contents API), 'false' if it already exists (200), and exits non-zero on
# any other response so we don't silently mis-classify the audit mode.
#
# brew audit treats new submissions and updates differently: `--new` enables
# extra checks (e.g. canonical naming) that fail on existing casks. The PR body
# template downstream also keys "validated as a new upstream cask" off the same
# signal, so getting this wrong produces misleading review text in the bot PR.
detect_upstream_cask_is_new() {
  local cask_name="$1"
  local first_letter="${cask_name:0:1}"
  local target_path="Casks/$first_letter/$cask_name.rb"
  local api_url="https://api.github.com/repos/Homebrew/homebrew-cask/contents/$target_path"
  local status_code
  local curl_args=(-sS -o /dev/null -w "%{http_code}")

  # Authenticate when a token is available. Unauthenticated GitHub API requests
  # are throttled to 60/hour shared across the runner IP pool, which routinely
  # produces 403s on hosted CI; an installation/PAT/Actions token raises that
  # to 1000/hour or more. GH_TOKEN is the convention used by the `gh` CLI and
  # by most repo scripts; GITHUB_TOKEN is the default exposed by GitHub Actions.
  local token="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
  if [[ -n "$token" ]]; then
    curl_args+=(-H "Authorization: Bearer $token" -H "X-GitHub-Api-Version: 2022-11-28")
  fi

  status_code="$(curl "${curl_args[@]}" "$api_url")"
  case "$status_code" in
    200) echo "false" ;;
    404) echo "true" ;;
    *) echo "Error: could not determine whether $target_path exists upstream (HTTP $status_code)" >&2; exit 1 ;;
  esac
}

find_archive() {
  local archive_root="$1"
  local archive_name="$2"
  local matches=()
  local match

  while IFS= read -r match; do
    matches+=("$match")
  done < <(find "$archive_root" -type f -name "$archive_name" -print | LC_ALL=C sort)

  if [[ "${#matches[@]}" -eq 0 ]]; then
    echo "Error: could not find $archive_name under $archive_root" >&2
    exit 1
  fi

  if [[ "${#matches[@]}" -gt 1 ]]; then
    echo "Error: found multiple $archive_name archives under $archive_root:" >&2
    printf '  %s\n' "${matches[@]}" >&2
    exit 1
  fi

  printf '%s' "${matches[0]}"
}

start_archive_server() {
  local archive_root="$1"
  local version="$2"
  local server_root="$3"
  local port_file="$4"

  local arm_archive
  local x64_archive
  arm_archive="$(find_archive "$archive_root" "aspire-cli-osx-arm64-$version.tar.gz")"
  x64_archive="$(find_archive "$archive_root" "aspire-cli-osx-x64-$version.tar.gz")"

  mkdir -p "$server_root"
  cp "$arm_archive" "$server_root/aspire-cli-osx-arm64-$version.tar.gz"
  cp "$x64_archive" "$server_root/aspire-cli-osx-x64-$version.tar.gz"

  python3 - "$server_root" "$port_file" <<'PY' &
import http.server
import socketserver
import sys

root = sys.argv[1]
port_file = sys.argv[2]

class ArchiveHandler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=root, **kwargs)

    def log_message(self, format, *args):
        pass

    def copyfile(self, source, outputfile):
        try:
            super().copyfile(source, outputfile)
        except (BrokenPipeError, ConnectionResetError):
            pass

class ArchiveServer(socketserver.TCPServer):
    allow_reuse_address = True

with ArchiveServer(("127.0.0.1", 0), ArchiveHandler) as httpd:
    with open(port_file, "w", encoding="utf-8") as f:
        f.write(str(httpd.server_address[1]))
    httpd.serve_forever()
PY

  ARCHIVE_SERVER_PID=$!

  for _ in {1..100}; do
    if [[ -s "$port_file" ]]; then
      return
    fi
    sleep 0.1
  done

  echo "Error: timed out waiting for local archive server to start." >&2
  exit 1
}

rewrite_cask_for_local_archives() {
  local cask_file="$1"
  local local_url_prefix="$2"
  local verified_host="$3"

  ruby - "$cask_file" "$local_url_prefix" "$verified_host" <<'RUBY'
cask_path = ARGV[0]
local_url_prefix = ARGV[1]
verified_host = ARGV[2]
lines = File.readlines(cask_path, chomp: true)
rewritten = []
skip_verified = false

lines.each do |line|
  stripped = line.strip
  if stripped.start_with?('url "https://ci.dot.net/public/aspire/')
    rewritten << "  url \"#{local_url_prefix}/aspire-cli-osx-\#{arch}-\#{version}.tar.gz\","
    rewritten << "      verified: \"#{verified_host}\""
    skip_verified = true
    next
  end

  if skip_verified && stripped.start_with?('verified: "ci.dot.net/public/aspire/"')
    skip_verified = false
    next
  end

  skip_verified = false
  rewritten << line
end

File.write(cask_path, rewritten.join("\n") + "\n")
RUBY
}

TAP_NAME="local/aspire"
TAP_ROOT=""
ARCHIVE_SERVER_PID=""
TEMP_DIR=""

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

  if [[ -n "$ARCHIVE_SERVER_PID" ]]; then
    kill "$ARCHIVE_SERVER_PID" >/dev/null 2>&1 || true
    wait "$ARCHIVE_SERVER_PID" >/dev/null 2>&1 || true
  fi

  if [[ -n "$TEMP_DIR" ]]; then
    rm -rf "$TEMP_DIR"
  fi
}

echo "Validating Ruby syntax..."
ruby -c "$CASK_FILE"
echo "ruby -c aspire.rb succeeded."

if ! command -v brew >/dev/null 2>&1; then
  if [[ "$VALIDATION_MODE" == "Full" ]]; then
    echo "Error: brew is required for Full validation mode, but it was not found in PATH." >&2
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

AUDIT_CASK_PATH="$TAPPED_CASK_PATH"
AUDIT_NOTE=""

if [[ "$VALIDATION_MODE" == "Offline" ]]; then
  if [[ -z "$ARCHIVE_ROOT" ]]; then
    echo "Skipping online audit because validation mode is Offline and no archive root was provided."
    trap - EXIT
    cleanup
    exit 0
  fi

  if ! command -v python3 >/dev/null 2>&1; then
    echo "Error: python3 is required to run Offline online audit against local archive URLs." >&2
    exit 1
  fi

  cask_version="$(read_cask_version "$CASK_FILE")"
  if [[ -z "$cask_version" ]]; then
    echo "Error: could not read version from $CASK_FILE" >&2
    exit 1
  fi

  TEMP_DIR="$(mktemp -d)"
  port_file="$TEMP_DIR/archive-server.port"
  start_archive_server "$ARCHIVE_ROOT" "$cask_version" "$TEMP_DIR/archives" "$port_file"
  archive_port="$(cat "$port_file")"

  cp "$CASK_FILE" "$AUDIT_CASK_PATH"
  rewrite_cask_for_local_archives "$AUDIT_CASK_PATH" "http://127.0.0.1:$archive_port" "127.0.0.1:$archive_port/"
  AUDIT_NOTE=" against local archive URLs"
fi

echo ""
if [[ "$VALIDATION_MODE" == "Offline" ]]; then
  echo "Skipping upstream cask probe because validation mode is Offline; running standard audit."
  IS_NEW_CASK="false"
else
  echo "Determining whether $CASK_NAME is a new upstream cask..."
  IS_NEW_CASK="$(detect_upstream_cask_is_new "$CASK_NAME")"
  if [[ "$IS_NEW_CASK" == "true" ]]; then
    echo "Detected new upstream cask; running new-cask audit."
  else
    echo "Detected existing upstream cask; running standard audit."
  fi
fi

echo ""
echo "Auditing cask via local tap..."
audit_args=(--cask --online)
if [[ "$IS_NEW_CASK" == "true" ]]; then
  audit_args+=(--new)
fi
if [[ "$VALIDATION_MODE" == "Offline" ]]; then
  audit_args+=(--no-signing)
fi
audit_args+=("$TAP_NAME/$CASK_NAME")
audit_command="brew audit ${audit_args[*]}"
brew audit "${audit_args[@]}"
echo "$audit_command worked successfully$AUDIT_NOTE."

if [[ "$VALIDATION_MODE" == "Full" ]]; then
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

if [[ -n "$SUMMARY_PATH" && "$VALIDATION_MODE" == "Full" ]]; then
  if [[ "$CHANNEL" == "stable" ]]; then
    is_stable_release=true
  else
    is_stable_release=false
  fi

  mkdir -p "$(dirname "$SUMMARY_PATH")"
  cat > "$SUMMARY_PATH" <<EOF
{
  "schemaVersion": 1,
  "validatedByPreparePipeline": true,
  "isStableRelease": $is_stable_release,
  "isNewCask": $IS_NEW_CASK,
  "checks": {
    "rubySyntax": {
      "status": "passed",
      "details": "ruby -c aspire.rb"
    },
    "brewStyle": {
      "status": "passed",
      "details": "brew style --fix reported no offenses after copying aspire.rb into a temporary local tap"
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

  echo "Wrote Homebrew validation summary to $SUMMARY_PATH"
  cat "$SUMMARY_PATH"
fi

trap - EXIT
cleanup
