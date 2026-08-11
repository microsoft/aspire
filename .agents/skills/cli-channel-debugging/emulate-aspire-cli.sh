#!/usr/bin/env bash
#
# emulate-aspire-cli.sh — SOURCE this to make the current shell drive a locally built
# Aspire CLI that emulates a given build identity. It exports the ASPIRE_CLI_* identity
# overrides and defines an `aspire` function pointing at this repo's freshly built CLI.
#
# This is the one-liner that powers the per-scenario "Terminal" canvases described in
# this skill's SKILL.md — open a terminal canvas, then source this with the scenario.
#
#   source .agents/skills/cli-channel-debugging/emulate-aspire-cli.sh <channel> [options]
#
# Channels:
#   stable | daily            version auto-resolved from the matching feed (override with --version)
#   staging                   requires --commit; version auto-resolved from the darc feed
#   pr-<N> | local            version NOT auto-resolved; pass --version to set one
#
# Options:
#   --version <v>     Identity version (ASPIRE_CLI_VERSION). Auto-resolved for stable/daily/staging.
#   --commit <sha>    Identity commit (ASPIRE_CLI_COMMIT). Required for staging.
#   --packages <dir>  Point Aspire* package resolution at a flat .nupkg dir (ASPIRE_CLI_PACKAGES).
#   --config <cfg>    Build/use this configuration (Debug|Release). Default: Debug.
#   --no-build        Do not build; require an existing CLI binary.
#
# After sourcing, just run `aspire <command>` (e.g. `aspire --version`, `aspire new`).
# Every invocation prints the yellow emulation banner on stderr.

# NOTE: this file is meant to be sourced, so it must never call `exit` or rely on `set -e`
# (either would tear down the caller's interactive shell). All failure paths `return`.

__aspire_emulate_main() {
    local channel="" version="" commit="" packages="" config="Debug" no_build="false"

    # Resolve this script's own directory whether sourced from bash or zsh.
    local src=""
    if [ -n "${BASH_SOURCE:-}" ]; then
        src="${BASH_SOURCE[0]}"
    elif [ -n "${ZSH_VERSION:-}" ]; then
        # zsh-only expansion; eval keeps bash from trying to parse it.
        src="$(eval 'printf "%s" "${(%):-%x}"')"
    else
        src="$0"
    fi
    local skill_dir
    skill_dir="$(cd "$(dirname "$src")" >/dev/null 2>&1 && pwd)"
    local repo_root
    repo_root="$(cd "$skill_dir" >/dev/null 2>&1 && git rev-parse --show-toplevel 2>/dev/null)"
    if [ -z "$repo_root" ]; then
        echo "emulate-aspire-cli: could not locate the repository root from $skill_dir" >&2
        return 1
    fi

    if [ "$#" -eq 0 ]; then
        echo "emulate-aspire-cli: a channel is required (stable | daily | staging | pr-<N> | local)" >&2
        return 1
    fi
    channel="$1"; shift
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --version) version="${2:-}"; shift 2 ;;
            --commit) commit="${2:-}"; shift 2 ;;
            --packages) packages="${2:-}"; shift 2 ;;
            --config) config="${2:-Debug}"; shift 2 ;;
            --no-build) no_build="true"; shift ;;
            *) echo "emulate-aspire-cli: unrecognized option: $1" >&2; return 1 ;;
        esac
    done

    # Auto-resolve the version for feed-backed channels unless the caller pinned one.
    if [ -z "$version" ]; then
        case "$channel" in
            stable|daily|staging)
                local resolver="$skill_dir/get-aspire-channel-version.sh"
                local resolver_args=("$channel")
                if [ "$channel" = "staging" ]; then
                    if [ -z "$commit" ]; then
                        echo "emulate-aspire-cli: staging requires --commit <sha> (see docs/cli-staging-validation.md)" >&2
                        return 1
                    fi
                    resolver_args+=("--commit" "$commit")
                fi
                echo "emulate-aspire-cli: resolving latest '$channel' version..." >&2
                version="$(bash "$resolver" "${resolver_args[@]}")" || {
                    echo "emulate-aspire-cli: failed to resolve a version for '$channel'" >&2
                    return 1
                }
                ;;
        esac
    fi

    # Build (or locate) the CLI. The override only changes identity/decisions — it never
    # materializes package bytes — so a normal local build of the CLI is all we need.
    local cli_dll="$repo_root/artifacts/bin/Aspire.Cli/$config/net10.0/aspire.dll"
    if [ "$no_build" != "true" ]; then
        echo "emulate-aspire-cli: building Aspire.Cli ($config)..." >&2
        ( cd "$repo_root" && MSBUILDTERMINALLOGGER=false dotnet build src/Aspire.Cli/Aspire.Cli.csproj \
            -c "$config" -p:SkipNativeBuild=true -clp:ErrorsOnly ) || {
            echo "emulate-aspire-cli: build failed" >&2
            return 1
        }
    fi
    if [ ! -f "$cli_dll" ]; then
        echo "emulate-aspire-cli: CLI binary not found at $cli_dll (drop --no-build to build it)" >&2
        return 1
    fi

    export ASPIRE_CLI_CHANNEL="$channel"
    if [ -n "$version" ]; then export ASPIRE_CLI_VERSION="$version"; else unset ASPIRE_CLI_VERSION; fi
    if [ -n "$commit" ]; then export ASPIRE_CLI_COMMIT="$commit"; else unset ASPIRE_CLI_COMMIT; fi
    if [ -n "$packages" ]; then export ASPIRE_CLI_PACKAGES="$packages"; else unset ASPIRE_CLI_PACKAGES; fi

    # Define the `aspire` shell function for this session.
    eval 'aspire() { dotnet "'"$cli_dll"'" "$@"; }'

    echo "emulate-aspire-cli: ready — channel=$ASPIRE_CLI_CHANNEL version=${ASPIRE_CLI_VERSION:-<unset>}${ASPIRE_CLI_COMMIT:+ commit=$ASPIRE_CLI_COMMIT}${ASPIRE_CLI_PACKAGES:+ packages=$ASPIRE_CLI_PACKAGES}" >&2
    echo "emulate-aspire-cli: run 'aspire --version' to confirm the emulation banner." >&2
}

__aspire_emulate_main "$@"
unset -f __aspire_emulate_main 2>/dev/null || true
