# shellcheck shell=bash
# Points every npm-ecosystem package manager at the approved dotnet-public-npm feed.
#
# Source this (do not execute it) from any polyglot validation script that acquires npm packages,
# directly or indirectly, before the first acquisition happens:
#
#   source "$(dirname "${BASH_SOURCE[0]}")/npm-registry-env.sh"
#
# It lives in its own file rather than inline in one script because the guarantee is a property of
# the whole polyglot job, not of a single script. A new validation script that installs packages
# needs one `source` line instead of re-deriving which environment variable each package manager
# reads, which is the kind of detail that is easy to get subtly wrong.
#
# ---------------------------------------------------------------------------------------------
# Why the environment, and not a config file
#
# The repository-root .npmrc does not reach the AppHosts under tests/PolyglotAppHosts. npm resolves
# project config from `localPrefix`, the nearest ancestor directory containing package.json or
# node_modules, which for every AppHost is the AppHost directory itself. npm therefore reads that
# directory's (non-existent) .npmrc and falls back to the public registry. Environment variables
# outrank project config, so exporting them is what actually reaches the AppHosts.
#
# Committed lockfiles pin absolute tarball URLs and cover most AppHosts, but two shapes have no such
# protection: an AppHost with no lockfile has to resolve everything remotely, and Yarn Berry
# lockfiles record `resolution: "ms@npm:2.1.3"` locators with no host in them, so Berry re-resolves
# through whatever registry is configured.
#
# The knobs are not interchangeable, so each manager gets the one it actually reads:
#   npm, pnpm  npm_config_registry - npm's environment form of an .npmrc key, and pnpm honors it.
#   bun        BUN_CONFIG_REGISTRY - bun also reads npm_config_registry, so both are set.
#   Yarn Berry YARN_NPM_REGISTRY_SERVER - Berry ignores .npmrc and npm_config_registry entirely. It
#              maps YARN_<SCREAMING_SNAKE> onto the .yarnrc.yml setting of the same name, and it has
#              no --registry flag, so the environment is the only lever. Without this, Berry uses its
#              built-in default of https://registry.yarnpkg.com. See
#              https://yarnpkg.com/configuration/yarnrc#npmRegistryServer.
#   corepack   COREPACK_NPM_REGISTRY - AppHosts declare `packageManager`, so if corepack shims are
#              active it fetches the pinned manager itself before any install begins.

#   npm scopes  NPM_CONFIG_USERCONFIG / NPM_CONFIG_GLOBALCONFIG - see below.

APPROVED_NPM_REGISTRY="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/"

# The preflight's authority has to be independent of NPM_REGISTRY. If NPM_REGISTRY itself were
# accepted as the expected value, a caller could set it to the public registry and every manager
# check below would only prove the bad value propagated.
if [ -n "${NPM_REGISTRY:-}" ] && [ "${NPM_REGISTRY%/}" != "${APPROVED_NPM_REGISTRY%/}" ]; then
    echo "❌ NPM_REGISTRY override '${NPM_REGISTRY}' is not the approved feed '${APPROVED_NPM_REGISTRY}'."
    echo "   Refusing to install packages that would come from an unapproved registry."
    exit 1
fi

NPM_REGISTRY="$APPROVED_NPM_REGISTRY"

export NPM_REGISTRY
export npm_config_registry="$NPM_REGISTRY"
export NPM_CONFIG_REGISTRY="$NPM_REGISTRY"
export BUN_CONFIG_REGISTRY="$NPM_REGISTRY"
export YARN_NPM_REGISTRY_SERVER="$NPM_REGISTRY"
export COREPACK_NPM_REGISTRY="$NPM_REGISTRY"

# ---------------------------------------------------------------------------------------------
# Why the default registry alone is not enough
#
# `registry` sets the default only. A per-scope `@scope:registry` key is a separate setting that
# always wins for that scope, and neither npm_config_registry nor `npm --registry` overrides it.
# Measured with npm 11.4.2, a user-level `@types:registry=https://scoped.example.invalid/` and
# npm_config_registry pointing at the approved feed:
#
#   npm config get registry        -> https://pkgs.dev.azure.com/.../npm/registry/
#   npm config get @types:registry -> https://scoped.example.invalid/
#   npm install @types/node        -> request to https://scoped.example.invalid/@types%2fnode
#
# The AppHosts install scoped packages (@types/*, @esbuild/*), so an ambient user- or global-level
# scoped key in the image would silently redirect exactly the packages the guard exists to protect.
# Point both config paths at files this script owns so no ambient scoped key can apply.
NPM_REGISTRY_CONFIG_DIR="$(mktemp -d)"
printf 'registry=%s\n' "$NPM_REGISTRY" > "$NPM_REGISTRY_CONFIG_DIR/npmrc"
: > "$NPM_REGISTRY_CONFIG_DIR/globalrc"
export NPM_CONFIG_USERCONFIG="$NPM_REGISTRY_CONFIG_DIR/npmrc"
export NPM_CONFIG_GLOBALCONFIG="$NPM_REGISTRY_CONFIG_DIR/globalrc"

