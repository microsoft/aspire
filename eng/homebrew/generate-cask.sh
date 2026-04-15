#!/usr/bin/env bash
set -euo pipefail

# Generates a Homebrew cask from the template by downloading archives and computing SHA256 hashes.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  cat <<EOF
Usage: $(basename "$0") --version VERSION [OPTIONS]

Required:
  --version VERSION         Installer version in the cask and archive filename (e.g. 9.2.0)

Optional:
  --artifact-version VER    Version segment used in the ci.dot.net artifact path (defaults to --version)
  --output PATH             Output file path (default: ./aspire.rb)
  --archive-root PATH       Root directory containing locally built CLI archives to hash
  --skip-url-validation     Skip URL validation and downloading; use placeholder SHA256 hashes
  --help                    Show this help message
EOF
  exit 1
}

VERSION=""
ARTIFACT_VERSION=""
OUTPUT=""
ARCHIVE_ROOT=""
SKIP_URL_VALIDATION=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)              VERSION="$2";  shift 2 ;;
    --artifact-version)     ARTIFACT_VERSION="$2"; shift 2 ;;
    --output)               OUTPUT="$2";   shift 2 ;;
    --archive-root)         ARCHIVE_ROOT="$2"; shift 2 ;;
    --skip-url-validation)  SKIP_URL_VALIDATION=true; shift ;;
    --help)                 usage ;;
    *)                      echo "Unknown option: $1"; usage ;;
  esac
done

if [[ -z "$VERSION" ]]; then
  echo "Error: --version is required."
  usage
fi

if [[ -z "$ARTIFACT_VERSION" ]]; then
  ARTIFACT_VERSION="$VERSION"
fi

TEMPLATE="$SCRIPT_DIR/aspire.rb.template"
[[ -z "$OUTPUT" ]] && OUTPUT="./aspire.rb"

if [[ ! -f "$TEMPLATE" ]]; then
  echo "Error: Template not found: $TEMPLATE"
  exit 1
fi

BASE_URL="https://ci.dot.net/public/aspire/$ARTIFACT_VERSION"

# Compute SHA256 for a URL by downloading to a temp file
compute_sha256() {
  local url="$1"
  local description="$2"
  local tmpfile

  echo "Downloading $description to compute SHA256..." >&2
  echo "  URL: $url" >&2

  tmpfile="$(mktemp)"
  trap 'rm -f "$tmpfile"' RETURN

  curl -fsSL "$url" -o "$tmpfile"
  local hash
  hash="$(shasum -a 256 "$tmpfile" | awk '{print $1}')"
  echo "  SHA256: $hash" >&2
  echo "$hash"
}

compute_sha256_from_file() {
  local file_path="$1"
  local description="$2"

  echo "Computing SHA256 for $description from local file..." >&2
  echo "  Path: $file_path" >&2

  local hash
  hash="$(shasum -a 256 "$file_path" | awk '{print $1}')"
  echo "  SHA256: $hash" >&2
  echo "$hash"
}

find_local_archive() {
  local archive_name="$1"
  local archive_path
  local matches=()
  local match

  while IFS= read -r archive_path; do
    matches+=("$archive_path")
  done < <(find "$ARCHIVE_ROOT" -type f -name "$archive_name" -print | LC_ALL=C sort)

  if [[ "${#matches[@]}" -eq 0 ]]; then
    echo "Error: Could not find local archive '$archive_name' under '$ARCHIVE_ROOT'" >&2
    exit 1
  fi

  if [[ "${#matches[@]}" -gt 1 ]]; then
    echo "Error: Found multiple local archives named '$archive_name' under '$ARCHIVE_ROOT':" >&2
    for match in "${matches[@]}"; do
      echo "  $match" >&2
    done
    exit 1
  fi

  echo "${matches[0]}"
}

# Check if a URL is accessible (HEAD request)
url_exists() {
  curl -o /dev/null -s --head --fail "$1"
}

echo "Generating Homebrew cask for Aspire version $VERSION"
echo ""

# macOS tarballs are required
OSX_ARM64_URL="$BASE_URL/aspire-cli-osx-arm64-$VERSION.tar.gz"
OSX_X64_URL="$BASE_URL/aspire-cli-osx-x64-$VERSION.tar.gz"

if [[ -n "$ARCHIVE_ROOT" && ! -d "$ARCHIVE_ROOT" ]]; then
  echo "Error: --archive-root directory does not exist: $ARCHIVE_ROOT"
  exit 1
fi

if [[ -n "$ARCHIVE_ROOT" ]]; then
  echo "Computing SHA256 hashes from local archives in $ARCHIVE_ROOT..."
  OSX_ARM64_ARCHIVE="$(find_local_archive "aspire-cli-osx-arm64-$VERSION.tar.gz")"
  OSX_X64_ARCHIVE="$(find_local_archive "aspire-cli-osx-x64-$VERSION.tar.gz")"

  SHA256_OSX_ARM64="$(compute_sha256_from_file "$OSX_ARM64_ARCHIVE" "macOS ARM64 tarball")"
  SHA256_OSX_X64="$(compute_sha256_from_file "$OSX_X64_ARCHIVE" "macOS x64 tarball")"
elif [[ "$SKIP_URL_VALIDATION" == true ]]; then
  echo "SkipUrlValidation specified — using placeholder SHA256 hashes (tarball URLs will not be validated or downloaded)"
  SHA256_OSX_ARM64="$(printf '0%.0s' {1..64})"
  SHA256_OSX_X64="$(printf '0%.0s' {1..64})"
  echo "  osx-arm64: URL=$OSX_ARM64_URL, SHA256=$SHA256_OSX_ARM64 (placeholder)"
  echo "  osx-x64: URL=$OSX_X64_URL, SHA256=$SHA256_OSX_X64 (placeholder)"
else
  # Validate URLs are accessible before downloading (fast-fail)
  echo "Validating tarball URLs..."
  failed=false
  for url in "$OSX_ARM64_URL" "$OSX_X64_URL"; do
    echo "  Checking: $url"
    if url_exists "$url"; then
      echo "    OK"
    else
      echo "    ERROR: URL not accessible"
      failed=true
    fi
  done

  if [[ "$failed" == true ]]; then
    echo "Error: One or more tarball URLs are not accessible. Ensure the release artifacts have been published."
    exit 1
  fi
  echo ""

  SHA256_OSX_ARM64="$(compute_sha256 "$OSX_ARM64_URL" "macOS ARM64 tarball")"
  SHA256_OSX_X64="$(compute_sha256 "$OSX_X64_URL" "macOS x64 tarball")"
fi

echo ""
echo "Generating cask from template..."

# Read template and perform substitutions
content="$(cat "$TEMPLATE")"
content="${content//\$\{VERSION\}/$VERSION}"
content="${content//\$\{ARTIFACT_VERSION\}/$ARTIFACT_VERSION}"
content="${content//\$\{SHA256_OSX_ARM64\}/$SHA256_OSX_ARM64}"
content="${content//\$\{SHA256_OSX_X64\}/$SHA256_OSX_X64}"

# Write output
mkdir -p "$(dirname "$OUTPUT")"
printf '%s\n' "$content" > "$OUTPUT"

echo "  Created: $OUTPUT"
echo ""
echo "Next steps:"
echo "  1. Validate syntax: ruby -c \"$OUTPUT\""
echo "  2. Audit cask:      brew audit --cask aspire (after installing to a local tap)"
echo "  3. Test install:    brew install --cask \"$OUTPUT\""
