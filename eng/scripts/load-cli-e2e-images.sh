#!/usr/bin/env bash

set -euo pipefail

# All known image variants. Each variant V maps to:
#   --require-V CLI option,
#   ${image_dir}/aspire-cli-e2e-V.tar.gz tarball,
#   aspire-cli-e2e-V:prebuilt image tag,
#   ASPIRE_E2E_<V>_IMAGE / ASPIRE_E2E_REQUIRE_<V>_IMAGE env vars (where <V> is
#   the variant with dashes replaced by underscores and uppercased).
declare -a VARIANTS=(
  dotnet
  polyglot
  polyglot-java
  polyglot-python
  polyglot-go
  polyglot-rust
)

# Mode for each variant is stored in a shell variable named
# require_mode__<variant-with-dashes-converted-to-underscores>.
# Using indirect variable names (via ${!var} / printf -v) rather than an
# associative array keeps this script portable to bash 3.2 (macOS default).
mode_var_for() {
  local variant="$1"
  printf 'require_mode__%s' "${variant//-/_}"
}

for variant in "${VARIANTS[@]}"; do
  printf -v "$(mode_var_for "$variant")" '%s' "false"
done

image_dir=""
github_env="${GITHUB_ENV:-}"

usage() {
  cat <<EOF
Usage: load-cli-e2e-images.sh --image-dir <path> [options]

Loads prebuilt Aspire CLI E2E Docker image artifacts and exports the matching
ASPIRE_E2E_* image environment variables to GITHUB_ENV.

Options:
  --image-dir <path>          Directory containing the image tarballs.
  --github-env <path>         Environment file to append to. Defaults to GITHUB_ENV.
EOF
  for variant in "${VARIANTS[@]}"; do
    printf "  --require-%-19s true, false, or auto. Defaults to false.\n" "$variant <mode>"
  done
  cat <<'EOF'

Mode behavior:
  true   The tarball must exist. Load it, export IMAGE and REQUIRE=true.
  false  Do not load the tarball. Export REQUIRE=false.
  auto   Load and require the image if the tarball exists; otherwise REQUIRE=false.
EOF
}

normalize_mode() {
  local option="$1"
  local value="$2"
  local normalized
  normalized="$(printf "%s" "$value" | tr '[:upper:]' '[:lower:]')"
  case "$normalized" in
    true|1)  printf "true" ;;
    false|0) printf "false" ;;
    auto)    printf "auto" ;;
    *)
      echo "Invalid value for $option: $value. Expected true, false, or auto." >&2
      exit 2
      ;;
  esac
}

# Generic --opt=val / --opt val argument splitter. Sets $opt and $val, and
# consumes one or two positional args from "$@" via outer shifts.
while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help)
      usage; exit 0
      ;;
    --*=*)
      opt="${1%%=*}"
      val="${1#*=}"
      shift
      ;;
    --*)
      opt="$1"
      if [[ $# -lt 2 ]]; then
        echo "Missing value for $opt" >&2
        usage >&2
        exit 2
      fi
      val="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac

  case "$opt" in
    --image-dir)
      image_dir="$val"
      ;;
    --github-env)
      github_env="$val"
      ;;
    --require-*)
      variant="${opt#--require-}"
      mode_var="$(mode_var_for "$variant")"
      if [[ -z "${!mode_var+x}" ]]; then
        echo "Unknown option: $opt" >&2
        usage >&2
        exit 2
      fi
      # normalize_mode's `exit 2` only kills the $(…) subshell, so capture and
      # propagate its exit status explicitly.
      if ! normalized="$(normalize_mode "$opt" "$val")"; then
        exit 2
      fi
      printf -v "$mode_var" '%s' "$normalized"
      ;;
    *)
      echo "Unknown option: $opt" >&2
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
  printf "%s=%s\n" "$1" "$2" >> "$github_env"
}

load_image() {
  local variant="$1"
  local mode="$2"
  local tarball_path="${image_dir%/}/aspire-cli-e2e-${variant}.tar.gz"
  local image_tag="aspire-cli-e2e-${variant}:prebuilt"
  # Derive env-var names from the variant: 'polyglot-java' -> 'POLYGLOT_JAVA'
  # (uppercase letters, '-' -> '_'). `tr` aligns the source set [a-z-] with the
  # target set [A-Z_] positionally, which works on GNU, BSD, and Busybox tr.
  local suffix
  suffix="$(printf '%s' "$variant" | tr 'a-z-' 'A-Z_')"
  local image_env_name="ASPIRE_E2E_${suffix}_IMAGE"
  local require_env_name="ASPIRE_E2E_REQUIRE_${suffix}_IMAGE"

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
    echo "::error::$variant image is required but artifact was not found at $tarball_path"
    exit 1
  fi

  docker load -i "$tarball_path"
  docker image inspect "$image_tag" > /dev/null
  write_env "$image_env_name" "$image_tag"
  write_env "$require_env_name" "true"
}

for variant in "${VARIANTS[@]}"; do
  mode_var="$(mode_var_for "$variant")"
  load_image "$variant" "${!mode_var}"
done
