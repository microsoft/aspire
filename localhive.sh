#!/usr/bin/env bash

# Build local NuGet packages, Aspire CLI, and bundle, then create/update a hive and install everything.
#
# Usage:
#   ./localhive.sh [options]
#   ./localhive.sh [Release|Debug] [HiveName]
#
# Options:
#   -c, --configuration   Build configuration: Release or Debug
#   -n, --name            Hive name (default: local)
#   -o, --output          Output directory for portable layout (instead of $HOME/.aspire)
#   -r, --rid             Target RID for cross-platform builds (e.g. linux-x64)
#   -v, --versionsuffix   Prerelease version suffix (default: auto-generates local.YYYYMMDD.tHHmmss)
#       --version         Build a STABLE-shaped version X.Y.Z (no prerelease suffix), e.g. 13.5.0.
#                         Mutually exclusive with --versionsuffix. Use this to emulate a future
#                         released build entirely from local packages.
#       --archive         Create a .tar.gz (or .zip for win-* RIDs) archive of the output. Requires --output.
#       --copy            Copy .nupkg files instead of creating a symlink
#       --skip-cli        Skip installing the locally-built CLI to $HOME/.aspire/bin
#       --skip-bundle     Skip building the bundle payload (CLI won't have embedded bundle)
#       --native-aot      Build native AOT CLI (self-extracting with embedded bundle)
#   -h, --help            Show this help and exit
#
# Notes:
# - If no configuration is specified, the script tries Release then Debug.
# - The hive is created at $HOME/.aspire/hives/<HiveName> so the Aspire CLI can discover a channel.
# - The CLI is installed to $HOME/.aspire/bin so it can be used directly.
# - The bundle payload is embedded in the CLI binary and self-extracts on first run.

set -euo pipefail

