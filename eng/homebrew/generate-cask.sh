#!/usr/bin/env bash
set -euo pipefail

# Generates a Homebrew cask from the template by downloading archives and computing SHA256 hashes.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  cat <<EOF
Usage: $(basename "$0") --version VERSION --channel CHANNEL [OPTIONS]

Required:
  --version VERSION         Package version (e.g. 9.2.0 or 13.2.0-preview.1.26123.7)
  --channel CHANNEL         Release channel: stable or prerelease

Optional:
  --output PATH             Output file path (default: ./aspire.rb or ./aspire@prerelease.rb)
  --validate-urls           Verify all tarball URLs are accessible before downloading
  --help                    Show this help message
EOF
  exit 1
}

VERSION=""
CHANNEL=""
OUTPUT=""
VALIDATE_URLS=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)        VERSION="$2";  shift 2 ;;
    --channel)        CHANNEL="$2";  shift 2 ;;
    --output)         OUTPUT="$2";   shift 2 ;;
    --validate-urls)  VALIDATE_URLS=true; shift ;;
    --help)           usage ;;
    *)                echo "Unknown option: $1"; usage ;;
  esac
done

if [[ -z "$VERSION" || -z "$CHANNEL" ]]; then
  echo "Error: --version and --channel are required."
  usage
fi

if [[ "$CHANNEL" != "stable" && "$CHANNEL" != "prerelease" ]]; then
  echo "Error: --channel must be 'stable' or 'prerelease'."
  exit 1
fi

# Select template and default output based on channel
if [[ "$CHANNEL" == "prerelease" ]]; then
  TEMPLATE="$SCRIPT_DIR/aspire@prerelease.rb.template"
  [[ -z "$OUTPUT" ]] && OUTPUT="./aspire@prerelease.rb"
else
  TEMPLATE="$SCRIPT_DIR/aspire.rb.template"
  [[ -z "$OUTPUT" ]] && OUTPUT="./aspire.rb"
fi

if [[ ! -f "$TEMPLATE" ]]; then
  echo "Error: Template not found: $TEMPLATE"
  exit 1
fi

BASE_URL="https://ci.dot.net/public/aspire/$VERSION"

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

# Check if a URL is accessible (HEAD request)
url_exists() {
  curl -o /dev/null -s --head --fail "$1"
}

echo "Generating Homebrew cask for Aspire version $VERSION (channel: $CHANNEL)"
echo ""

# macOS tarballs are required
OSX_ARM64_URL="$BASE_URL/aspire-cli-osx-arm64-$VERSION.tar.gz"
OSX_X64_URL="$BASE_URL/aspire-cli-osx-x64-$VERSION.tar.gz"

# Validate URLs are accessible before downloading (fast-fail)
if [[ "$VALIDATE_URLS" == true ]]; then
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
fi

SHA256_OSX_ARM64="$(compute_sha256 "$OSX_ARM64_URL" "macOS ARM64 tarball")"
SHA256_OSX_X64="$(compute_sha256 "$OSX_X64_URL" "macOS x64 tarball")"

echo ""
echo "Generating cask from template..."

# Read template and perform substitutions
content="$(cat "$TEMPLATE")"
content="${content//\$\{VERSION\}/$VERSION}"
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
