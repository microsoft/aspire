#!/usr/bin/env bash

# Shared implementation for debug-staging.sh and debug-stable.sh.
#
# Purpose
# -------
# Make an EASY-TO-GET Aspire CLI build behave like an official release-branch
# **staging** build for the purpose of validating package feed routing, WITHOUT
# having to produce a real official build or stamp a binary locally.
#
# The recommended carrier is a PR build (a real, full self-extracting `~/.aspire`
# install) acquired with `eng/scripts/get-aspire-cli-pr.sh <PR>`. Any installed
# `aspire` (or a locally built one via `--cli`) works just as well, because the
# behavior is driven entirely by two diagnostic config overrides read by
# `PackagingService` (see docs/cli-staging-validation.md):
#
#   overrideCliIdentityChannel       - forces the identity used for staging-feed
#                                      routing decisions (here: `staging`).
#   overrideCliInformationalVersion  - forces the informational version the SHA
#                                      derivation and version-shape (quality)
#                                      checks read, e.g. `13.4.0-preview.1.x+<sha>`.
#
# Both flow into IConfiguration from environment variables (used here) OR from
# aspire.config.json, and are scoped to staging feed routing only -- they do NOT
# change the global identity used for hive/packages directory lookups. A CLI run
# with them set emits a one-time warning so they can never silently mis-route a
# normal invocation.
#
# What this script asserts
# ------------------------
# It runs `aspire add <package> --debug` in a throwaway directory whose
# aspire.config.json pins `channel: staging`, then asserts the debug log contains
#   Resolved 'staging' channel: feed=<expected darc feed>, quality=<expected>
# where the feed is the SHA-specific darc-pub-microsoft-aspire-<first8-of-sha>
# feed. The `aspire add` step is expected to fail later (there is no real apphost
# project in the scratch directory); only the feed-routing log line is validated.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Default version shapes for the 13.4 release branch. Override with --version.
readonly DEFAULT_STAGING_VERSION="13.4.0-preview.1.26280.6"
readonly DEFAULT_STABLE_VERSION="13.4.0"

say() { printf '%s\n' "$*"; }
say_err() { printf 'error: %s\n' "$*" >&2; }

print_usage() {
    local invoked_as="$1"
    cat <<USAGE
Usage: ${invoked_as} --sha <commit> [options] [-- <aspire args>]

Simulates an official ${KIND_LABEL} build and validates that the CLI resolves
Aspire.* packages from the SHA-specific darc feed for <commit>.

Required:
  --sha <commit>        Commit hash of the darc feed to target (>= 8 hex chars).
                        Use a real release-branch build commit if you intend to
                        actually restore packages; any 8+ hex value is fine for
                        inspecting/asserting the resolved feed URL.

Options:
  --pr <number>         Install that PR's build first (via get-aspire-cli-pr.sh)
                        and target it. Omit to use an already-installed CLI.
  --cli <path>          Path to the aspire CLI to drive. Default: 'aspire' on
                        PATH, else ~/.aspire/bin/aspire.
  --version <version>   Override the informational version (without +<sha>).
                        Default for ${KIND_LABEL}: ${DEFAULT_VERSION}.
  --identity <channel>  Override the identity used for staging-feed routing.
                        Default: staging.
  --package <name>      Package to use for the validation 'aspire add'.
                        Default: foundry.
  --shell               Instead of the one-shot validation, drop into an
                        interactive subshell where the target CLI behaves like
                        this ${KIND_LABEL} build for EVERY 'aspire' command
                        (aspire new, add, run, ...). The overrides are exported
                        only into that subshell; they vanish when you exit it.
  --print-env           Print the 'export' lines for this build to stdout so you
                        can apply them to your current shell, e.g.
                          eval "\$(${invoked_as} --sha <commit> --print-env)"
                        Every 'aspire' command in that shell then behaves like
                        this build until you 'unset' the variables (printed too).
  -h, --help            Show this help.

Anything after '--' is passed through to the aspire invocation.

Examples:
  ${invoked_as} --sha 1a2b3c4d5e6f7a8b
  ${invoked_as} --pr 17743 --sha 1a2b3c4d5e6f7a8b
  ${invoked_as} --cli ./artifacts/bin/Aspire.Cli/Debug/net10.0/aspire --sha 1a2b3c4d
  # Install a PR build and explore it interactively as a staging build:
  ${invoked_as} --pr 17743 --sha 1a2b3c4d5e6f7a8b --shell
USAGE
}

