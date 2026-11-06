#!/usr/bin/env bash
set -euo pipefail

build_with_mirror_retry() {
    local primary_timeout="$1"
    local display_name="$2"
    shift 2
    local status

    echo "Building $display_name with Azure Ubuntu apt mirror (limit: $primary_timeout)"
    # Do not use --foreground: timeout must signal the entire client process group.
    # TERM lets Docker cancel its BuildKit session before default-source fallback.
    # Normalize external signals in a supervising shell: Docker may handle TERM
    # and exit 1, which otherwise looks like an ordinary build failure to timeout.
    # On a deadline timeout still returns 124; external cancellation returns 143/130.
    if timeout --verbose --signal=TERM --kill-after=30s "$primary_timeout" \
        bash -c '
            cancel() {
                trap "" INT TERM
                if [[ -n "${client_pid:-}" ]]; then
                    wait "$client_pid"
                fi
                exit "$1"
            }
            trap "cancel 143" TERM
            trap "cancel 130" INT
            "$@" &
            client_pid=$!
            wait "$client_pid"
        ' bash "$@" --build-arg "UBUNTU_APT_MIRROR=http://azure.archive.ubuntu.com/ubuntu/" .; then
        return 0
    else
        status=$?
    fi

    case "$status" in
        124)
            echo "$display_name primary mirror build exceeded $primary_timeout; retrying with default Ubuntu apt sources" >&2
            ;;
        125|126|127|130|137|143)
            # A forced kill cannot confirm BuildKit acknowledged cancellation. Do
            # not overlap another build or turn workflow cancellation into a retry.
            echo "$display_name primary build stopped with exit code $status; not retrying after cancellation or timeout execution failure" >&2
            return "$status"
            ;;
        *)
            echo "$display_name build failed with the Azure Ubuntu apt mirror (exit code $status); retrying with default Ubuntu apt sources" >&2
            ;;
    esac

    "$@" .
}

build_cli_e2e_images() {
    # Four primary attempts can spend at most 20m plus 2m cancellation grace,
    # leaving 23m of the 45m job for default sources, setup and artifact handling.
    # Throttled apt downloads have otherwise consumed 38-41m in a single image.
    local primary_timeout=5m
    local dotnet_cache_scope=cli-e2e-dotnet
    local polyglot_cache_scope=cli-e2e-polyglot-base

    docker buildx create --name cli-e2e-builder --use
    docker buildx inspect --bootstrap

    build_with_mirror_retry "$primary_timeout" ".NET image" docker buildx build \
        --load \
        --cache-from "type=gha,scope=$dotnet_cache_scope" \
        --cache-to "type=gha,scope=$dotnet_cache_scope,mode=max,ignore-error=true" \
        --build-arg SKIP_SOURCE_BUILD=true \
        -f tests/Shared/Docker/Dockerfile.e2e \
        -t "$CLI_E2E_DOTNET_IMAGE_TAG"

    if [[ "$CLI_E2E_INCLUDE_POLYGLOT_IMAGES" == "true" ]]; then
        build_with_mirror_retry "$primary_timeout" "polyglot base image" docker buildx build \
            --load \
            --cache-from "type=gha,scope=$polyglot_cache_scope" \
            --cache-to "type=gha,scope=$polyglot_cache_scope,mode=max,ignore-error=true" \
            --build-arg SKIP_SOURCE_BUILD=true \
            -f tests/Shared/Docker/Dockerfile.e2e-polyglot-base \
            -t "$CLI_E2E_POLYGLOT_BASE_IMAGE_TAG"
        docker tag "$CLI_E2E_POLYGLOT_BASE_IMAGE_TAG" "$CLI_E2E_POLYGLOT_IMAGE_TAG"

        DOCKER_BUILDKIT=1 build_with_mirror_retry "$primary_timeout" "Java polyglot image" docker build \
            -f tests/Shared/Docker/Dockerfile.e2e-polyglot-java \
            -t "$CLI_E2E_POLYGLOT_JAVA_IMAGE_TAG"
        DOCKER_BUILDKIT=1 build_with_mirror_retry "$primary_timeout" "Go polyglot image" docker build \
            -f tests/Shared/Docker/Dockerfile.e2e-polyglot-go \
            -t "$CLI_E2E_POLYGLOT_GO_IMAGE_TAG"
    fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    build_cli_e2e_images
fi
