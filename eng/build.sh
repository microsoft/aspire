#!/usr/bin/env bash

set -ue

source="${BASH_SOURCE[0]}"

# resolve $source until the file is no longer a symlink
while [[ -h "$source" ]]; do
  scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"
  source="$(readlink "$source")"
  # if $source was a relative symlink, we need to resolve it relative to the path where the
  # symlink file was located
  [[ $source != /* ]] && source="$scriptroot/$source"
done
scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"

usage()
{
  echo "Common settings:"
  echo "  --arch (-a)                     Target platform: x86, x64, arm or arm64."
  echo "                                  [Default: Your machine's architecture.]"
  echo "  --binaryLog (-bl)               Output binary log."
  echo "  --configuration (-c)            Build configuration: Debug or Release."
  echo "                                  [Default: Debug]"
  echo "  --help (-h)                     Print help and exit."
  echo "  --os                            Target operating system: windows, linux, or osx."
  echo "                                  [Default: Your machine's OS.]"
  echo "  --verbosity (-v)                MSBuild verbosity: q[uiet], m[inimal], n[ormal], d[etailed], and diag[nostic]."
  echo "                                  [Default: Minimal]"
  echo ""

  echo "Actions (defaults to --restore --build):"
  echo "  --build (-b)               Build all source projects."
  echo "                             This assumes --restore has been run already."
  echo "  --clean                    Clean the solution."
  echo "  --pack                     Package build outputs into NuGet packages."
  echo "  --publish                  Publish artifacts (e.g. symbols)."
  echo "                             This assumes --build has been run already."
  echo "  --rebuild                  Rebuild all source projects."
  echo "  --restore (-r)             Restore dependencies."
  echo "  --mauirestore              Restore dependencies and install MAUI workload (only on macOS)."
  echo "  --sign                     Sign build outputs."
  echo "  --test (-t)                Incrementally builds and runs tests."
  echo "                             Use in conjunction with --testnobuild to only run tests."
  echo ""

  echo "Libraries settings:"
  echo "  --testnobuild              Skip building tests when invoking -test."
  echo "  --build-extension          Build the VS Code extension."
  echo "  --bundle                   Build the self-contained bundle (CLI + Runtime + Dashboard + DCP)."
  echo "  --runtime-version <ver>    .NET runtime version for bundle (default: 10.0.2)."
  echo ""

  echo "Command line arguments starting with '/p:' are passed through to MSBuild."
  echo "Arguments can also be passed in with a single hyphen."
  echo ""
}

arguments=''
extraargs=''
build_bundle=false
runtime_version=""
config="Debug"

# Check if an action is passed in
declare -a actions=("b" "build" "r" "restore" "rebuild" "testnobuild" "sign" "publish" "clean" "t" "test" "build-extension")
actInt=($(comm -12 <(printf '%s\n' "${actions[@]/#/-}" | sort) <(printf '%s\n' "${@/#--/-}" | sort)))

while [[ $# > 0 ]]; do
  opt="$(echo "${1/#--/-}" | tr "[:upper:]" "[:lower:]")"

  case "$opt" in
     -help|-h|-\?|/?)
      usage
      exit 0
      ;;

     -arch|-a)
      if [ -z ${2+x} ]; then
        echo "No architecture supplied. See help (--help) for supported architectures." 1>&2
        exit 1
      fi
      passedArch="$(echo "$2" | tr "[:upper:]" "[:lower:]")"
      case "$passedArch" in
        x64|x86|arm|arm64)
          arch=$passedArch
          ;;
        *)
          echo "Unsupported target architecture '$2'."
          echo "The allowed values are x86, x64, arm, arm64."
          exit 1
          ;;
      esac
      arguments="$arguments /p:TargetArchitecture=$arch"
      shift 2
      ;;

     -configuration|-c)
      if [ -z ${2+x} ]; then
        echo "No configuration supplied. See help (--help) for supported configurations." 1>&2
        exit 1
      fi
      passedConfig="$(echo "$2" | tr "[:upper:]" "[:lower:]")"
      case "$passedConfig" in
        debug|release)
          val="$(tr '[:lower:]' '[:upper:]' <<< ${passedConfig:0:1})${passedConfig:1}"
          config="$val"
          ;;
        *)
          echo "Unsupported target configuration '$2'."
          echo "The allowed values are Debug and Release."
          exit 1
          ;;
      esac
      arguments="$arguments -configuration $val"
      shift 2
      ;;

     -os)
      if [ -z ${2+x} ]; then
        echo "No target operating system supplied. See help (--help) for supported target operating systems." 1>&2
        exit 1
      fi
      passedOS="$(echo "$2" | tr "[:upper:]" "[:lower:]")"
      case "$passedOS" in
        windows)
          os="windows" ;;
        linux)
          os="linux" ;;
        osx)
          os="osx" ;;
        *)
          echo "Unsupported target OS '$2'."
          echo "Try 'build.sh --help' for values supported by '--os'."
          exit 1
          ;;
      esac
      arguments="$arguments /p:TargetOS=$os"
      shift 2
      ;;

     -testnobuild)
      arguments="$arguments /p:VSTestNoBuild=true"
      shift 1
      ;;

     -build-extension)
      extraargs="$extraargs /p:BuildExtension=true"
      shift 1
      ;;

     -mauirestore)
      export restore_maui=true
      shift 1
      ;;

     -bundle)
      build_bundle=true
      shift 1
      ;;

     -runtime-version)
      if [ -z ${2+x} ]; then
        echo "No runtime version supplied." 1>&2
        exit 1
      fi
      runtime_version="$2"
      shift 2
      ;;

     *)
      extraargs="$extraargs $1"
      shift 1
      ;;
  esac
done

if [ ${#actInt[@]} -eq 0 ]; then
    arguments="-restore -build $arguments"
fi

if [[ "${TreatWarningsAsErrors:-}" == "false" ]]; then
    arguments="$arguments -warnAsError 0"
fi

arguments="$arguments $extraargs"
"$scriptroot/common/build.sh" $arguments
build_exit_code=$?

if [ $build_exit_code -ne 0 ]; then
    exit $build_exit_code
fi

# Build bundle if requested
if [ "$build_bundle" = true ]; then
    echo ""
    echo "Building bundle via MSBuild..."
    echo ""
    
    repo_root="$(dirname "$scriptroot")"
    
    # Use the local .NET SDK installed by restore
    export DOTNET_ROOT="$repo_root/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
    
    # Free up disk space by cleaning intermediate build artifacts (CI only)
    if [ "${CI:-}" = "true" ]; then
        echo "  Cleaning intermediate build artifacts to free disk space..."
        find "$repo_root" -type d -name "obj" -exec rm -rf {} + 2>/dev/null || true
        dotnet nuget locals http-cache --clear 2>/dev/null || true
        df -h / 2>/dev/null || true
    fi
    
    # Determine RID
    if [ -z "${os:-}" ]; then
        case "$(uname -s)" in
            Linux*)  target_os="linux" ;;
            Darwin*) target_os="osx" ;;
            *)       target_os="linux" ;;
        esac
    else
        target_os="$os"
    fi
    
    if [ -z "${arch:-}" ]; then
        case "$(uname -m)" in
            x86_64)  target_arch="x64" ;;
            aarch64) target_arch="arm64" ;;
            arm64)   target_arch="arm64" ;;
            *)       target_arch="x64" ;;
        esac
    else
        target_arch="$arch"
    fi
    
    rid="${target_os}-${target_arch}"
    
    echo "  RID: $rid"
    echo "  Configuration: $config"
    echo ""
    
    # Build MSBuild arguments
    bundle_args=(
        "$scriptroot/Bundle.proj"
        "/p:Configuration=$config"
        "/p:TargetRid=$rid"
    )
    
    # Pass through SkipNativeBuild if set
    for arg in "$@"; do
        if [[ "$arg" == *"SkipNativeBuild=true"* ]]; then
            bundle_args+=("/p:SkipNativeBuild=true")
            break
        fi
    done
    
    # CI flag is passed to Bundle.proj which handles version computation via Versions.props
    if [ "${CI:-}" = "true" ]; then
        bundle_args+=("/p:ContinuousIntegrationBuild=true")
    fi
    
    dotnet msbuild "${bundle_args[@]}" || exit $?
fi

exit 0