# run_debug_channel <kind> <invoked-as> [args...]
#   kind: "staging" (prerelease-shaped) | "stable" (stable-shaped)
run_debug_channel() {
    local kind="$1"; shift
    local invoked_as="$1"; shift

    case "$kind" in
        staging) KIND_LABEL="staging (prerelease-shaped)"; DEFAULT_VERSION="$DEFAULT_STAGING_VERSION"; EXPECTED_QUALITY="Both" ;;
        stable)  KIND_LABEL="staging (stable-shaped)";    DEFAULT_VERSION="$DEFAULT_STABLE_VERSION";  EXPECTED_QUALITY="Stable" ;;
        *) say_err "unknown kind '$kind'"; return 2 ;;
    esac

    local sha="" pr="" cli_path="" version="" identity="staging" package="foundry"
    local mode="validate"
    local -a passthrough=()

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --sha) sha="${2:-}"; shift 2 ;;
            --pr) pr="${2:-}"; shift 2 ;;
            --cli) cli_path="${2:-}"; shift 2 ;;
            --version) version="${2:-}"; shift 2 ;;
            --identity) identity="${2:-}"; shift 2 ;;
            --package) package="${2:-}"; shift 2 ;;
            --shell) mode="shell"; shift ;;
            --print-env) mode="printenv"; shift ;;
            -h|--help) print_usage "$invoked_as"; return 0 ;;
            --) shift; passthrough=("$@"); break ;;
            *) say_err "unknown argument '$1'"; print_usage "$invoked_as" >&2; return 2 ;;
        esac
    done

    if [[ -z "$sha" ]]; then
        say_err "--sha is required."
        print_usage "$invoked_as" >&2
        return 2
    fi

    # The darc feed name is built from the first 8 chars of the commit hash, so
    # require at least that many hex characters (full hashes are accepted).
    if [[ ! "$sha" =~ ^[0-9a-fA-F]{8,40}$ ]]; then
        say_err "--sha must be 8-40 hexadecimal characters (got '$sha')."
        return 2
    fi

    [[ -n "$version" ]] || version="$DEFAULT_VERSION"

    # Lowercase the first 8 chars to match how PackagingService derives the feed.
    local sha8
    sha8="$(printf '%s' "${sha:0:8}" | tr '[:upper:]' '[:lower:]')"
    local expected_feed="https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-${sha8}/nuget/v3/index.json"
    local info_version="${version}+${sha}"

    # --print-env: emit shell-applicable export/unset lines and stop. This mode is
    # CLI-agnostic on purpose -- the three keys drive ANY 'aspire' on PATH, so the
    # developer can install/upgrade the carrier build independently. Intended use:
    #   eval "$(debug-staging.sh --sha <commit> --print-env)"
    if [[ "$mode" == "printenv" ]]; then
        cat <<ENV
