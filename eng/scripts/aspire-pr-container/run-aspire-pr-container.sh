#!/usr/bin/env bash

set -euo pipefail

usage() {
    cat <<'EOF'
Usage:
  run-aspire-pr-container.sh [command [args...]]

Environment:
  ASPIRE_PR_IMAGE        Docker image name to build/run (default: aspire-pr-runner)
  ASPIRE_PR_WORKSPACE    Host directory to mount as /workspace (default: current directory)
  ASPIRE_PR_STATE_VOLUME Docker named volume mounted at /workspace/.aspire
                         (default: deterministic name derived from the workspace path)
  ASPIRE_DOCKER_SOCKET   Docker socket path on the host (default: /var/run/docker.sock)
  ASPIRE_CONTAINER_USER  Container user for docker run (default: current uid:gid)
                         Set to 0:0 when the container needs direct Docker socket access.
  ASPIRE_PR_RECORD       Set to 1/true to record the full host-side session with asciinema
  ASPIRE_PR_RECORDING_PATH
                         Output path for the .cast file
                         (default: <workspace>/recordings/<timestamp>-<command>.cast)
  ASPIRE_PR_RECORDING_TITLE
                          Optional title stored in the recording metadata
  GH_TOKEN/GITHUB_TOKEN  GitHub token passed into the container

Default command:
  bash
EOF
}

is_truthy() {
    local value
    value="$(printf '%s' "${1:-}" | tr '[:upper:]' '[:lower:]')"

    case "$value" in
        1|true|yes|on)
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}

get_recording_stem() {
    local stem="${1:-session}"

    if [[ "$stem" =~ ^[0-9]+$ ]]; then
        stem="pr-$stem"
    fi

    stem="$(printf '%s' "$stem" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9._-]+/-/g; s/^-+//; s/-+$//')"

    if [[ -z "$stem" ]]; then
        stem="session"
    fi

    printf '%s' "$stem"
}

compute_workspace_hash() {
    local value="$1"
    local digest

    if command -v sha256sum >/dev/null 2>&1; then
        digest="$(printf '%s' "$value" | sha256sum | awk '{print $1}')"
    elif command -v shasum >/dev/null 2>&1; then
        digest="$(printf '%s' "$value" | shasum -a 256 | awk '{print $1}')"
    else
        digest="$(printf '%s' "$value" | cksum | awk '{print $1}')"
    fi

    printf '%s' "${digest:0:12}"
}

get_state_volume_name() {
    local workspace="$1"
    local resolved_workspace

    if [[ -n "${ASPIRE_PR_STATE_VOLUME:-}" ]]; then
        printf '%s' "$ASPIRE_PR_STATE_VOLUME"
        return
    fi

    resolved_workspace="$(cd "$workspace" && pwd -P)"
    printf 'aspire-pr-state-%s' "$(compute_workspace_hash "$resolved_workspace")"
}

resolve_host_path() {
    local path="$1"

    if command -v realpath >/dev/null 2>&1; then
        realpath "$path" 2>/dev/null && return 0
    fi

    if command -v readlink >/dev/null 2>&1 && readlink -f / >/dev/null 2>&1; then
        readlink -f "$path" 2>/dev/null && return 0
    fi

    printf '%s\n' "$path"
    return 1
}

ensure_github_token() {
    local token

    if [[ -n "${GH_TOKEN:-}" ]]; then
        export GH_TOKEN
        return
    fi

    if [[ -n "${GITHUB_TOKEN:-}" ]]; then
        GH_TOKEN="$GITHUB_TOKEN"
        export GH_TOKEN
        return
    fi

    if ! command -v gh >/dev/null 2>&1; then
        echo "GitHub CLI 'gh' is required when GH_TOKEN/GITHUB_TOKEN is not set. Run 'gh auth login' or export GH_TOKEN." >&2
        exit 1
    fi

    if ! token="$(gh auth token 2>/dev/null)"; then
        echo "Failed to get a GitHub token from 'gh auth token'. Run 'gh auth login' or export GH_TOKEN/GITHUB_TOKEN." >&2
        exit 1
    fi

    if [[ -z "$token" ]]; then
        echo "GitHub CLI returned an empty token. Run 'gh auth login' or export GH_TOKEN/GITHUB_TOKEN." >&2
        exit 1
    fi

    GH_TOKEN="$token"
    export GH_TOKEN
}

