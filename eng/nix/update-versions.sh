#!/usr/bin/env bash

set -euo pipefail

repo="microsoft/aspire"
version=""
output_path="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/versions.json"

show_help() {
  cat <<'EOF'
Usage: eng/nix/update-versions.sh --version VERSION [--repo OWNER/REPO] [--output-path PATH]

Updates eng/nix/versions.json from stable Aspire CLI GitHub release assets.
The script reads each official .sha512 checksum asset and converts it to the
Nix SRI hash format used by fetchurl.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "error: --version requires a value" >&2
        exit 1
      fi
      version="$2"
      shift 2
      ;;
    --repo)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "error: --repo requires a value" >&2
        exit 1
      fi
      repo="$2"
      shift 2
      ;;
    --output-path)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "error: --output-path requires a value" >&2
        exit 1
      fi
      output_path="$2"
      shift 2
      ;;
    -h|--help)
      show_help
      exit 0
      ;;
    *)
      echo "error: unknown argument '$1'" >&2
      show_help >&2
      exit 1
      ;;
  esac
done

if [[ -z "$version" ]]; then
  echo "error: --version is required" >&2
  exit 1
fi

normalized_version="${version#[vV]}"
if [[ ! "$normalized_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "error: Nix packaging consumes stable GitHub release assets. Version '$version' must be a stable x.y.z version." >&2
  exit 1
fi

hex_sha512_to_sri() {
  local hex="$1"
  local normalized
  normalized="$(printf '%s' "$hex" | tr -d '\r\n[:space:]' | tr '[:upper:]' '[:lower:]')"

  if [[ ! "$normalized" =~ ^[0-9a-f]{128}$ ]]; then
    echo "error: expected a 128-character SHA512 hex digest, but received '$hex'" >&2
    exit 1
  fi

  printf 'sha512-%s' "$(printf '%s' "$normalized" | xxd -r -p | base64 | tr -d '\r\n')"
}

release_tag="v${normalized_version}"
asset_base_url="https://github.com/${repo}/releases/download/${release_tag}"
systems=(
  "aarch64-darwin:osx-arm64"
  "aarch64-linux:linux-arm64"
  "x86_64-darwin:osx-x64"
  "x86_64-linux:linux-x64"
)

temp_path="${output_path}.tmp"
{
  printf '{\n'
  printf '  "version": "%s",\n' "$normalized_version"
  printf '  "releaseTag": "%s",\n' "$release_tag"
  printf '  "systems": {\n'

  for index in "${!systems[@]}"; do
    system="${systems[$index]%%:*}"
    rid="${systems[$index]#*:}"
    archive_name="aspire-cli-${rid}-${normalized_version}.tar.gz"
    archive_url="${asset_base_url}/${archive_name}"
    checksum_url="${archive_url}.sha512"

    echo "Reading ${checksum_url}" >&2
    checksum_contents="$(curl -fsSL "$checksum_url")"
    # The release checksum sidecar can be either:
    #   <hex-sha512>
    #   <hex-sha512>  <archive-name>
    # Only the first token is the digest consumed by Nix's SRI hash format.
    read -r checksum _ <<< "$checksum_contents"
    hash="$(hex_sha512_to_sri "$checksum")"

    printf '    "%s": {\n' "$system"
    printf '      "rid": "%s",\n' "$rid"
    printf '      "archiveName": "%s",\n' "$archive_name"
    printf '      "url": "%s",\n' "$archive_url"
    printf '      "hash": "%s"\n' "$hash"
    if [[ "$index" == "$((${#systems[@]} - 1))" ]]; then
      printf '    }\n'
    else
      printf '    },\n'
    fi
  done

  printf '  }\n'
  printf '}\n'
} > "$temp_path"

mv "$temp_path" "$output_path"
echo "Updated ${output_path}" >&2