# ${KIND_LABEL} build (sha ${sha8}, feed darc-pub-microsoft-aspire-${sha8}, quality ${EXPECTED_QUALITY}).
# Apply to your current shell:   eval "\$(${invoked_as} --sha ${sha} --print-env)"
# Every 'aspire' command then behaves like this build. Undo with the 'unset' lines below.
export channel='staging'
export overrideCliIdentityChannel='${identity}'
export overrideCliInformationalVersion='${info_version}'
# To revert:
# unset channel overrideCliIdentityChannel overrideCliInformationalVersion
ENV
        return 0
    fi

    # Optionally install the PR build first; it becomes the default target CLI.
    if [[ -n "$pr" ]]; then
        say ">> Installing PR #${pr} build via get-aspire-cli-pr.sh ..."
        "${SCRIPT_DIR}/get-aspire-cli-pr.sh" "$pr"
    fi

    if [[ -z "$cli_path" ]]; then
        if command -v aspire >/dev/null 2>&1; then
            cli_path="$(command -v aspire)"
        elif [[ -x "$HOME/.aspire/bin/aspire" ]]; then
            cli_path="$HOME/.aspire/bin/aspire"
        else
            say_err "No aspire CLI found. Install a PR build (--pr <N>), pass --cli <path>, or put 'aspire' on PATH."
            return 1
        fi
    fi
    if [[ ! -x "$cli_path" ]]; then
        say_err "CLI path '$cli_path' is not executable."
        return 1
    fi
    # Resolve to an absolute path because the validation step runs from a scratch
    # working directory, where a relative --cli would no longer resolve.
    case "$cli_path" in
        /*) : ;;
        *) cli_path="$(cd "$(dirname "$cli_path")" && pwd)/$(basename "$cli_path")" ;;
    esac

    say ""
    say "Simulating an official ${KIND_LABEL} build"
    say "  CLI:               $cli_path"
    say "  identity override: $identity"
    say "  version override:  $info_version"
    say "  expected feed:     $expected_feed"
    say "  expected quality:  $EXPECTED_QUALITY"
    say ""

    # --shell: drop into an interactive subshell where the target CLI behaves like
    # this build for EVERY 'aspire' command. The overrides live only in this child
    # shell's environment, so exiting it fully restores normal behavior -- nothing
    # is written to global/aspire.config.json. The target CLI's directory is put
    # first on PATH so a bare 'aspire' resolves to it (handles --cli pointing at a
    # local build as well as an installed PR build).
    if [[ "$mode" == "shell" ]]; then
        local cli_dir
        cli_dir="$(dirname "$cli_path")"
        # Redirect NuGet's global packages folder to an isolated, per-sha directory
        # so packages restored from the simulated staging feed (which can collide in
        # version with packages already cached from real feeds) never contaminate the
        # developer's real global cache (~/.nuget/packages by default). The directory
        # is keyed by the simulated sha so repeat sessions reuse the same isolated
        # cache, and is left in place on exit (it lives under the system temp dir).
        local nuget_packages="${TMPDIR:-/tmp}/aspire-debug-nuget/${sha8}"
        mkdir -p "$nuget_packages"
        say ">> Launching an interactive subshell. Run 'aspire new', 'aspire add', etc."
        say "   'aspire' resolves to: $cli_path"
        say "   NuGet packages cache: $nuget_packages (isolated from your global cache)"
        say "   Type 'exit' to leave and restore normal CLI behavior."
        say ""
        channel="staging" \
            overrideCliIdentityChannel="$identity" \
            overrideCliInformationalVersion="$info_version" \
            NUGET_PACKAGES="$nuget_packages" \
            PATH="${cli_dir}:${PATH}" \
            ASPIRE_DEBUG_BUILD_PROMPT="aspire(${kind}:${sha8})" \
            "${SHELL:-/bin/bash}" -i
        return $?
    fi

    # Throwaway working directory pinned to channel: staging so 'aspire add'
    # filters to the synthesized staging channel. Created securely; cleaned up
    # on exit. No real apphost project lives here, so 'add' will ultimately fail
    # after feed routing has already been logged -- that is expected.
    local scratch
    scratch="$(mktemp -d)"
    trap 'rm -rf "$scratch"' RETURN
    cat > "${scratch}/aspire.config.json" <<JSON
{
  "channel": "staging"
}
JSON

    local log="${scratch}/aspire-debug.log"
    say ">> Running: aspire add ${package} --debug ${passthrough[*]:-}"
    say "   (feed routing is logged before the add step fails on the missing apphost)"
    say ""

    # The overrides are scoped to THIS invocation only (no export, no persisted
    # config), so they can't leak into the developer's other aspire commands.
    # 'aspire add' is allowed to exit non-zero; success is decided by the log.
    set +e
    ( cd "$scratch" && \
        overrideCliIdentityChannel="$identity" \
        overrideCliInformationalVersion="$info_version" \
        "$cli_path" add "$package" --debug "${passthrough[@]+"${passthrough[@]}"}" ) > "$log" 2>&1
    set -e

    # Echo the resolution + override-warning lines for visibility.
    grep -E "diagnostic overrides are active|Resolved 'staging' channel|Refusing to synthesize|Could not synthesize" "$log" || true
    say ""

    local resolved_line
    resolved_line="$(grep -F "Resolved 'staging' channel: feed=${expected_feed}" "$log" || true)"
    if [[ -z "$resolved_line" ]]; then
        say_err "FAILED: did not resolve the expected darc feed."
        say_err "Expected: Resolved 'staging' channel: feed=${expected_feed}, quality=${EXPECTED_QUALITY}"
        say_err "See full debug log for details:"
        sed 's/^/    /' "$log" >&2
        return 1
    fi

    if [[ "$resolved_line" != *"quality=${EXPECTED_QUALITY}"* ]]; then
        say_err "FAILED: resolved the darc feed but quality was not '${EXPECTED_QUALITY}'."
        say_err "  $resolved_line"
        return 1
    fi

    say "PASSED: ${KIND_LABEL} build resolves Aspire.* from the darc feed with quality=${EXPECTED_QUALITY}."
    say ""
    say "Equivalent persistent 'config options' (drop into the apphost's aspire.config.json"
    say "to simulate this build interactively with an installed PR build):"
    say ""
    cat <<JSON
{
  "channel": "staging",
  "overrideCliIdentityChannel": "${identity}",
  "overrideCliInformationalVersion": "${info_version}"
}
JSON
    say ""
    say "Remove those override keys when you are done -- they are for local validation only."
}
