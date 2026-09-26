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
  echo "  --warnNotAsError <codes>         Additional warning exemptions, merged with the evaluated repository policy."
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

arguments=()
extraargs=()
warn_as_error=true
explicit_warning_exemptions=''
ci=false
clean=false
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
      arguments+=("/p:TargetArchitecture=$arch")
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
      arguments+=("-configuration" "$val")
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
      arguments+=("/p:TargetOS=$os")
      shift 2
      ;;

     -testnobuild)
      arguments+=("/p:VSTestNoBuild=true")
      shift 1
      ;;

     -build-extension)
      extraargs+=("/p:BuildExtension=true")
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

     -ci)
      ci=true
      arguments+=("-ci")
      shift
      ;;

     -clean)
      clean=true
      arguments+=("-clean")
      shift
      ;;

     -warnaserror|-warnnotaserror)
      if [[ $# -lt 2 ]]; then
        echo "No value supplied for $1." >&2
        exit 1
      fi
      if [[ "$opt" == "-warnaserror" ]]; then
        warn_as_error="$(echo "$2" | tr "[:upper:]" "[:lower:]")"
        if [[ "$warn_as_error" != "true" && "$warn_as_error" != "false" ]]; then
          echo "Expected true or false for $1." >&2
          exit 1
        fi
      else
        explicit_warning_exemptions="$2"
      fi
      shift 2
      ;;

     *)
      extraargs+=("$1")
      shift 1
      ;;
  esac
done

if [ ${#actInt[@]} -eq 0 ]; then
    arguments=("-restore" "-build" ${arguments[@]+"${arguments[@]}"})
fi

if [[ "${TreatWarningsAsErrors:-}" == "false" ]]; then
    # Arcade forwards this value directly into /p:TreatWarningsAsErrors=...,
    # and the Csc task only accepts boolean literals (true/false). The
    # earlier '0' worked as a shell-truthy switch but produced
    # 'MSB4030: "0" is an invalid value for the "TreatWarningsAsErrors"
    # parameter' once it reached the inner MSBuild call.
    warn_as_error=false
fi

# Arcade handles clean before invoking MSBuild, even when other actions are supplied.
# Preserve SDK-free cleanup instead of bootstrapping just to evaluate unused policy.
if [[ "$clean" != "true" && "$warn_as_error" == "true" ]]; then
    evaluation_properties=()
    # The guarded expansions also work with empty arrays under macOS Bash 3.2's nounset.
    for argument in ${arguments[@]+"${arguments[@]}"} ${extraargs[@]+"${extraargs[@]}"}; do
        case "$(echo "$argument" | tr "[:upper:]" "[:lower:]")" in
            /p:*|-p:*|/property:*|-property:*) evaluation_properties+=("$argument") ;;
        esac
    done

    # Arcade's standalone NuGet.targets restore does not import Directory.Build.props.
    # Evaluate that policy once and forward it to the command-line warning logger too.
    # See https://github.com/dotnet/msbuild/issues/10801.
    if repository_warnings="$(
        source "$scriptroot/common/tools.sh" >&2
        InitializeDotNetCli true >&2
        "$_InitializeDotNetCli/dotnet" msbuild "$eng_root/WarningPolicy.proj" -nologo \
          -getProperty:WarningsNotAsErrors "/p:Configuration=$config" "/p:ContinuousIntegrationBuild=$ci" \
          ${evaluation_properties[@]+"${evaluation_properties[@]}"}
    )"; then
        # Single-property output is a semicolon list, e.g. ";CS1591;NU1901".
        repository_warnings="${repository_warnings//$'\r'/}"
        if [[ "$repository_warnings" == *$'\n'* ]]; then
            printf 'Unexpected output while evaluating the repository warning policy:\n%s\n' "$repository_warnings" >&2
            exit 1
        fi
    else
        printf 'Could not evaluate the repository warning policy:\n%s\n' "$repository_warnings" >&2
        exit 1
    fi
    if [[ -n "$repository_warnings" ]]; then
        explicit_warning_exemptions="$repository_warnings${explicit_warning_exemptions:+;$explicit_warning_exemptions}"
    fi
fi

# Empty entries from property appends (e.g. ";CS1591;;TST1001") are valid in
# MSBuild properties but rejected by its command-line warning switch.
IFS=';' read -r -a warning_codes <<< "${explicit_warning_exemptions//[[:space:]]/}"
explicit_warning_exemptions=''
for code in ${warning_codes[@]+"${warning_codes[@]}"}; do
    if [[ -n "$code" && ";$explicit_warning_exemptions;" != *";$code;"* ]]; then
        explicit_warning_exemptions="${explicit_warning_exemptions:+$explicit_warning_exemptions;}$code"
    fi
done

arguments+=(${extraargs[@]+"${extraargs[@]}"} "-warnAsError" "$warn_as_error")
if [[ -n "$explicit_warning_exemptions" ]]; then
    arguments+=("-warnNotAsError" "$explicit_warning_exemptions")
fi
"$scriptroot/common/build.sh" "${arguments[@]}"
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
