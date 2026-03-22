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
#   -v, --versionsuffix   Prerelease version suffix (default: auto-generates local.YYYYMMDD.tHHmmss)
#       --copy            Copy .nupkg files instead of creating a symlink
#       --skip-cli        Skip installing the locally-built CLI to $HOME/.aspire/bin
#       --skip-bundle     Skip building and installing the bundle (aspire-managed + DCP)
#       --native-aot      Build native AOT CLI (self-extracting with embedded bundle)
#   -h, --help            Show this help and exit
#
# Notes:
# - If no configuration is specified, the script tries Release then Debug.
# - The hive is created at $HOME/.aspire/hives/<HiveName> so the Aspire CLI can discover a channel.
# - The CLI is installed to $HOME/.aspire/bin so it can be used directly.

set -euo pipefail

print_usage() {
  cat <<EOF
Usage:
  ./localhive.sh [options]
  ./localhive.sh [Release|Debug] [HiveName]

Options:
  -c, --configuration   Build configuration: Release or Debug
  -n, --name            Hive name (default: local)
  -v, --versionsuffix   Prerelease version suffix (default: auto-generates local.YYYYMMDD.tHHmmss)
      --copy            Copy .nupkg files instead of creating a symlink
      --skip-cli        Skip installing the locally-built CLI to \$HOME/.aspire/bin
      --skip-bundle     Skip building and installing the bundle (aspire-managed + DCP)
      --native-aot      Build native AOT CLI (self-extracting with embedded bundle)
  -h, --help            Show this help and exit

Examples:
  ./localhive.sh -c Release -n local
  ./localhive.sh Debug my-feature
  ./localhive.sh -c Release -n demo -v local.20250811.t033324
  ./localhive.sh --skip-cli

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

# Normalize config value if set
if [[ -n "$CONFIG" ]]; then
  case "${CONFIG,,}" in
    release) CONFIG=Release ;;
    debug)   CONFIG=Debug ;;
    *) error "Unsupported configuration '$CONFIG'. Use Release or Debug."; exit 1 ;;
  esac
fi

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
log "Using prerelease version suffix: $VERSION_SUFFIX"

# Track effective configuration
EFFECTIVE_CONFIG="${CONFIG:-Release}"

# Skip native AOT during pack unless user will build it separately via --native-aot + Bundle.proj
AOT_ARG=""
if [[ $NATIVE_AOT -eq 0 ]]; then
  AOT_ARG="/p:PublishAot=false"
fi

if [ -n "$CONFIG" ]; then
  log "Building and packing NuGet packages [-c $CONFIG] with versionsuffix '$VERSION_SUFFIX'"
  # Single invocation: restore + build + pack to ensure all Build-triggered targets run and packages are produced.
  "$REPO_ROOT/build.sh" --restore --build --pack -c "$CONFIG" /p:VersionSuffix="$VERSION_SUFFIX" /p:SkipTestProjects=true /p:SkipPlaygroundProjects=true $AOT_ARG
  PKG_DIR="$REPO_ROOT/artifacts/packages/$CONFIG/Shipping"
  if [ ! -d "$PKG_DIR" ]; then
    error "Could not find packages path $PKG_DIR for CONFIG=$CONFIG"
    exit 1
  fi
else
  log "Building and packing NuGet packages [-c Release] with versionsuffix '$VERSION_SUFFIX'"
  "$REPO_ROOT/build.sh" --restore --build --pack -c Release /p:VersionSuffix="$VERSION_SUFFIX" /p:SkipTestProjects=true /p:SkipPlaygroundProjects=true $AOT_ARG
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