prepare_state_volume() {
    local image_name="$1"
    local state_volume="$2"
    local container_user="$3"

    docker volume create "$state_volume" >/dev/null

    docker run --rm \
        -u 0:0 \
        -e TARGET_USER="$container_user" \
        -v "$state_volume:/state" \
        "$image_name" \
        bash -lc 'set -euo pipefail; mkdir -p /state; if [[ -n "${TARGET_USER:-}" && "${TARGET_USER}" != "0:0" && "${TARGET_USER}" != "0" ]]; then chown -R "$TARGET_USER" /state; fi'
}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT_PATH="$SCRIPT_DIR/$(basename "${BASH_SOURCE[0]}")"
IMAGE_NAME="${ASPIRE_PR_IMAGE:-aspire-pr-runner}"
WORKSPACE="${ASPIRE_PR_WORKSPACE:-$PWD}"
DOCKER_SOCKET_PATH="${ASPIRE_DOCKER_SOCKET:-/var/run/docker.sock}"
CONTAINER_USER="${ASPIRE_CONTAINER_USER:-$(id -u):$(id -g)}"
STATE_VOLUME="$(get_state_volume_name "$WORKSPACE")"

if [[ $# -eq 0 ]]; then
    set -- bash
fi

case "$1" in
    -h|--help)
        usage
        exit 0
        ;;
esac

if [[ ! -d "$WORKSPACE" ]]; then
    echo "Workspace directory does not exist: $WORKSPACE" >&2
    exit 1
fi

if [[ -z "${ASPIRE_PR_RECORDING_ACTIVE:-}" ]] && [[ -n "${ASPIRE_PR_RECORD:-}" ]] && is_truthy "${ASPIRE_PR_RECORD}"; then
    if ! command -v asciinema >/dev/null 2>&1; then
        echo "asciinema is required when ASPIRE_PR_RECORD is enabled." >&2
        exit 1
    fi

    recording_path="${ASPIRE_PR_RECORDING_PATH:-$WORKSPACE/recordings/$(date -u +%Y%m%dT%H%M%SZ)-$(get_recording_stem "$1").cast}"
    mkdir -p "$(dirname "$recording_path")"

    recording_command=("$SCRIPT_PATH" "$@")
    recording_command_string="$(printf '%q ' "${recording_command[@]}")"
    recording_command_string="${recording_command_string% }"

    export ASPIRE_PR_RECORDING_ACTIVE=1
    echo "Recording session to $recording_path" >&2

    recording_args=(
        record
        --return
        --command "$recording_command_string"
    )

    if [[ -n "${ASPIRE_PR_RECORDING_TITLE:-}" ]]; then
        recording_args+=(--title "$ASPIRE_PR_RECORDING_TITLE")
    fi

    recording_args+=("$recording_path")

    asciinema "${recording_args[@]}"
    exit $?
fi

ensure_github_token

tty_args=()
if [[ -t 0 && -t 1 ]]; then
    tty_args=(-it)
fi

run_args=(
    --rm
    -e GH_TOKEN
    -e ASPIRE_REPO
    -e HOME=/workspace
    -u "$CONTAINER_USER"
    -v "$WORKSPACE:/workspace"
    -v "$STATE_VOLUME:/workspace/.aspire"
    -w /workspace
)

if [[ ${#tty_args[@]} -gt 0 ]]; then
    run_args+=("${tty_args[@]}")
fi

if [[ -e "$DOCKER_SOCKET_PATH" ]]; then
    DOCKER_SOCKET_REALPATH="$DOCKER_SOCKET_PATH"
    if ! DOCKER_SOCKET_REALPATH="$(resolve_host_path "$DOCKER_SOCKET_PATH")"; then
        echo "Warning: Unable to resolve Docker socket path '$DOCKER_SOCKET_PATH' with realpath or readlink -f; using the original path." >&2
        DOCKER_SOCKET_REALPATH="$DOCKER_SOCKET_PATH"
    fi

    if [[ -S "$DOCKER_SOCKET_REALPATH" ]]; then
        run_args+=(-v "$DOCKER_SOCKET_REALPATH:/var/run/docker.sock")
    elif [[ "$DOCKER_SOCKET_REALPATH" != "$DOCKER_SOCKET_PATH" && -S "$DOCKER_SOCKET_PATH" ]]; then
        run_args+=(-v "$DOCKER_SOCKET_PATH:/var/run/docker.sock")
    fi
fi

docker build -t "$IMAGE_NAME" "$SCRIPT_DIR"
prepare_state_volume "$IMAGE_NAME" "$STATE_VOLUME" "$CONTAINER_USER"
docker run "${run_args[@]}" "$IMAGE_NAME" "$@"
