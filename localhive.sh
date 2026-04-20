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
#       --archive         Create a .tar.gz (or .zip for win-* RIDs) archive of the output. Requires --output.
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
  -o, --output          Output directory for portable layout (instead of \$HOME/.aspire)
  -r, --rid             Target RID for cross-platform builds (e.g. linux-x64)
  -v, --versionsuffix   Prerelease version suffix (default: auto-generates local.YYYYMMDD.tHHmmss)
      --archive         Create a .tar.gz (or .zip for win-* RIDs) archive of the output. Requires --output.
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
  ./localhive.sh -o /tmp/aspire-linux -r linux-x64 --archive   # Portable archive for a Linux machine

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
OUTPUT_DIR=""
TARGET_RID=""
ARCHIVE=0
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
  log "Populating hive '$HIVE_NAME' by copying .nupkg files (version suffix: $VERSION_SUFFIX)"
  mkdir -p "$HIVE_PATH"
  # Only copy packages matching the current version suffix to avoid accumulating stale packages
  copied_packages=0
  shopt -s nullglob
  for pkg in "$PKG_DIR"/*"$VERSION_SUFFIX"*.nupkg; do
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

# Build the bundle (aspire-managed + DCP, and optionally native AOT CLI)
if [[ $SKIP_BUNDLE -eq 0 ]]; then
  BUNDLE_PROJ="$REPO_ROOT/eng/Bundle.proj"

  if [[ $NATIVE_AOT -eq 1 ]]; then
    log "Building bundle (aspire-managed + DCP + native AOT CLI)..."
    dotnet build "$BUNDLE_PROJ" -c "$EFFECTIVE_CONFIG" "/p:VersionSuffix=$VERSION_SUFFIX" "/p:TargetRid=$BUNDLE_RID"
  else
    log "Building bundle (aspire-managed + DCP)..."
    dotnet build "$BUNDLE_PROJ" -c "$EFFECTIVE_CONFIG" /p:SkipNativeBuild=true "/p:VersionSuffix=$VERSION_SUFFIX" "/p:TargetRid=$BUNDLE_RID"
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

# Install the CLI to $ASPIRE_ROOT/bin
if [[ $SKIP_CLI -eq 0 ]]; then
  if [[ $NATIVE_AOT -eq 1 ]]; then
    # Native AOT CLI from Bundle.proj publish
    CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli/$EFFECTIVE_CONFIG/net10.0/$BUNDLE_RID/native"
    if [[ ! -d "$CLI_PUBLISH_DIR" ]]; then
      CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli/$EFFECTIVE_CONFIG/net10.0/$BUNDLE_RID/publish"
    fi
  elif [[ -n "$TARGET_RID" ]]; then
    # Cross-RID: publish CLI for the target platform
    log "Publishing Aspire CLI for target RID: $TARGET_RID"
    CLI_PROJ="$REPO_ROOT/src/Aspire.Cli/Aspire.Cli.csproj"
    CLI_PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli/$EFFECTIVE_CONFIG/net10.0/$TARGET_RID/publish"
    dotnet publish "$CLI_PROJ" -c "$EFFECTIVE_CONFIG" -r "$TARGET_RID" --self-contained \
      /p:PublishAot=false /p:PublishSingleFile=true "/p:VersionSuffix=$VERSION_SUFFIX"
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

    if [[ -z "$OUTPUT_DIR" ]]; then
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
    fi
  else
    warn "Could not find CLI at $CLI_SOURCE_PATH. Skipping CLI installation."
    warn "You may need to build the CLI separately or use 'dotnet tool install' for the Aspire.Cli package."
  fi
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
  if [[ $ARCHIVE -eq 1 ]]; then
    log "Archive: $ARCHIVE_PATH"
    log ""
    log "To install on the target machine:"
    log "  mkdir -p ~/.aspire && tar -xzf $(basename "$ARCHIVE_PATH") -C ~/.aspire"
    log "  ~/.aspire/bin/aspire config set channel '$HIVE_NAME' -g"
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
    log "Bundle (aspire-managed + DCP) installed to: $ASPIRE_ROOT"
    log "  The CLI at ~/.aspire/bin/ will auto-discover managed/ and dcp/ in the parent directory."
    echo
  fi
  log "The Aspire CLI discovers channels automatically from the hives directory; no extra flags are required."
fi