print_usage() {
  cat <<EOF
Usage:
  ./localhive.sh [options]
  ./localhive.sh [Release|Debug] [HiveName]

Options:
  -c, --configuration   Build configuration: Release or Debug
  -n, --name            Hive name (default: local)
  -o, --output          Output directory for portable layout (instead of \$HOME/.aspire)
  -r, --rid             Target RID for cross-platform builds (e.g. linux-x64)
  -v, --versionsuffix   Prerelease version suffix (default: auto-generates local.YYYYMMDD.tHHmmss)
      --version         Build a STABLE-shaped version X.Y.Z (no prerelease suffix), e.g. 13.5.0.
                        Mutually exclusive with --versionsuffix.
      --archive         Create a .tar.gz (or .zip for win-* RIDs) archive of the output. Requires --output.
      --copy            Copy .nupkg files instead of creating a symlink
      --skip-cli        Skip installing the locally-built CLI to \$HOME/.aspire/bin
      --skip-bundle     Skip building the bundle payload (CLI won't have embedded bundle)
      --native-aot      Build native AOT CLI (self-extracting with embedded bundle)
  -h, --help            Show this help and exit

Examples:
  ./localhive.sh -c Release -n local
  ./localhive.sh Debug my-feature
  ./localhive.sh -c Release -n demo -v local.20250811.t033324
  ./localhive.sh --skip-cli
  ./localhive.sh -o /tmp/aspire-linux -r linux-x64 --archive   # Portable archive for a Linux machine
  ./localhive.sh --version 13.5.0 -o /tmp/aspire-stable -r linux-arm64 --archive   # Stable-shaped local "release" build

This will pack NuGet packages into artifacts/packages/<Config>/Shipping and create/update
a hive at \$HOME/.aspire/hives/<HiveName> so the Aspire CLI can use it as a channel.
It also installs the locally-built CLI to \$HOME/.aspire/bin (unless --skip-cli is specified).
EOF
}

log()   { echo "[localhive] $*"; }
warn()  { echo "[localhive] Warning: $*" >&2; }
error() { echo "[localhive] Error: $*" >&2; }

if [ -z "${ZSH_VERSION:-}" ]; then
  source="${BASH_SOURCE[0]}"
  # resolve $SOURCE until the file is no longer a symlink
  while [[ -h $source ]]; do
    scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"
    source="$(readlink "$source")"
    [[ $source != /* ]] && source="$scriptroot/$source"
  done
  scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"
else
  # :A resolves symlinks, :h truncates to directory
  scriptroot=${0:A:h}
fi

REPO_ROOT=$(cd "${scriptroot}"; pwd)

CONFIG=""
HIVE_NAME="local"
USE_COPY=0
SKIP_CLI=0
SKIP_BUNDLE=0
NATIVE_AOT=0
VERSION_SUFFIX=""
STABLE_VERSION=""
OUTPUT_DIR=""
TARGET_RID=""
ARCHIVE=0
BUNDLE_PAYLOAD_ARCHIVE=""
is_valid_versionsuffix() {
  local s="$1"
  # Must be dot-separated identifiers containing only 0-9A-Za-z- per SemVer2.
  if [[ ! "$s" =~ ^[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*$ ]]; then
    return 1
  fi
  # Numeric identifiers must not have leading zeros.
  IFS='.' read -r -a parts <<< "$s"
  for part in "${parts[@]}"; do
    if [[ "$part" =~ ^[0-9]+$ ]] && [[ ${#part} -gt 1 ]] && [[ "$part" == 0* ]]; then
      return 1
    fi
  done
  return 0
}

# A stable-shaped release version is a strict X.Y.Z triple with no prerelease/build metadata.
is_valid_stableversion() {
  local s="$1"
  [[ "$s" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
}

# Restrict hive names to a safe identifier set: this value is concatenated
# into $HIVES_ROOT/$HIVE_NAME and then passed to `rm -rf`, so any path
# separator, leading dot, or `..` segment would let the removal target
# escape the hives directory and delete arbitrary parent paths.
is_valid_hivename() {
  local s="$1"
  if [[ -z "$s" ]]; then
    return 1
  fi
  if [[ ! "$s" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
    return 1
  fi
  if [[ "$s" == *".."* ]]; then
    return 1
  fi
  return 0
}


# Parse flags and positional fallbacks
while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help)
      print_usage
      exit 0
      ;;
    -c|--configuration)
      if [[ $# -lt 2 ]]; then error "Missing value for $1"; exit 1; fi
      CONFIG="$2"; shift 2 ;;
    -n|--name|--hive|--hive-name)
      if [[ $# -lt 2 ]]; then error "Missing value for $1"; exit 1; fi
      HIVE_NAME="$2"; shift 2 ;;
    -v|--versionsuffix)
      if [[ $# -lt 2 ]]; then error "Missing value for $1"; exit 1; fi
      VERSION_SUFFIX="$2"; shift 2 ;;
    --version)
      if [[ $# -lt 2 ]]; then error "Missing value for $1"; exit 1; fi
      STABLE_VERSION="$2"; shift 2 ;;
    -o|--output)
      if [[ $# -lt 2 ]]; then error "Missing value for $1"; exit 1; fi
      OUTPUT_DIR="$2"; shift 2 ;;
    -r|--rid)
      if [[ $# -lt 2 ]]; then error "Missing value for $1"; exit 1; fi
      TARGET_RID="$2"; shift 2 ;;
    --archive)
      ARCHIVE=1; shift ;;
    --copy)
      USE_COPY=1; shift ;;
    --skip-cli)
      SKIP_CLI=1; shift ;;
    --skip-bundle)
      SKIP_BUNDLE=1; shift ;;
    --native-aot)
      NATIVE_AOT=1; shift ;;
    --)
      shift; break ;;
    Release|Debug|release|debug)
      # Positional config (for backward-compat)
      if [[ -z "$CONFIG" ]]; then CONFIG="$1"; else HIVE_NAME="$1"; fi
      shift ;;
    *)
      # Treat first unknown as hive name if not set, else error
      if [[ "$HIVE_NAME" == "local" ]]; then HIVE_NAME="$1"; shift; else error "Unknown argument: $1"; exit 1; fi ;;
  esac
done

# Validate flag combinations
if [[ $ARCHIVE -eq 1 ]] && [[ -z "$OUTPUT_DIR" ]]; then
  error "--archive requires --output to be specified."
  exit 1
fi

if ! is_valid_hivename "$HIVE_NAME"; then
  error "Invalid hive name '$HIVE_NAME'. Hive names must match [A-Za-z0-9][A-Za-z0-9._-]* and cannot contain path separators or '..'."
  exit 1
fi

if [[ -n "$TARGET_RID" ]] && [[ $NATIVE_AOT -eq 1 ]]; then
  # Detect if this is a cross-OS build (e.g. building linux-x64 on macOS)
  HOST_OS="$(uname -s)"
  case "$HOST_OS" in
    Darwin) HOST_PREFIX="osx" ;;
    Linux)  HOST_PREFIX="linux" ;;
    *)      HOST_PREFIX="win" ;;
  esac
  if [[ "$TARGET_RID" != "$HOST_PREFIX"* ]]; then
    error "Cross-OS native AOT builds are not supported (host=$HOST_PREFIX, target=$TARGET_RID). Use --rid without --native-aot."
    exit 1
  fi
fi

# When --output is specified, always copy (portable layout, no symlinks)
if [[ -n "$OUTPUT_DIR" ]]; then
  USE_COPY=1
fi

# Normalize config value if set
if [[ -n "$CONFIG" ]]; then
  case "$(printf '%s' "$CONFIG" | tr '[:upper:]' '[:lower:]')" in
    release) CONFIG=Release ;;
    debug)   CONFIG=Debug ;;
    *) error "Unsupported configuration '$CONFIG'. Use Release or Debug."; exit 1 ;;
  esac
fi

# --version (stable shape) and --versionsuffix (prerelease) are mutually exclusive: one produces
# a clean X.Y.Z, the other an X.Y.Z-<suffix>. Allowing both would be ambiguous.
if [[ -n "$STABLE_VERSION" ]] && [[ -n "$VERSION_SUFFIX" ]]; then
  error "--version and --versionsuffix cannot be combined. Use --version for a stable shape (X.Y.Z) or --versionsuffix for a prerelease shape."
  exit 1
fi

# PKG_MATCH is the substring used to select the freshly built .nupkg files for this run from a
# packages directory that may also contain stale packages from earlier builds. For a prerelease
# build it is the version suffix (e.g. local.20250811.t033324); for a stable build it is the full
# X.Y.Z (matched at the end of the file name, so "13.5.0" does not also pick up "13.5.0-preview").
if [[ -n "$STABLE_VERSION" ]]; then
  if ! is_valid_stableversion "$STABLE_VERSION"; then
    error "Invalid --version '$STABLE_VERSION'. It must be a stable X.Y.Z version with no prerelease suffix, e.g. 13.5.0."
    exit 1
  fi
  # StabilizePackageVersion=true strips Arcade's date/build suffix so the produced packages are an
  # exact stable shape (Aspire.Hosting.13.5.0.nupkg), matching a real GA release. This is what lets
  # the CLI identity sidecar emulate a future released build entirely from locally built packages.
  PACK_VERSION_ARGS=(/p:VersionPrefix="$STABLE_VERSION" /p:StabilizePackageVersion=true)
  PKG_MATCH="$STABLE_VERSION"
  STABLE_BUILD=1
  log "Using stable release version: $STABLE_VERSION (no prerelease suffix)"
else
  # If no version suffix provided, auto-generate one so packages rev every build.
  if [[ -z "$VERSION_SUFFIX" ]]; then
    VERSION_SUFFIX="local.$(date -u +%Y%m%d).t$(date -u +%H%M%S)"
  fi

  # Validate provided/auto-generated suffix early to avoid NuGet failures.
  if ! is_valid_versionsuffix "$VERSION_SUFFIX"; then
    error "Invalid versionsuffix '$VERSION_SUFFIX'. It must be dot-separated identifiers using [0-9A-Za-z-] only; numeric identifiers cannot have leading zeros."
    warn "Examples: preview.1, rc.2, local.20250811.t033324"
    exit 1
  fi
  PACK_VERSION_ARGS=(/p:VersionSuffix="$VERSION_SUFFIX")
  PKG_MATCH="$VERSION_SUFFIX"
  STABLE_BUILD=0
  log "Using prerelease version suffix: $VERSION_SUFFIX"
fi

# Track effective configuration
EFFECTIVE_CONFIG="${CONFIG:-Release}"

# Skip native AOT during pack unless user will build it separately via --native-aot + Bundle.proj
AOT_ARG=""
if [[ $NATIVE_AOT -eq 0 ]]; then
  AOT_ARG="/p:PublishAot=false"
fi

if [ -n "$CONFIG" ]; then
  log "Building and packing NuGet packages [-c $CONFIG] (${PACK_VERSION_ARGS[*]})"
  # Single invocation: restore + build + pack to ensure all Build-triggered targets run and packages are produced.
  "$REPO_ROOT/build.sh" --restore --build --pack -c "$CONFIG" "${PACK_VERSION_ARGS[@]}" /p:SkipTestProjects=true /p:SkipPlaygroundProjects=true $AOT_ARG
  PKG_DIR="$REPO_ROOT/artifacts/packages/$CONFIG/Shipping"
  if [ ! -d "$PKG_DIR" ]; then
    error "Could not find packages path $PKG_DIR for CONFIG=$CONFIG"
    exit 1
  fi
else
  log "Building and packing NuGet packages [-c Release] (${PACK_VERSION_ARGS[*]})"
  "$REPO_ROOT/build.sh" --restore --build --pack -c Release "${PACK_VERSION_ARGS[@]}" /p:SkipTestProjects=true /p:SkipPlaygroundProjects=true $AOT_ARG
  PKG_DIR="$REPO_ROOT/artifacts/packages/Release/Shipping"
  if [ ! -d "$PKG_DIR" ]; then
    error "Could not find packages path $PKG_DIR for CONFIG=Release"
    exit 1
  fi
fi

# Ensure there are some .nupkg files
shopt -s nullglob
packages=("$PKG_DIR"/*.nupkg)
pkg_count=${#packages[@]}
shopt -u nullglob
if [[ $pkg_count -eq 0 ]]; then
  error "No .nupkg files found in $PKG_DIR. Did the pack step succeed?"
  exit 1
fi
log "Found $pkg_count packages in $PKG_DIR"

# Determine the RID for the current platform (or use --rid override)
if [[ -n "$TARGET_RID" ]]; then
  BUNDLE_RID="$TARGET_RID"
  log "Using target RID: $BUNDLE_RID"
else
  ARCH=$(uname -m)
  case "$(uname -s)" in
    Darwin)
      if [[ "$ARCH" == "arm64" ]]; then BUNDLE_RID="osx-arm64"; else BUNDLE_RID="osx-x64"; fi
      ;;
    Linux)
      if [[ "$ARCH" == "aarch64" ]]; then BUNDLE_RID="linux-arm64"; else BUNDLE_RID="linux-x64"; fi
      ;;
    *)
      BUNDLE_RID="linux-x64"
      ;;
  esac
fi

if [[ "$BUNDLE_RID" == win-* ]]; then
  CLI_EXE_NAME="aspire.exe"
else
  CLI_EXE_NAME="aspire"
fi

if [[ -n "$OUTPUT_DIR" ]]; then
  ASPIRE_ROOT="$OUTPUT_DIR"
else
  ASPIRE_ROOT="$HOME/.aspire"
fi
CLI_BIN_DIR="$ASPIRE_ROOT/bin"

HIVES_ROOT="$ASPIRE_ROOT/hives"
HIVE_ROOT="$HIVES_ROOT/$HIVE_NAME"
HIVE_PATH="$HIVE_ROOT/packages"

log "Preparing hive directory: $HIVES_ROOT"
mkdir -p "$HIVES_ROOT"

# Remove previous hive content (handles both old layout symlinks and stale data)
if [ -e "$HIVE_ROOT" ] || [ -L "$HIVE_ROOT" ]; then
  log "Removing previous hive '$HIVE_NAME'"
  rm -rf "$HIVE_ROOT"
fi

if [[ $USE_COPY -eq 1 ]]; then
  log "Populating hive '$HIVE_NAME' by copying .nupkg files (match: $PKG_MATCH)"
  mkdir -p "$HIVE_PATH"
  # Only copy packages matching the current build to avoid accumulating stale packages. For a
  # stable build we anchor the version to the end (*.13.5.0.nupkg / *.13.5.0.symbols.nupkg) so we
  # don't also pick up a stale prerelease of the same version prefix (e.g. 13.5.0-preview); for a
  # prerelease build the unique suffix already disambiguates, so a substring match is sufficient.
  if [[ $STABLE_BUILD -eq 1 ]]; then
    match_globs=("$PKG_DIR"/*."$PKG_MATCH".nupkg "$PKG_DIR"/*."$PKG_MATCH".symbols.nupkg)
  else
    match_globs=("$PKG_DIR"/*"$PKG_MATCH"*.nupkg)
  fi
  copied_packages=0
  shopt -s nullglob
  for pkg in "${match_globs[@]}"; do
    pkg_name="$(basename "$pkg")"
    if [[ -f "$pkg" ]] && [[ "$pkg_name" != ._* ]]; then
      cp -f "$pkg" "$HIVE_PATH"/
      copied_packages=$((copied_packages + 1))
    fi
  done
  shopt -u nullglob
  log "Created/updated hive '$HIVE_NAME' at $HIVE_PATH (copied $copied_packages packages)."
else
  log "Linking hive '$HIVE_NAME/packages' to $PKG_DIR"
  mkdir -p "$HIVE_ROOT"
  if ln -sfn "$PKG_DIR" "$HIVE_PATH" 2>/dev/null; then
    log "Created/updated hive '$HIVE_NAME/packages' -> $PKG_DIR"
  else
    warn "Symlink not supported; copying .nupkg files instead"
    mkdir -p "$HIVE_PATH"
    copied_packages=0
    shopt -s nullglob
    for pkg in "$PKG_DIR"/*.nupkg; do
      pkg_name="$(basename "$pkg")"
      if [[ -f "$pkg" ]] && [[ "$pkg_name" != ._* ]]; then
        cp -f "$pkg" "$HIVE_PATH"/
        copied_packages=$((copied_packages + 1))
      fi
    done
    shopt -u nullglob
    log "Created/updated hive '$HIVE_NAME' at $HIVE_PATH (copied $copied_packages packages)."
  fi
fi

# Build the bundle payload (aspire-managed + DCP tar.gz archive, and optionally native AOT CLI)
if [[ $SKIP_BUNDLE -eq 0 ]]; then
  BUNDLE_PROJ="$REPO_ROOT/eng/Bundle.proj"

  # Clean stale managed publish output so dotnet publish doesn't skip due to incremental builds
  STALE_MANAGED_DIR="$REPO_ROOT/artifacts/bundle/$BUNDLE_RID/managed"
  if [[ -d "$STALE_MANAGED_DIR" ]]; then
    log "Cleaning stale managed publish output at $STALE_MANAGED_DIR"
    rm -rf "$STALE_MANAGED_DIR"
  fi

  if [[ $NATIVE_AOT -eq 1 ]]; then
    log "Building bundle (aspire-managed + DCP + native AOT CLI)..."
    set +e
    dotnet build "$BUNDLE_PROJ" -c "$EFFECTIVE_CONFIG" "${PACK_VERSION_ARGS[@]}" "/p:TargetRid=$BUNDLE_RID"
    rc=$?
    set -e
  else
    log "Building bundle (aspire-managed + DCP)..."
    set +e
    dotnet build "$BUNDLE_PROJ" -c "$EFFECTIVE_CONFIG" /p:SkipNativeBuild=true "${PACK_VERSION_ARGS[@]}" "/p:TargetRid=$BUNDLE_RID"
    rc=$?
    set -e
  fi
  if [[ $rc -ne 0 ]]; then
    error "Bundle build failed."
    exit 1
  fi

  # Locate the bundle payload archive produced by Bundle.proj / CreateLayout.
  # The archive is embedded in the CLI binary so EnsureExtractedAsync handles
  # versioned layout creation, symlink management, and cleanup at runtime.
  BUNDLE_PAYLOAD_ARCHIVE="$(ls -t "$REPO_ROOT/artifacts/bundle/"aspire-*-"$BUNDLE_RID".tar.gz 2>/dev/null | head -1)"
  if [[ -z "$BUNDLE_PAYLOAD_ARCHIVE" ]]; then
    error "Bundle payload archive not found in artifacts/bundle/ for RID $BUNDLE_RID"
    exit 1
  fi
  PAYLOAD_SIZE_MB="$(du -m "$BUNDLE_PAYLOAD_ARCHIVE" | cut -f1)"
  log "Bundle payload archive: $BUNDLE_PAYLOAD_ARCHIVE ($PAYLOAD_SIZE_MB MB)"
fi

# Install the CLI to $ASPIRE_ROOT/bin
if [[ $SKIP_CLI -eq 0 ]]; then
  if [[ $NATIVE_AOT -eq 1 ]]; then
    # Native AOT CLI from Bundle.proj publish (already has embedded bundle payload)
    CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli/$EFFECTIVE_CONFIG/net10.0/$BUNDLE_RID/native"
    if [[ ! -d "$CLI_PUBLISH_DIR" ]]; then
      CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli/$EFFECTIVE_CONFIG/net10.0/$BUNDLE_RID/publish"
    fi
  elif [[ -n "$TARGET_RID" ]]; then
    # Cross-RID: publish CLI for the target platform with embedded bundle payload
    log "Publishing Aspire CLI for target RID: $TARGET_RID"
    CLI_PROJ="$REPO_ROOT/src/Aspire.Cli/Aspire.Cli.csproj"
    CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli/$EFFECTIVE_CONFIG/net10.0/$TARGET_RID/publish"
    PUBLISH_ARGS=(-c "$EFFECTIVE_CONFIG" -r "$TARGET_RID" --self-contained /p:PublishAot=false /p:PublishSingleFile=true "${PACK_VERSION_ARGS[@]}")
    if [[ -n "$BUNDLE_PAYLOAD_ARCHIVE" ]]; then
      PUBLISH_ARGS+=("/p:BundlePayloadPath=$BUNDLE_PAYLOAD_ARCHIVE")
    fi
    set +e
    dotnet publish "$CLI_PROJ" "${PUBLISH_ARGS[@]}"
    rc=$?
    set -e
    if [[ $rc -ne 0 ]]; then
      error "CLI publish for RID $TARGET_RID failed."
      exit 1
    fi
  else
    CLI_PROJ="$REPO_ROOT/src/Aspire.Cli/Aspire.Cli.Tool.csproj"
    if [[ -n "$BUNDLE_PAYLOAD_ARCHIVE" ]]; then
      # NativeAOT CLI (Aspire.Cli.csproj sets PublishAot=true) with embedded bundle payload.
      # Publish output is RID-specific when we pass -r, so the path includes $BUNDLE_RID.
      CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli.Tool/$EFFECTIVE_CONFIG/net10.0/$BUNDLE_RID/publish"
      log "Publishing Aspire CLI (dotnet tool, native AOT) with embedded bundle payload..."
      set +e
      dotnet publish "$CLI_PROJ" -c "$EFFECTIVE_CONFIG" -r "$BUNDLE_RID" "${PACK_VERSION_ARGS[@]}" "/p:BundlePayloadPath=$BUNDLE_PAYLOAD_ARCHIVE"
      rc=$?
      set -e
      if [[ $rc -ne 0 ]]; then
        error "CLI publish with embedded bundle failed."
        exit 1
      fi
    else
      # --skip-bundle builds Aspire.Cli.Tool with PublishAot=false, which keeps the
      # historical framework-dependent, non-RID output layout.
      CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli.Tool/$EFFECTIVE_CONFIG/net10.0/publish"
      if [[ ! -d "$CLI_PUBLISH_DIR" ]]; then
        CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli.Tool/$EFFECTIVE_CONFIG/net10.0"
      fi
    fi

    if [[ ! -f "$CLI_PUBLISH_DIR/aspire" ]]; then
      RID_CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli.Tool/$EFFECTIVE_CONFIG/net10.0/$BUNDLE_RID/publish"
      RID_CLI_NATIVE_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli.Tool/$EFFECTIVE_CONFIG/net10.0/$BUNDLE_RID/native"
      if [[ -f "$RID_CLI_PUBLISH_DIR/aspire" ]]; then
        CLI_PUBLISH_DIR="$RID_CLI_PUBLISH_DIR"
      elif [[ -f "$RID_CLI_NATIVE_DIR/aspire" ]]; then
        CLI_PUBLISH_DIR="$RID_CLI_NATIVE_DIR"
      fi
    fi
  fi

  CLI_SOURCE_PATH="$CLI_PUBLISH_DIR/$CLI_EXE_NAME"

  if [ -f "$CLI_SOURCE_PATH" ]; then
    if [[ $NATIVE_AOT -eq 1 ]]; then
      log "Installing Aspire CLI (native AOT) to $CLI_BIN_DIR"
    else
      log "Installing Aspire CLI to $CLI_BIN_DIR"
    fi
    mkdir -p "$CLI_BIN_DIR"

    # Copy all files and directories from the publish directory (CLI and its dependencies).
    if ! cp -Rf "$CLI_PUBLISH_DIR"/. "$CLI_BIN_DIR"/; then
      error "Failed to copy CLI files from $CLI_PUBLISH_DIR to $CLI_BIN_DIR"
      exit 1
    fi

    # Ensure the CLI is executable
    chmod +x "$CLI_BIN_DIR/$CLI_EXE_NAME"
    if [[ ! -f "$CLI_BIN_DIR/$CLI_EXE_NAME" ]]; then
      error "Installed CLI executable was not found at $CLI_BIN_DIR/$CLI_EXE_NAME"
      exit 1
    fi

    # Stamp the install-route sidecar so `aspire info` / `aspire uninstall`
    # can identify this binary as a locally-built (`localhive`) install.
    # The format matches docs/specs/install-routes.md exactly; localhive
    # shares the script-route layout (binary under <prefix>/bin/, bundle
    # extracted at parent-of-bin).
    printf '%s' '{"source":"localhive"}' > "$CLI_BIN_DIR/.aspire-install.json"

    log "Aspire CLI installed to: $CLI_BIN_DIR/$CLI_EXE_NAME"

    if [[ -z "$OUTPUT_DIR" ]]; then
      log "Run Aspire directly with: $CLI_BIN_DIR/$CLI_EXE_NAME"
      if [[ ":$PATH:" != *":$CLI_BIN_DIR:"* ]]; then
        log "For this shell only, run: export PATH=\"$CLI_BIN_DIR:\$PATH\""
      fi
    fi
  else
    warn "Could not find CLI at $CLI_SOURCE_PATH. Skipping CLI installation."
    warn "You may need to build the CLI separately or use 'dotnet tool install' for the Aspire.Cli package."
  fi
fi

# For a stable-shaped emulated release written to a portable layout, drop an
# activate script so the layout is turnkey. Sourcing it puts the CLI on PATH,
# stamps the emulated stable identity (so the locally-built CLI resolves Aspire*
# from the bundled hive), and — critically — isolates the NuGet global-packages
# cache.
#
# Why the isolated cache matters: NuGet's global packages folder
# ($HOME/.nuget/packages) caches EXTRACTED packages by version. When you emulate a
# FIXED stable version (e.g. 13.5.0) and rebuild it, a stale 13.5.0 left in that
# shared cache by an earlier build silently shadows the freshly built one — same
# version string, different content. The stale AppHost SDK can then inject a
# prerelease version floor (Version=">= X.Y.Z-<suffix>") and restore drifts to an
# unrelated prerelease of the same version instead of your stable packages. A
# per-layout cache guarantees restore only ever sees this emulation's packages.
if [[ -n "$OUTPUT_DIR" ]] && [[ $STABLE_BUILD -eq 1 ]] && [[ $SKIP_CLI -eq 0 ]]; then
  ACTIVATE_PATH="$OUTPUT_DIR/activate.sh"
  log "Writing emulated-stable activation script: $ACTIVATE_PATH"
  # Unquoted heredoc: $STABLE_VERSION and $HIVE_NAME are expanded now; everything
  # that must be evaluated at activation time (paths, PATH, command -v) is escaped.
  cat > "$ACTIVATE_PATH" <<ACTIVATE
# Activate the emulated stable $STABLE_VERSION Aspire release (all-local, hermetic).
# Usage: source "<path-to-this-layout>/activate.sh"
_aspire_root="\$(cd "\$(dirname "\${BASH_SOURCE[0]:-\$0}")" && pwd)"
export PATH="\$_aspire_root/bin:\$PATH"
export ASPIRE_CLI_CHANNEL=stable
export ASPIRE_CLI_VERSION=$STABLE_VERSION
export ASPIRE_CLI_PACKAGES="\$_aspire_root/hives/$HIVE_NAME/packages"
# Hermetic NuGet global-packages cache for this emulated release. A per-layout
# cache is required when rebuilding a fixed stable version so restore can't be
# shadowed by a stale, same-versioned package in \$HOME/.nuget/packages.
export NUGET_PACKAGES="\$_aspire_root/.nuget-packages"
mkdir -p "\$_aspire_root/work"
cd "\$_aspire_root/work"
echo "Activated emulated stable $STABLE_VERSION (hermetic NUGET_PACKAGES). CLI: \$(command -v aspire)"
ACTIVATE
fi

# Create archive if requested
if [[ $ARCHIVE -eq 1 ]]; then
  # Resolve to absolute path before cd to avoid relative path issues
  ARCHIVE_BASE="$(cd "$(dirname "$OUTPUT_DIR")" && pwd)/$(basename "$OUTPUT_DIR")"
  if [[ "$BUNDLE_RID" == win-* ]]; then
    ARCHIVE_PATH="${ARCHIVE_BASE}.zip"
    log "Creating archive: $ARCHIVE_PATH"
    (cd "$OUTPUT_DIR" && zip -r "$ARCHIVE_PATH" .)
  else
    ARCHIVE_PATH="${ARCHIVE_BASE}.tar.gz"
    log "Creating archive: $ARCHIVE_PATH"
    COPYFILE_DISABLE=1 tar -czf "$ARCHIVE_PATH" -C "$OUTPUT_DIR" .
  fi
  log "Archive created: $ARCHIVE_PATH"
fi

echo
log "Done."
echo
if [[ -n "$OUTPUT_DIR" ]]; then
  log "Portable layout created at: $OUTPUT_DIR"
  if [[ $STABLE_BUILD -eq 1 ]] && [[ $SKIP_CLI -eq 0 ]]; then
    log ""
    log "Emulated stable $STABLE_VERSION. Activate a hermetic, all-local session with:"
    log "  source \"$OUTPUT_DIR/activate.sh\""
    log "It sets PATH + ASPIRE_CLI_* (channel=stable, version=$STABLE_VERSION) and an isolated"
    log "NUGET_PACKAGES so restores can't be shadowed by a stale cached $STABLE_VERSION."
  fi
  if [[ $ARCHIVE -eq 1 ]]; then
    log "Archive: $ARCHIVE_PATH"
    log ""
    log "To install on the target machine:"
    if [[ "$BUNDLE_RID" == win-* ]]; then
      log "  Expand-Archive -Path $(basename "$ARCHIVE_PATH") -DestinationPath \$HOME\\.aspire"
      log "  \$HOME\\.aspire\\bin\\aspire.exe"
    else
      log "  mkdir -p ~/.aspire && tar -xzf $(basename "$ARCHIVE_PATH") -C ~/.aspire"
      log "  ~/.aspire/bin/aspire"
      if [[ $STABLE_BUILD -eq 1 ]] && [[ $SKIP_CLI -eq 0 ]]; then
        log "  source ~/.aspire/activate.sh   # hermetic emulated stable $STABLE_VERSION session"
      fi
    fi
  fi
else
  log "Aspire CLI will discover a channel named '$HIVE_NAME' from:"
  log "  $HIVE_PATH"
  echo
  log "Channel behavior: Aspire* comes from the hive; others from nuget.org."
  echo
  if [[ $SKIP_CLI -eq 0 ]]; then
    log "The locally-built CLI was installed to: $ASPIRE_ROOT/bin"
    echo
  fi
  if [[ $SKIP_BUNDLE -eq 0 ]]; then
    log "Bundle payload embedded in CLI binary. The CLI will extract and"
    log "  create the versioned layout (bundle/ -> versions/<id>/) on first run."
    echo
  fi
  log "The Aspire CLI discovers channels automatically from the hives directory; no extra flags are required."
fi
