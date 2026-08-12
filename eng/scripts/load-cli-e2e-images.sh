#!/usr/bin/env bash

set -euo pipefail

image_dir=""
github_env="${GITHUB_ENV:-}"
require_dotnet="false"
require_polyglot="false"
require_java="false"

usage() {
  cat <<'EOF'
Usage: load-cli-e2e-images.sh --image-dir <path> [options]

Loads prebuilt Aspire CLI E2E Docker image artifacts and exports the matching
ASPIRE_E2E_* image environment variables to GITHUB_ENV.

Options:
  --image-dir <path>          Directory containing the image tarballs.
  --github-env <path>         Environment file to append to. Defaults to GITHUB_ENV.
  --require-dotnet <mode>     true, false, or auto. Defaults to false.
  --require-polyglot <mode>   true, false, or auto. Defaults to false.
  --require-java <mode>       true, false, or auto. Defaults to false.

Mode behavior:
  true   The tarball must exist. Load it, export IMAGE and REQUIRE=true.
  false  Do not load the tarball. Export REQUIRE=false.
  auto   Load and require the image if the tarball exists; otherwise REQUIRE=false.
EOF
}

read_value_arg() {
  local option="$1"
  local value="${2:-}"
  if [[ -z "$value" ]]; then
    echo "Missing value for $option" >&2
    usage >&2
    exit 2
  fi

  printf "%s" "$value"
}

normalize_mode() {
  local option="$1"
  local value="$2"
  local normalized_value
  normalized_value="$(printf "%s" "$value" | tr '[:upper:]' '[:lower:]')"
  case "$normalized_value" in
    true|1)
      printf "true"
      ;;
    false|0)
      printf "false"
      ;;
    auto)
      printf "auto"
      ;;
    *)
      echo "Invalid value for $option: $value. Expected true, false, or auto." >&2
      exit 2
      ;;
  esac
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --image-dir)
      image_dir="$(read_value_arg "$1" "${2:-}")"
      shift 2
      ;;
    --image-dir=*)
      image_dir="${1#*=}"
      shift
      ;;
    --github-env)
      github_env="$(read_value_arg "$1" "${2:-}")"
      shift 2
      ;;
    --github-env=*)
      github_env="${1#*=}"
      shift
      ;;
    --require-dotnet)
      require_dotnet="$(normalize_mode "$1" "$(read_value_arg "$1" "${2:-}")")"
      shift 2
      ;;
    --require-dotnet=*)
      require_dotnet="$(normalize_mode "--require-dotnet" "${1#*=}")"
      shift
      ;;
    --require-polyglot)
      require_polyglot="$(normalize_mode "$1" "$(read_value_arg "$1" "${2:-}")")"
      shift 2
      ;;
    --require-polyglot=*)
      require_polyglot="$(normalize_mode "--require-polyglot" "${1#*=}")"
      shift
      ;;
    --require-java)
      require_java="$(normalize_mode "$1" "$(read_value_arg "$1" "${2:-}")")"
      shift 2
      ;;
    --require-java=*)
      require_java="$(normalize_mode "--require-java" "${1#*=}")"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -z "$image_dir" ]]; then
  echo "--image-dir is required." >&2
  usage >&2
  exit 2
fi

if [[ -z "$github_env" ]]; then
  echo "GITHUB_ENV is not set. Pass --github-env or run inside GitHub Actions." >&2
  exit 2
fi

write_env() {
  local name="$1"
  local value="$2"
  printf "%s=%s\n" "$name" "$value" >> "$github_env"
}

load_image() {
  local display_name="$1"
  local mode="$2"
  local tarball_name="$3"
  local image_tag="$4"
  local image_env_name="$5"
  local require_env_name="$6"
  local tarball_path="${image_dir%/}/$tarball_name"

  if [[ "$mode" == "auto" ]]; then
    if [[ -f "$tarball_path" ]]; then
      mode="true"
    else
      mode="false"
    fi
  fi

  if [[ "$mode" == "false" ]]; then
    write_env "$require_env_name" "false"
    return
  fi

  if [[ ! -f "$tarball_path" ]]; then
    echo "::error::$display_name image is required but artifact was not found at $tarball_path"
    exit 1
  fi

  docker load -i "$tarball_path"
  docker image inspect "$image_tag" > /dev/null
  write_env "$image_env_name" "$image_tag"
  write_env "$require_env_name" "true"
}

load_image ".NET" "$require_dotnet" \
  "aspire-cli-e2e-dotnet.tar.gz" \
  "aspire-cli-e2e-dotnet:prebuilt" \
  "ASPIRE_E2E_DOTNET_IMAGE" \
  "ASPIRE_E2E_REQUIRE_DOTNET_IMAGE"

load_image "polyglot" "$require_polyglot" \
  "aspire-cli-e2e-polyglot.tar.gz" \
  "aspire-cli-e2e-polyglot:prebuilt" \
  "ASPIRE_E2E_POLYGLOT_IMAGE" \
  "ASPIRE_E2E_REQUIRE_POLYGLOT_IMAGE"

load_image "Java polyglot" "$require_java" \
  "aspire-cli-e2e-polyglot-java.tar.gz" \
  "aspire-cli-e2e-polyglot-java:prebuilt" \
  "ASPIRE_E2E_POLYGLOT_JAVA_IMAGE" \
  "ASPIRE_E2E_REQUIRE_POLYGLOT_JAVA_IMAGE"