HIVES_ROOT="$HOME/.aspire/hives"
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
  log "Populating hive '$HIVE_NAME' by copying .nupkg files"
  mkdir -p "$HIVE_PATH"
  cp -f "$PKG_DIR"/*.nupkg "$HIVE_PATH"/ 2>/dev/null || true
  log "Created/updated hive '$HIVE_NAME' at $HIVE_PATH (copied packages)."
else
  log "Linking hive '$HIVE_NAME/packages' to $PKG_DIR"
  mkdir -p "$HIVE_ROOT"
  if ln -sfn "$PKG_DIR" "$HIVE_PATH" 2>/dev/null; then
    log "Created/updated hive '$HIVE_NAME/packages' -> $PKG_DIR"
  else
    warn "Symlink not supported; copying .nupkg files instead"
    mkdir -p "$HIVE_PATH"
    cp -f "$PKG_DIR"/*.nupkg "$HIVE_PATH"/ 2>/dev/null || true
    log "Created/updated hive '$HIVE_NAME' at $HIVE_PATH (copied packages)."
  fi
fi

# Determine the RID for the current platform
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

ASPIRE_ROOT="$HOME/.aspire"
CLI_BIN_DIR="$ASPIRE_ROOT/bin"

# Build the bundle (aspire-managed + DCP, and optionally native AOT CLI)
if [[ $SKIP_BUNDLE -eq 0 ]]; then
  BUNDLE_PROJ="$REPO_ROOT/eng/Bundle.proj"

  if [[ $NATIVE_AOT -eq 1 ]]; then
    log "Building bundle (aspire-managed + DCP + native AOT CLI)..."
    dotnet build "$BUNDLE_PROJ" -c "$EFFECTIVE_CONFIG" "/p:VersionSuffix=$VERSION_SUFFIX"
  else
    log "Building bundle (aspire-managed + DCP)..."
    dotnet build "$BUNDLE_PROJ" -c "$EFFECTIVE_CONFIG" /p:SkipNativeBuild=true "/p:VersionSuffix=$VERSION_SUFFIX"
  fi
  if [[ $? -ne 0 ]]; then
    error "Bundle build failed."
    exit 1
  fi

  BUNDLE_LAYOUT_DIR="$REPO_ROOT/artifacts/bundle/$BUNDLE_RID"

  if [[ ! -d "$BUNDLE_LAYOUT_DIR" ]]; then
    error "Bundle layout not found at $BUNDLE_LAYOUT_DIR"
    exit 1
  fi

  # Copy managed/ and dcp/ to $HOME/.aspire so the CLI auto-discovers them
  for component in managed dcp; do
    SOURCE_DIR="$BUNDLE_LAYOUT_DIR/$component"
    DEST_DIR="$ASPIRE_ROOT/$component"
    if [[ -d "$SOURCE_DIR" ]]; then
      rm -rf "$DEST_DIR"
      log "Copying $component/ to $DEST_DIR"
      cp -r "$SOURCE_DIR" "$DEST_DIR"
      # Ensure executables are executable
      if [[ "$component" == "managed" ]]; then
        chmod +x "$DEST_DIR/aspire-managed" 2>/dev/null || true
      elif [[ "$component" == "dcp" ]]; then
        find "$DEST_DIR" -type f -name "dcp" -exec chmod +x {} \; 2>/dev/null || true
      fi
    else
      warn "$component/ not found in bundle layout at $SOURCE_DIR"
    fi
  done

  log "Bundle installed to $ASPIRE_ROOT (managed/ + dcp/)"
fi

# Install the CLI to $HOME/.aspire/bin
if [[ $SKIP_CLI -eq 0 ]]; then
  if [[ $NATIVE_AOT -eq 1 ]]; then
    # Native AOT CLI from Bundle.proj publish
    CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli/$EFFECTIVE_CONFIG/net10.0/$BUNDLE_RID/native"
    if [[ ! -d "$CLI_PUBLISH_DIR" ]]; then
      CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli/$EFFECTIVE_CONFIG/net10.0/$BUNDLE_RID/publish"
    fi
  else
    # Framework-dependent CLI from dotnet tool build
    CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli.Tool/$EFFECTIVE_CONFIG/net10.0/publish"
    if [[ ! -d "$CLI_PUBLISH_DIR" ]]; then
      CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli.Tool/$EFFECTIVE_CONFIG/net10.0"
    fi
  fi

  CLI_SOURCE_PATH="$CLI_PUBLISH_DIR/aspire"

  if [ -f "$CLI_SOURCE_PATH" ]; then
    if [[ $NATIVE_AOT -eq 1 ]]; then
      log "Installing Aspire CLI (native AOT) to $CLI_BIN_DIR"
    else
      log "Installing Aspire CLI to $CLI_BIN_DIR"
    fi
    mkdir -p "$CLI_BIN_DIR"

    # Copy all files from the publish directory (CLI and its dependencies)
    cp -f "$CLI_PUBLISH_DIR"/* "$CLI_BIN_DIR"/ 2>/dev/null || true

    # Ensure the CLI is executable
    chmod +x "$CLI_BIN_DIR/aspire"

    log "Aspire CLI installed to: $CLI_BIN_DIR/aspire"

    if "$CLI_BIN_DIR/aspire" config set channel "$HIVE_NAME" -g >/dev/null 2>&1; then
      log "Set global channel to '$HIVE_NAME'"
    else
      warn "Failed to set global channel to '$HIVE_NAME'. Run: aspire config set channel '$HIVE_NAME' -g"
    fi

    # Check if the bin directory is in PATH
    if [[ ":$PATH:" != *":$CLI_BIN_DIR:"* ]]; then
      warn "The CLI bin directory is not in your PATH."
      log "Add it to your PATH with: export PATH=\"$CLI_BIN_DIR:\$PATH\""
    fi
  else
    warn "Could not find CLI at $CLI_SOURCE_PATH. Skipping CLI installation."
    warn "You may need to build the CLI separately or use 'dotnet tool install' for the Aspire.Cli package."
  fi
fi

echo
log "Done."
echo
log "Aspire CLI will discover a channel named '$HIVE_NAME' from:"
log "  $HIVE_PATH"
echo
log "Channel behavior: Aspire* comes from the hive; others from nuget.org."
echo
if [[ $SKIP_CLI -eq 0 ]]; then
  log "The locally-built CLI was installed to: $HOME/.aspire/bin"
  echo
fi
if [[ $SKIP_BUNDLE -eq 0 ]]; then
  log "Bundle (aspire-managed + DCP) installed to: $HOME/.aspire"
  log "  The CLI at ~/.aspire/bin/ will auto-discover managed/ and dcp/ in the parent directory."
  echo
fi
log "The Aspire CLI discovers channels automatically from the hives directory; no extra flags are required."