# Trailing slashes are not significant to any of these managers, and they do not all echo the value
# back verbatim, so compare against a single normalized form.
check_manager_registry() {
    local manager="$1"
    local reported="$2"

    if [ "${reported%/}" != "${APPROVED_NPM_REGISTRY%/}" ]; then
        echo "  ❌ $manager resolves packages from '${reported:-<unset>}' instead of the approved feed '$APPROVED_NPM_REGISTRY'"
        return 1
    fi

    echo "  ✅ $manager -> $reported"
    return 0
}

# Every query below runs from NPM_REGISTRY_CONFIG_DIR rather than the caller's directory. These
# managers refuse to answer inside a project claimed by a different one — from an AppHost whose
# package.json says `"packageManager": "yarn@4.14.1"`, `pnpm config get registry` exits 1 with
# "This project is configured to use yarn" and prints nothing. Reading that empty output as a
# registry value would fail the job for a project that is configured correctly, so ask in a
# directory that belongs to no project and the answer depends only on the environment.
#
# What this deliberately does not cover is a per-project .npmrc or .yarnrc.yml, which is invisible
# from here. That is asserted statically instead, by
# NpmLockfileRegistryTests.PolyglotFixtures_DoNotOverrideTheRegistry.
config_in_neutral_directory() {
    (cd "$NPM_REGISTRY_CONFIG_DIR" && "$@" 2>/dev/null) || true
}

# Exporting the variables above is not proof that they took effect. A package manager bump could
# rename a setting, or an image could bake in a conflicting config with higher precedence, and the
# installs would quietly fall back to the public registry with the job still green. Ask each manager
# what it actually resolved so that drift fails loudly here, before anything is downloaded.
#
# Bun has no config-read command, so it is covered by the two environment variables above rather than
# by an assertion.
# A scoped key that survives the config paths above — from a project .npmrc, or from a source a
# future npm adds — would redirect only the scoped packages, which is the easiest form of this drift
# to miss. Enumerate every "@scope:registry" npm reports and require each to be the approved feed.
#
# `npm config list` prints one setting per line, quoted:
#   @types:registry = "https://scoped.example.invalid/"
#   registry = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/"
# Comment lines beginning with ';' name the file each block came from and are skipped by the match.
check_scoped_registries() {
    local failed=0
    local scope reported line

    while IFS= read -r line; do
        scope="${line%%:registry*}"
        reported="${line#*= }"
        reported="${reported%\"}"
        reported="${reported#\"}"

        if [ "${reported%/}" != "${APPROVED_NPM_REGISTRY%/}" ]; then
            echo "  ❌ npm resolves $scope packages from '$reported' instead of the approved feed '$APPROVED_NPM_REGISTRY'"
            failed=1
        else
            echo "  ✅ npm $scope -> $reported"
        fi
    done < <(config_in_neutral_directory npm config list | grep -E '^@[^:]+:registry = ' || true)

    return "$failed"
}

verify_registry_configuration() {
    local failed=0
    local yarn_version

    echo "Package registry configuration:"

    if command -v npm &> /dev/null; then
        check_manager_registry "npm" "$(config_in_neutral_directory npm config get registry)" || failed=1
        check_scoped_registries || failed=1
    fi

    if command -v pnpm &> /dev/null; then
        check_manager_registry "pnpm" "$(config_in_neutral_directory pnpm config get registry)" || failed=1
    fi

    if command -v yarn &> /dev/null; then
        # Yarn Classic does not understand npmRegistryServer and prints "undefined" for it, while
        # still exiting 0. It also ignores npm_config_registry, so there is no way to point it at the
        # approved feed from the environment. A Classic binary on PATH would still be used to install
        # a Berry AppHost, so surface it here instead of letting it reach the public registry.
        #
        # The version query has to run in the same neutral directory as the config query, because
        # which yarn answers depends on the working directory: the launcher honours the nearest
        # package.json "packageManager" field, so from a fixture pinned to yarn@4.14.1 `yarn
        # --version` reports 4.14.1 while the same command one directory up reports the globally
        # installed 1.22.22. Asking in two different directories lets a Classic binary pass the
        # version gate and then answer "undefined" to the config query, which reports a registry
        # mismatch when the real problem is the yarn version.
        yarn_version="$(config_in_neutral_directory yarn --version)"

        if [[ "$yarn_version" == 1.* ]]; then
            echo "  ❌ yarn is Yarn Classic ($yarn_version), which cannot be pointed at the approved feed. Install Yarn 4 or later."
            failed=1
        else
            check_manager_registry "yarn" "$(config_in_neutral_directory yarn config get npmRegistryServer)" || failed=1
        fi
    fi

    if [ "$failed" -ne 0 ]; then
        echo "❌ Refusing to install: packages would be downloaded from outside the approved feed."
        exit 1
    fi
}

verify_registry_configuration
