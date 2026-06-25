#!/usr/bin/env bash

# get-aspire-cli-pr.sh - Download and unpack the Aspire CLI from a specific PR's build artifacts
# Usage: ./get-aspire-cli-pr.sh PR_NUMBER [OPTIONS]
#        ./get-aspire-cli-pr.sh --run-id WORKFLOW_RUN_ID [OPTIONS]
#        ./get-aspire-cli-pr.sh --local-dir /path/to/artifacts [OPTIONS]
#        ./get-aspire-cli-pr.sh --local-dir /path/to/build-output [OPTIONS]   # raw 'dotnet build' output

set -euo pipefail

# Global constants / defaults
readonly BUILT_NUGETS_ARTIFACT_NAME="built-nugets"
readonly BUILT_NUGETS_RID_ARTIFACT_NAME="built-nugets-for"
readonly CLI_ARCHIVE_ARTIFACT_NAME_PREFIX="cli-native-archives"
readonly ASPIRE_CLI_ARTIFACT_NAME_PREFIX="aspire-cli"
readonly EXTENSION_ARTIFACT_NAME="aspire-extension"
readonly WINGET_MANIFEST_ARTIFACT_NAME="winget-manifests-prerelease"
readonly HOMEBREW_CASK_ARTIFACT_NAME="homebrew-cask-prerelease"

# Repository: Allow override via ASPIRE_REPO env var (owner/name). Default: microsoft/aspire
readonly REPO="${ASPIRE_REPO:-microsoft/aspire}"
readonly GH_REPOS_BASE="repos/${REPO}"

# Global constants
readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly YELLOW='\033[1;33m'
readonly RESET='\033[0m'

# Variables (defaults set after parsing arguments)
INSTALL_PREFIX=""
INSTALL_PREFIX_EXPLICIT=false
PR_NUMBER=""
WORKFLOW_RUN_ID=""
LOCAL_DIR=""
HIVE_LABEL=""
OS_ARG=""
ARCH_ARG=""
INSTALL_MODE="archive"
FORCE=false
SHOW_HELP=false
VERBOSE=false
KEEP_ARCHIVE=false
DRY_RUN=false
HIVE_ONLY=false
SKIP_EXTENSION_INSTALL=false
USE_INSIDERS=false
SKIP_PATH=false
HOST_OS="unset"

# Function to show help
show_help() {
    cat << 'EOF'
Aspire CLI PR Download Script

DESCRIPTION:
    Downloads and installs the Aspire CLI from a specific pull request's latest successful build.
    Automatically detects the current platform (OS and architecture) and downloads the appropriate artifact.

    The script queries the GitHub API to find the latest successful run of the 'ci.yml' workflow
    for the specified PR, then downloads and extracts the CLI archive for your platform using 'gh run download'.

    Optionally downloads and installs the VS Code Aspire extension as well.

    Alternatively, you can specify a workflow run ID directly to download from a specific build.

USAGE:
    ./get-aspire-cli-pr.sh PR_NUMBER [OPTIONS]
    ./get-aspire-cli-pr.sh PR_NUMBER --run-id WORKFLOW_RUN_ID [OPTIONS]
    ./get-aspire-cli-pr.sh --run-id WORKFLOW_RUN_ID [OPTIONS]
    ./get-aspire-cli-pr.sh --local-dir /path/to/artifacts [OPTIONS]

    PR_NUMBER                   Pull request number (required unless --run-id or --local-dir is used alone)
    --run-id, -r WORKFLOW_ID    Workflow run ID to download from (optional with PR, required without)
    --local-dir PATH            Use pre-downloaded artifacts from a local directory instead of downloading
                                from GitHub. Mutually exclusive with PR_NUMBER and --run-id.
                                The directory is auto-detected: if it contains a CLI archive
                                (aspire-cli-*.tar.gz or .zip) the archive flow is used; otherwise it is
                                treated as raw 'dotnet build'/'dotnet publish' output and the contained
                                'aspire' or 'aspire.exe' executable is installed directly.
                                NuGet packages (*.nupkg) in the directory are always installed into the hive.
    --hive-label LABEL          Override the NuGet hive label (default: pr-<PR_NUMBER>, run-<RUN_ID>,
                                or local for --local-dir)
    -i, --install-path PATH     Directory prefix to install (default: ~/.aspire)
                                CLI installs to: <install-path>/bin when installing from archives
                                or as a dotnet tool with --tool-path.
                                NuGet hive:      <install-path>/hives/pr-<PR_NUMBER>/packages (or run-<RUN_ID>)
    -m, --install-mode MODE     How to install the CLI: 'archive' (default) installs from
                                cli-native-archives-<rid> artifact, 'tool' installs the Aspire.Cli
                                dotnet tool from the PR's RID-specific NuGet artifact, 'winget'
                                installs from the generated WinGet manifest artifact, and
                                'homebrew' installs from the generated Homebrew cask artifact.
    --force                     Tool mode: update an existing Aspire.Cli tool to the exact PR package
                                version. WinGet mode: allow replacing an existing Microsoft.Aspire install.
    --os OS                     Override OS detection (win, linux, linux-musl, osx)
    --arch ARCH                 Override architecture detection (x64, arm64)
    --hive-only                 For installs from archives only: only install NuGet packages to the hive, skip CLI download
    --skip-extension.           Skip VS Code extension download and installation
    --use-insiders              Install extension to VS Code Insiders instead of VS Code
    --skip-path                 Do not add the install path to PATH environment variable (useful for portable installs)
    -v, --verbose               Enable verbose output
    -k, --keep-archive          Keep downloaded archive files after installation
    --dry-run                   Show what would be done without performing actions
    -h, --help                  Show this help message

EXAMPLES:
    ./get-aspire-cli-pr.sh 1234
    ./get-aspire-cli-pr.sh 1234 --run-id 12345678
    ./get-aspire-cli-pr.sh --run-id 12345678
    ./get-aspire-cli-pr.sh --local-dir /path/to/artifacts
    ./get-aspire-cli-pr.sh --local-dir /path/to/artifacts --hive-label my-build
    ./get-aspire-cli-pr.sh --local-dir artifacts/bin/Aspire.Cli/Debug/net10.0
    ./get-aspire-cli-pr.sh 1234 --install-path ~/my-aspire
    ./get-aspire-cli-pr.sh 1234 --os linux --arch arm64 --verbose
    ./get-aspire-cli-pr.sh 1234 --hive-only
    ./get-aspire-cli-pr.sh 1234 --skip-extension
    ./get-aspire-cli-pr.sh 1234 --use-insiders
    ./get-aspire-cli-pr.sh 1234 -m tool
    ./get-aspire-cli-pr.sh 1234 --install-mode tool --force
    ./get-aspire-cli-pr.sh 1234 --install-mode homebrew
    ./get-aspire-cli-pr.sh 1234 --install-mode winget
    ./get-aspire-cli-pr.sh --local-dir /path/to/artifacts --install-mode tool
    ./get-aspire-cli-pr.sh 1234 --skip-path
    ./get-aspire-cli-pr.sh 1234 --dry-run

    curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- <PR_NUMBER>

REQUIREMENTS:
    - GitHub CLI (gh) must be installed and authenticated (not needed with --local-dir)
    - In tool mode (--install-mode tool), the .NET SDK 'dotnet' command must be available in PATH
    - In Homebrew mode (--install-mode homebrew), Homebrew must be available on macOS
    - In WinGet mode (--install-mode winget), PowerShell and WinGet must be available on Windows
    - Permissions to download artifacts from the target repository
    - VS Code extension installation requires VS Code CLI (code) to be available in PATH

ENVIRONMENT VARIABLES:
    ASPIRE_REPO            Override repository (owner/name). Default: microsoft/aspire
                           Example: export ASPIRE_REPO=myfork/aspire

EOF
}

# Function to parse command line arguments
parse_args() {
    # Check for help flag first (can be anywhere in arguments)
    for arg in "$@"; do
        if [[ "$arg" == "-h" || "$arg" == "--help" ]]; then
            SHOW_HELP=true
            return 0  # Exit early, help will be handled in main
        fi
    done

    # Check that at least one argument is provided
    if [[ $# -lt 1 ]]; then
        say_error "At least one argument is required. Provide a PR number or --run-id <ID>."
        say_info "Use --help for usage information."
        exit 1
    fi

    # First argument can be a PR number, --run-id, or --local-dir for direct artifact installation
    if [[ "$1" == "--run-id" || "$1" == "-r" ]]; then
        # No PR number — install directly from workflow run ID
        PR_NUMBER=""
    elif [[ "$1" == "--local-dir" ]]; then
        # No PR number — install from local directory
        PR_NUMBER=""
    elif [[ "$1" == --* ]]; then
        say_error "First argument must be a PR number, --run-id <ID>, or --local-dir <PATH>. Got: '$1'"
        say_info "Use --help for usage information."
        exit 1
    elif [[ "$1" =~ ^[1-9][0-9]*$ ]]; then
        PR_NUMBER="$1"
        shift
    else
        say_error "First argument must be a valid PR number, --run-id <ID>, or --local-dir <PATH>"
        say_info "Use --help for usage information."
        exit 1
    fi

    while [[ $# -gt 0 ]]; do
        case $1 in
            --run-id|-r)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                # Validate that the run ID is a number
                if [[ ! "$2" =~ ^[0-9]+$ ]]; then
                    say_error "Run ID must be a number. Got: '$2'"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                WORKFLOW_RUN_ID="$2"
                shift 2
                ;;
            --local-dir)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                LOCAL_DIR="$2"
                shift 2
                ;;
            --hive-label)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                HIVE_LABEL="$2"
                shift 2
                ;;
            -i|--install-path)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                INSTALL_PREFIX="$2"
                INSTALL_PREFIX_EXPLICIT=true
                shift 2
                ;;
            -m|--install-mode)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                case "$2" in
                    archive|tool|winget|homebrew)
                        INSTALL_MODE="$2"
                        ;;
                    *)
                        say_error "Invalid value for --install-mode: '$2'. Allowed: archive, tool, winget, homebrew"
                        say_info "Use --help for usage information."
                        exit 1
                        ;;
                esac
                shift 2
                ;;
            --force)
                FORCE=true
                shift
                ;;
            --os)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                OS_ARG="$2"
                shift 2
                ;;
            --arch)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                ARCH_ARG="$2"
                shift 2
                ;;
            -k|--keep-archive)
                KEEP_ARCHIVE=true
                shift
                ;;
            --hive-only)
                HIVE_ONLY=true
                shift
                ;;
            --skip-extension)
                SKIP_EXTENSION_INSTALL=true
                shift
                ;;
            --use-insiders)
                USE_INSIDERS=true
                shift
                ;;
            --skip-path)
                SKIP_PATH=true
                shift
                ;;
            --dry-run)
                DRY_RUN=true
                shift
                ;;
            -v|--verbose)
                VERBOSE=true
                shift
                ;;
            *)
                say_error "Unknown option '$1'"
                say_info "Use --help for usage information."
                exit 1
                ;;
        esac
    done
}

# =============================================================================
# START: Shared code
# =============================================================================

# Function for verbose logging
say_verbose() {
    if [[ "$VERBOSE" == true ]]; then
        echo -e "${YELLOW}$1${RESET}" >&2
    fi
}

say_error() {
    echo -e "${RED}Error: $1${RESET}" >&2
}

say_warn() {
    echo -e "${YELLOW}Warning: $1${RESET}" >&2
}

say_info() {
    echo -e "$1" >&2
}

say_success() {
    echo -e "${GREEN}$1${RESET}" >&2
}

detect_os() {
    local uname_s
    uname_s=$(uname -s)

    case "$uname_s" in
        Darwin*)
            printf "osx"
            ;;
        Linux*)
            # Check if it's musl-based (Alpine, etc.)
            if command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -q musl; then
                printf "linux-musl"
            else
                printf "linux"
            fi
            ;;
        CYGWIN*|MINGW*|MSYS*)
            printf "win"
            ;;
        *)
            printf "unsupported"
            return 1
            ;;
    esac
}

# Function to validate and normalize architecture
get_cli_architecture_from_architecture() {
    local architecture="$1"

    if [[ "$architecture" == "<auto>" ]]; then
        architecture=$(detect_architecture)
    fi

    case "$(echo "$architecture" | tr '[:upper:]' '[:lower:]')" in
        amd64|x64)
            printf "x64"
            ;;
        arm64)
            printf "arm64"
            ;;
        *)
            say_error "Architecture $architecture not supported. If you think this is a bug, report it at https://github.com/microsoft/aspire/issues"
            return 1
            ;;
    esac
}

detect_architecture() {
    local uname_m
    uname_m=$(uname -m)

    case "$uname_m" in
        x86_64|amd64)
            printf "x64"
            ;;
        aarch64|arm64)
            printf "arm64"
            ;;
        *)
            say_error "Architecture $uname_m not supported. If you think this is a bug, report it at https://github.com/microsoft/aspire/issues"
            return 1
            ;;
    esac
}

# Function to compute the Runtime Identifier (RID)
get_runtime_identifier() {
    # set target_os to $1 and default to HOST_OS
    local target_os="$1"
    local target_arch="$2"

    if [[ -z "$target_os" ]]; then
        target_os=$HOST_OS
    fi

    if [[ -z "$target_arch" ]]; then
        if ! target_arch=$(get_cli_architecture_from_architecture "<auto>"); then
            return 1
        fi
    else
        if ! target_arch=$(get_cli_architecture_from_architecture "$target_arch"); then
            return 1
        fi
    fi

    printf "%s" "${target_os}-${target_arch}"
}

# Create a temporary directory with a prefix. Honors DRY_RUN
new_temp_dir() {
    local prefix="$1"
    if [[ "$DRY_RUN" == true ]]; then
        printf "/tmp/%s-whatif" "$prefix"
        return 0
    fi
    local dir
    if ! dir=$(mktemp -d -t "${prefix}-XXXXXXXX"); then
        say_error "Unable to create temporary directory"
        return 1
    fi
    say_verbose "Creating temporary directory: $dir"
    printf "%s" "$dir"
}

# Remove a temporary directory unless KEEP_ARCHIVE is set. Honors DRY_RUN
remove_temp_dir() {
    local dir="$1"
    if [[ -z "$dir" || ! -d "$dir" ]]; then
        return 0
    fi
    if [[ "$DRY_RUN" == true ]]; then
        return 0
    fi
    if [[ "$KEEP_ARCHIVE" != true ]]; then
        say_verbose "Cleaning up temporary files..."
        rm -rf "$dir" || say_warn "Failed to clean up temporary directory: $dir"
    else
        printf "Archive files kept in: %s\n" "$dir"
    fi
}

# Function to install/unpack archive files
install_archive() {
    local archive_file="$1"
    local destination_path="$2"

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would install archive $archive_file to $destination_path"
        return 0
    fi

    say_verbose "Installing archive to: $destination_path"

    # Create install directory if it doesn't exist
    if [[ ! -d "$destination_path" ]]; then
        say_verbose "Creating install directory: $destination_path"
        mkdir -p "$destination_path"
    fi

    # Check archive format and extract accordingly
    if [[ "$archive_file" =~ \.zip$ ]]; then
        if ! command -v unzip >/dev/null 2>&1; then
            say_error "unzip command not found. Please install unzip to extract ZIP files."
            return 1
        fi
        if ! unzip -o "$archive_file" -d "$destination_path"; then
            say_error "Failed to extract ZIP archive: $archive_file"
            return 1
        fi
    elif [[ "$archive_file" =~ \.tar\.gz$ ]]; then
        if ! command -v tar >/dev/null 2>&1; then
            say_error "tar command not found. Please install tar to extract tar.gz files."
            return 1
        fi
        if ! tar -xzf "$archive_file" -C "$destination_path"; then
            say_error "Failed to extract tar.gz archive: $archive_file"
            return 1
        fi
    else
        say_error "Unsupported archive format: $archive_file. Only .zip and .tar.gz files are supported."
        return 1
    fi

    say_verbose "Successfully installed archive"
}

# Function to add PATH to shell configuration file
# Parameters:
#   $1 - config_file: Path to the shell configuration file
#   $2 - bin_path: The binary path to add to PATH
#   $3 - command: The command to add to the configuration file
path_contains() {
    local bin_path="$1"
    [[ ":$PATH:" == *":$bin_path:"* ]]
}

add_to_path()
{
    local config_file="$1"
    local bin_path="$2"
    local command="$3"

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would check if $bin_path is already in \$PATH"
        say_info "[DRY RUN] Would add '$command' to $config_file if not already present"
        return 0
    fi

    if path_contains "$bin_path"; then
        say_info "Path $bin_path already exists in \$PATH, skipping addition"
    elif [[ -f "$config_file" ]] && grep -Fxq "$command" "$config_file"; then
        say_info "Command already exists in $config_file, skipping addition"
    elif [[ -w $config_file ]]; then
        echo -e "\n# Added by get-aspire-cli*.sh script" >> "$config_file"
        echo "$command" >> "$config_file"
        say_info "Successfully added aspire to \$PATH in $config_file"
    else
        say_info "Manually add the following to $config_file (or similar):"
        say_info "  $command"
    fi
}

# Function to add PATH to shell profile
add_to_shell_profile() {
    local bin_path="$1"
    local bin_path_unexpanded="$2"
    local xdg_config_home="${XDG_CONFIG_HOME:-$HOME/.config}"

    # Detect the current shell
    local shell_name

    # Try to get shell from SHELL environment variable
    if [[ -n "${SHELL:-}" ]]; then
        shell_name=$(basename "$SHELL")
    else
        # Fallback to detecting from process
        shell_name=$(ps -p $$ -o comm= 2>/dev/null || echo "sh")
    fi

    # Normalize shell name
    case "$shell_name" in
        bash|zsh|fish)
            ;;
        sh|dash|ash)
            shell_name="sh"
            ;;
        *)
            # Default to bash for unknown shells
            shell_name="bash"
            ;;
    esac

    say_verbose "Detected shell: $shell_name"

    local config_files
    case "$shell_name" in
        bash)
            config_files="$HOME/.bashrc $HOME/.bash_profile $HOME/.profile $xdg_config_home/bash/.bashrc $xdg_config_home/bash/.bash_profile"
            ;;
        zsh)
            config_files="$HOME/.zshrc $HOME/.zshenv $xdg_config_home/zsh/.zshrc $xdg_config_home/zsh/.zshenv"
            ;;
        fish)
            config_files="$HOME/.config/fish/config.fish"
            ;;
        sh)
            config_files="$HOME/.profile /etc/profile"
            ;;
        *)
            # Default to bash files for unknown shells
            config_files="$HOME/.bashrc $HOME/.bash_profile $HOME/.profile"
            ;;
    esac

    # Get the appropriate shell config file
    local config_file=""

    # Find the first existing config file
    for file in $config_files; do
        if [[ -f "$file" ]]; then
            config_file="$file"
            break
        fi
    done

    if [[ -z $config_file ]]; then
        say_warn "No existing shell profile file found for $shell_name (checked: $config_files). Not adding to PATH automatically."
        say_info "Add Aspire CLI to PATH manually by adding:"
        say_info "  export PATH=\"$bin_path_unexpanded:\$PATH\""
        return 0
    fi

    case "$shell_name" in
        bash|zsh|sh)
            add_to_path "$config_file" "$bin_path" "export PATH=\"$bin_path_unexpanded:\$PATH\""
            ;;
        fish)
            add_to_path "$config_file" "$bin_path" "fish_add_path $bin_path_unexpanded"
            ;;
        *)
            say_error "Unsupported shell type $shell_name. Please add the path $bin_path_unexpanded manually to \$PATH in your profile."
            return 1
            ;;
    esac

    if [[ "$DRY_RUN" != true ]]; then
        printf "\nTo use the Aspire CLI in new terminal sessions, restart your terminal or run:\n"
        say_info "  source $config_file"
    fi

    return 0
}

# =============================================================================
# END: Shared code
# =============================================================================

# Function to check if gh command is available
check_gh_dependency() {
    if ! command -v gh >/dev/null 2>&1; then
        say_error "GitHub CLI (gh) is required but not installed. Please install it first."
        say_info "Installation instructions: https://cli.github.com/"
        return 1
    fi

    if ! gh_version_output=$(gh --version 2>&1); then
        say_error "GitHub CLI (gh) command failed: $gh_version_output"
        return 1
    fi

    say_verbose "GitHub CLI (gh) found: $(echo "$gh_version_output" | head -1)"
}

# Function to make GitHub API calls with proper error handling
# Parameters:
#   $1 - endpoint: The GitHub API endpoint (e.g., "repos/microsoft/aspire/pulls/123")
#   $2 - jq_filter: Optional jq filter to apply to the response (e.g., ".head.sha")
#   $3 - error_message: Optional custom error message prefix
# Returns:
#   0 on success (output written to stdout)
#   1 on failure (error message written to stderr)
gh_api_call() {
    local endpoint="$1"
    local jq_filter="${2:-}"
    local error_message="${3:-Failed to call GitHub API}"
    local gh_cmd=(gh api "$endpoint")
    if [[ -n "$jq_filter" ]]; then
        gh_cmd+=(--jq "$jq_filter")
    fi
    say_verbose "Calling GitHub API: ${gh_cmd[*]}"
    local api_output
    if ! api_output=$("${gh_cmd[@]}" 2>&1); then
        say_error "$error_message (API endpoint: $endpoint): $api_output"
        return 1
    fi
    printf "%s" "$api_output"
}

# Function to get PR head SHA
get_pr_head_sha() {
    local pr_number="$1"

    say_verbose "Getting HEAD SHA for PR #$pr_number"

    local repo_owner repo_name
    if [[ "$REPO" =~ ^([^/]+)/([^/]+)$ ]]; then
        repo_owner="${BASH_REMATCH[1]}"
        repo_name="${BASH_REMATCH[2]}"
    else
        say_error "Invalid repository format '$REPO'. Expected 'owner/name'."
        exit 1
    fi

    local graphql_query='query($owner:String!, $name:String!, $number:Int!) { repository(owner:$owner, name:$name) { pullRequest(number:$number) { headRefOid } } }'
    local gh_cmd=(gh api graphql -f query="$graphql_query" -f owner="$repo_owner" -f name="$repo_name" -F number="$pr_number" --jq ".data.repository.pullRequest.headRefOid")

    say_verbose "Calling GitHub API: ${gh_cmd[*]}"

    local head_sha
    if head_sha=$("${gh_cmd[@]}" 2>/dev/null) && [[ -n "$head_sha" && "$head_sha" != "null" ]]; then
        # GraphQL succeeded with a valid SHA
        :
    else
        say_verbose "GraphQL PR head lookup failed or returned empty, falling back to REST API"

        if ! head_sha=$(gh_api_call "${GH_REPOS_BASE}/pulls/$pr_number" ".head.sha" "Failed to get HEAD SHA for PR #$pr_number using REST fallback"); then
            say_info "This could mean:"
            say_info "  - The PR number does not exist"
            say_info "  - You don't have access to the repository"
            exit 1
        fi
    fi

    if [[ -z "$head_sha" || "$head_sha" == "null" ]]; then
        say_error "Could not retrieve HEAD SHA for PR #$pr_number"
        exit 1
    fi

    say_verbose "PR #$pr_number HEAD SHA: $head_sha"
    printf "%s" "$head_sha"
}

# Function to extract version suffix from downloaded NuGet packages
extract_version_suffix_from_packages() {
    local download_dir="$1"

    if [[ "$DRY_RUN" == true ]]; then
        # Return a non-PR-shaped sentinel so the --local-dir auto-detect regex at the
        # call site (^pr\.([0-9]+)\.[0-9a-g]+$) does NOT match and the caller falls
        # through to hive_label="local". A "pr.<N>.gSHA"-shaped mock would always
        # match and force hive_label="pr-1234" in every dry-run, regardless of what
        # is actually in --local-dir.
        printf "local"
        return 0
    fi

    # Look for any .nupkg file and extract version from its name
    local nupkg_file
    nupkg_file=$(find "$download_dir" -name "*.nupkg" | head -1)

    if [[ -z "$nupkg_file" ]]; then
        say_verbose "No .nupkg files found to extract version from"
        return 1
    fi

    local filename
    filename=$(basename "$nupkg_file")
    say_verbose "Extracting version from package: $filename"

    # Extract version from package name using a more robust two-step approach
    # First remove the .nupkg extension, then extract the version part
    local base_name="${filename%.nupkg}"
    local version

    # Look for semantic version pattern with PR suffix (more specific and robust)
    version=$(echo "$base_name" | sed -En 's/.*\.([0-9]+\.[0-9]+\.[0-9]+-pr\.[0-9]+\.[a-g0-9]+)/\1/p')

    if [[ -z "$version" ]]; then
        say_verbose "Could not extract version from package name: $filename"
        return 1
    fi

    say_verbose "Extracted full version: $version"

    # Extract just the PR suffix part using bash regex for better compatibility
    if [[ "$version" =~ (pr\.[0-9]+\.[a-g0-9]+) ]]; then
        local version_suffix="${BASH_REMATCH[1]}"
        printf "%s" "$version_suffix"
    else
        say_verbose "Package version does not contain PR suffix: $version"
        return 1
    fi
}

# =============================================================================
# Tool-mode helpers (installing the Aspire.Cli dotnet tool)
# =============================================================================

# Verify that 'dotnet' is available in PATH (required for tool mode).
check_dotnet_dependency() {
    if ! command -v dotnet >/dev/null 2>&1; then
        say_error "The .NET SDK 'dotnet' command is required for --install-mode tool but was not found in PATH."
        say_info "Install the .NET SDK from https://dotnet.microsoft.com/download and ensure 'dotnet' is on your PATH."
        return 1
    fi
    say_verbose "dotnet found: $(dotnet --version 2>/dev/null || echo unknown)"
    return 0
}

# Find the unique Aspire.Cli.<version>.nupkg in the given directory and print its exact version.
# Fails fast if zero or more than one matching package is present.
find_aspire_cli_package_version() {
    local search_dir="${1:-}"

    if [[ -z "$search_dir" ]]; then
        say_error "Cannot find Aspire.Cli package: search directory is empty"
        return 1
    fi

    if [[ "$DRY_RUN" == true && ! -d "$search_dir" ]]; then
        say_info "[DRY RUN] Would discover Aspire.Cli package version under: $search_dir"
        printf "13.3.0-pr.1234.a1b2c3d4"
        return 0
    fi

    if [[ ! -d "$search_dir" ]]; then
        say_error "Cannot find Aspire.Cli package: directory does not exist: $search_dir"
        return 1
    fi

    local -a matches=()
    local f base ver
    while IFS= read -r -d '' f; do
        base=$(basename "$f")
        if [[ "$base" =~ ^Aspire\.Cli\.(win|linux|linux-musl|osx)-(x64|arm64)\. ]]; then
            continue
        fi

        local version="${base#Aspire.Cli.}"
        version="${version%.nupkg}"
        if [[ "$base" == Aspire.Cli.*.nupkg && "$version" =~ ^[0-9A-Za-z.-]+$ ]]; then
            matches+=("$f")
        fi
    done < <(find "$search_dir" -type f -name 'Aspire.Cli.*.nupkg' ! -name '*.symbols.nupkg' ! -name '*.snupkg' -print0 | sort -z)

    if [[ ${#matches[@]} -eq 0 ]]; then
        say_error "No Aspire.Cli.<version>.nupkg package found under: $search_dir"
        say_info "Tool mode requires the Aspire.Cli dotnet tool package to be present in the package source."
        return 1
    fi
    if [[ ${#matches[@]} -gt 1 ]]; then
        say_error "Multiple Aspire.Cli.<version>.nupkg packages found (expected exactly one):"
        printf '  %s\n' "${matches[@]}" >&2
        return 1
    fi

    base=$(basename "${matches[0]}")
    # Strip 'Aspire.Cli.' prefix and '.nupkg' suffix to get exact version.
    ver="${base#Aspire.Cli.}"
    ver="${ver%.nupkg}"
    if [[ -z "$ver" ]]; then
        say_error "Failed to parse version from Aspire.Cli package filename: $base"
        return 1
    fi
    printf "%s" "$ver"
}

# Install or update the Aspire.Cli dotnet tool from the populated hive.
# Parameters:
#   $1 - hive_dir: directory containing nupkg files used as --add-source
#   $2 - tool_path: optional directory for --tool-path installs
install_or_update_aspire_cli_tool() {
    local hive_dir="$1"
    local tool_path="${2:-}"

    local version
    if ! version=$(find_aspire_cli_package_version "$hive_dir"); then
        return 1
    fi

    local tool_action
    local -a cmd
    local -a install_location_args
    if [[ -n "$tool_path" ]]; then
        install_location_args=(--tool-path "$tool_path")
    else
        install_location_args=(--global)
    fi

    if [[ "$FORCE" == true ]]; then
        tool_action="update"
        cmd=(dotnet tool update "${install_location_args[@]}" Aspire.Cli --version "$version" --add-source "$hive_dir" --allow-downgrade)
    else
        tool_action="install"
        cmd=(dotnet tool install "${install_location_args[@]}" Aspire.Cli --version "$version" --add-source "$hive_dir")
    fi

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would run: ${cmd[*]}"
        return 0
    fi

    say_info "Installing Aspire.Cli dotnet tool (version $version) from $hive_dir"
    say_verbose "Running: ${cmd[*]}"

    if ! "${cmd[@]}"; then
        say_error "Failed to $tool_action Aspire.Cli dotnet tool from $hive_dir"
        if [[ "$FORCE" != true ]]; then
            say_info "If Aspire.Cli is already installed or this PR version needs to replace an existing install, re-run with --force."
        fi
        return 1
    fi

    say_success "Aspire.Cli dotnet tool installed (version $version)"
    return 0
}

validate_tool_mode_runtime_identifier() {
    local target_os="${OS_ARG:-$HOST_OS}"
    local target_arch
    if [[ -n "$ARCH_ARG" ]]; then
        if ! target_arch=$(get_cli_architecture_from_architecture "$ARCH_ARG"); then
            return 1
        fi
    elif ! target_arch=$(get_cli_architecture_from_architecture "<auto>"); then
        return 1
    fi

    local host_arch
    if ! host_arch=$(get_cli_architecture_from_architecture "<auto>"); then
        return 1
    fi

    if [[ "$target_os" != "$HOST_OS" || "$target_arch" != "$host_arch" ]]; then
        say_error "--install-mode tool cannot target ${target_os}-${target_arch} from this ${HOST_OS}-${host_arch} host."
        say_info "dotnet tool install resolves RID-specific packages for the current host. Run tool mode on the target machine, or use archive mode for cross-RID downloads."
        return 1
    fi

    return 0
}

# Function to find workflow run for SHA
find_workflow_run() {
    local head_sha="$1"

    # https://docs.github.com/en/rest/actions/workflow-runs?apiVersion=2022-11-28#list-workflow-runs-for-a-repository
    say_verbose "Finding ci.yml workflow run for SHA: $head_sha"

    local workflow_run_id
    if ! workflow_run_id=$(gh_api_call "${GH_REPOS_BASE}/actions/workflows/ci.yml/runs?event=pull_request&head_sha=$head_sha" ".workflow_runs | sort_by(.created_at, .updated_at) | reverse | .[0].id" "Failed to query workflow runs for SHA: $head_sha"); then
        return 1
    fi

    if [[ -z "$workflow_run_id" || "$workflow_run_id" == "null" ]]; then
        say_error "No ci.yml workflow run found for PR SHA: $head_sha. This could mean no workflow has been triggered for this SHA $head_sha . Check at https://github.com/${REPO}/actions/workflows/ci.yml"
        return 1
    fi

    say_verbose "Found workflow run ID: $workflow_run_id"
    printf "%s" "$workflow_run_id"
}

# Function to download built-nugets artifact
download_built_nugets() {
    # Parameters:
    #   $1 - workflow_run_id
    #   $2 - rid (e.g. osx-arm64)
    #   $3 - temp_dir
    local workflow_run_id="$1"
    local rid="$2"
    local temp_dir="$3"

    local download_dir="${temp_dir}/built-nugets"
    local nugets_download_command=(gh run download "$workflow_run_id" -R "$REPO" --name "$BUILT_NUGETS_ARTIFACT_NAME" -D "$download_dir")
    local nugets_rid_filename="$BUILT_NUGETS_RID_ARTIFACT_NAME-${rid}"
    local nugets_rid_download_command=(gh run download "$workflow_run_id" -R "$REPO" --name "$nugets_rid_filename" -D "$download_dir")

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would download built nugets with: ${nugets_download_command[*]}"
        say_info "[DRY RUN] Would download rid specific built nugets with: ${nugets_rid_download_command[*]}"
        printf "%s" "$download_dir"
        return 0
    fi

    say_info "Downloading built nuget artifacts - $BUILT_NUGETS_ARTIFACT_NAME"
    say_verbose "Downloading with: ${nugets_download_command[*]}"

    if ! "${nugets_download_command[@]}"; then
        say_verbose "gh run download command failed. Command: ${nugets_download_command[*]}"
        say_error "Failed to download artifact '$BUILT_NUGETS_ARTIFACT_NAME' from run: $workflow_run_id . If the workflow is still running then the artifact named '$BUILT_NUGETS_ARTIFACT_NAME' may not be available yet. Check at https://github.com/${REPO}/actions/runs/$workflow_run_id#artifacts"
        return 1
    fi

    say_info "Downloading rid specific built nugets artifact - $nugets_rid_filename ..."
    say_verbose "Downloading with: ${nugets_rid_download_command[*]}"

    if ! "${nugets_rid_download_command[@]}"; then
        say_verbose "gh run download command failed. Command: ${nugets_rid_download_command[*]}"
        say_error "Failed to download artifact '$nugets_rid_filename' from run: $workflow_run_id . If the workflow is still running then the artifact named '$nugets_rid_filename' may not be available yet. Check at https://github.com/${REPO}/actions/runs/$workflow_run_id#artifacts"
        return 1
    fi

    say_verbose "Successfully downloaded nuget packages to: $download_dir"
    printf "%s" "$download_dir"
    return 0
}

# Function to install built-nugets
install_built_nugets() {
    local download_dir="$1"
    local nuget_install_dir="$2"

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would copy nugets to $nuget_install_dir"
        return 0
    fi

    # Remove and recreate the target directory to ensure clean state
    if [[ -d "$nuget_install_dir" ]]; then
        say_verbose "Removing existing nuget directory: $nuget_install_dir"
        rm -rf "$nuget_install_dir"
    fi
    mkdir -p "$nuget_install_dir"

    say_verbose "Copying nugets from $download_dir to $nuget_install_dir"

    # Copy all files from the artifact directory to the target directory
    if ! find "$download_dir" -name "*.nupkg" -exec cp -R {} "$nuget_install_dir"/ \;; then
        say_error "Failed to copy nuget artifact files"
        return 1
    fi

    say_verbose "Successfully installed nuget packages to: $nuget_install_dir"
    say_info "NuGet packages successfully installed to: ${GREEN}$nuget_install_dir${RESET}"
    return 0
}

download_aspire_cli() {
    # Parameters:
    #   $1 - workflow_run_id
    #   $2 - rid
    #   $3 - temp_dir
    local workflow_run_id="$1"
    local rid="$2"
    local temp_dir="$3"
    local cli_archive_name
    cli_archive_name="$CLI_ARCHIVE_ARTIFACT_NAME_PREFIX-${rid}"

    local download_dir="${temp_dir}/cli"
    local download_command=(gh run download "$workflow_run_id" -R "$REPO" --name "$cli_archive_name" -D "$download_dir")
    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would download $cli_archive_name with: ${download_command[*]}"
        printf "%s" "/tmp/fake-cli-path"
        return 0
    fi

    say_info "Downloading CLI from GitHub ..."
    say_verbose "Downloading with ${download_command[*]}"

    if ! "${download_command[@]}"; then
        say_verbose "gh run download command failed. Command: ${download_command[*]}"
    say_error "Failed to download artifact '$cli_archive_name' from run: $workflow_run_id . If the workflow is still running then the artifact named '$cli_archive_name' may not be available yet. Check at https://github.com/${REPO}/actions/runs/$workflow_run_id#artifacts"
        return 1
    fi

    local cli_archive_path
    local -a cli_files=()

    # Recursively search for CLI archives (.tar.gz or .zip) anywhere inside the artifact.
    # We purposefully limit to filenames starting with the configured prefix to avoid grabbing unrelated archives.
    # Using find instead of shell globs allows us to traverse subdirectories created by GitHub after compression.
    while IFS= read -r -d '' f; do
        cli_files+=("$f")
    done < <(find "$download_dir" -type f \( -name "${ASPIRE_CLI_ARTIFACT_NAME_PREFIX}-*.tar.gz" -o -name "${ASPIRE_CLI_ARTIFACT_NAME_PREFIX}-*.zip" \) -print0 | sort -z)

    if [[ ${#cli_files[@]} -eq 0 ]]; then
        say_error "No CLI archive found. Expected a single ${ASPIRE_CLI_ARTIFACT_NAME_PREFIX}-*.tar.gz or ${ASPIRE_CLI_ARTIFACT_NAME_PREFIX}-*.zip file anywhere under: $download_dir"
        say_info "Showing up to first 25 candidate regular files under artifact (for debugging):"
        find "$download_dir" -type f | head -25 | sed 's/^/  /'
        return 1
    fi
    if [[ ${#cli_files[@]} -gt 1 ]]; then
        say_error "Multiple CLI archives found (expected exactly one). Matches:"
        printf '  %s\n' "${cli_files[@]}"
        return 1
    fi
    cli_archive_path="${cli_files[0]}"
    say_verbose "Detected CLI archive: $cli_archive_path"

    # Export the path for the caller to use
    printf "%s" "$cli_archive_path"
    return 0
}

is_installer_mode() {
    [[ "$INSTALL_MODE" == "winget" || "$INSTALL_MODE" == "homebrew" ]]
}

script_manages_cli_path() {
    [[ "$INSTALL_MODE" == "archive" || ("$INSTALL_MODE" == "tool" && "$INSTALL_PREFIX_EXPLICIT" == true) ]]
}

get_installer_artifact_name() {
    case "$INSTALL_MODE" in
        winget)
            printf '%s' "$WINGET_MANIFEST_ARTIFACT_NAME"
            ;;
        homebrew)
            printf '%s' "$HOMEBREW_CASK_ARTIFACT_NAME"
            ;;
        *)
            say_error "Install mode '$INSTALL_MODE' does not use an installer artifact."
            return 1
            ;;
    esac
}

get_installer_archive_rids() {
    case "$INSTALL_MODE" in
        winget)
            printf '%s\n' "win-x64" "win-arm64"
            ;;
        homebrew)
            printf '%s\n' "osx-arm64" "osx-x64"
            ;;
        *)
            say_error "Install mode '$INSTALL_MODE' does not use native installer archives."
            return 1
            ;;
    esac
}

download_artifact_by_name() {
    local workflow_run_id="$1"
    local artifact_name="$2"
    local download_dir="$3"
    local download_command=(gh run download "$workflow_run_id" -R "$REPO" --name "$artifact_name" -D "$download_dir")

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would download $artifact_name with: ${download_command[*]}"
        return 0
    fi

    say_info "Downloading artifact - $artifact_name ..."
    say_verbose "Downloading with: ${download_command[*]}"

    if ! "${download_command[@]}"; then
        say_verbose "gh run download command failed. Command: ${download_command[*]}"
        say_error "Failed to download artifact '$artifact_name' from run: $workflow_run_id . If the workflow is still running then the artifact named '$artifact_name' may not be available yet. Check at https://github.com/${REPO}/actions/runs/$workflow_run_id#artifacts"
        return 1
    fi

    return 0
}

download_installer_artifacts() {
    local workflow_run_id="$1"
    local temp_dir="$2"
    local installer_artifact_name
    installer_artifact_name="$(get_installer_artifact_name)"

    INSTALLER_ARTIFACT_DIR="$temp_dir/installer-$INSTALL_MODE"
    INSTALLER_ARCHIVE_ROOT="$temp_dir/installer-native-archives"

    if ! download_artifact_by_name "$workflow_run_id" "$installer_artifact_name" "$INSTALLER_ARTIFACT_DIR"; then
        return 1
    fi

    local rid
    while IFS= read -r rid; do
        if [[ -z "$rid" ]]; then
            continue
        fi

        if ! download_artifact_by_name "$workflow_run_id" "$CLI_ARCHIVE_ARTIFACT_NAME_PREFIX-$rid" "$INSTALLER_ARCHIVE_ROOT"; then
            return 1
        fi
    done < <(get_installer_archive_rids)

    say_verbose "Downloaded installer artifact to: $INSTALLER_ARTIFACT_DIR"
    say_verbose "Downloaded native installer archives to: $INSTALLER_ARCHIVE_ROOT"
    return 0
}

find_installer_dogfood_script() {
    local artifact_dir="$1"
    local script_name
    local companion_pattern

    case "$INSTALL_MODE" in
        winget)
            script_name="dogfood.ps1"
            companion_pattern="*.installer.yaml"
            ;;
        homebrew)
            script_name="dogfood.sh"
            companion_pattern="aspire.rb"
            ;;
        *)
            say_error "Install mode '$INSTALL_MODE' does not use a dogfood script."
            return 1
            ;;
    esac

    local -a matches=()
    local f
    while IFS= read -r -d '' f; do
        if find "$(dirname "$f")" -maxdepth 1 -type f -name "$companion_pattern" | grep -q .; then
            matches+=("$f")
        fi
    done < <(find "$artifact_dir" -type f -name "$script_name" -print0 | sort -z)

    if [[ "${#matches[@]}" -eq 0 ]]; then
        say_error "Could not find $script_name co-located with $companion_pattern under: $artifact_dir"
        return 1
    fi

    if [[ "${#matches[@]}" -gt 1 ]]; then
        say_error "Found multiple $script_name files co-located with $companion_pattern under $artifact_dir:"
        printf '  %s\n' "${matches[@]}" >&2
        return 1
    fi

    printf '%s' "${matches[0]}"
    return 0
}

install_with_installer_artifact() {
    local artifact_dir="$1"
    local archive_root="$2"
    local dogfood_script

    if [[ "$DRY_RUN" == true ]]; then
        case "$INSTALL_MODE" in
            winget)
                dogfood_script="$artifact_dir/dogfood.ps1"
                ;;
            homebrew)
                dogfood_script="$artifact_dir/dogfood.sh"
                ;;
        esac
    else
        if ! dogfood_script="$(find_installer_dogfood_script "$artifact_dir")"; then
            return 1
        fi
    fi

    case "$INSTALL_MODE" in
        winget)
            local winget_command=(pwsh -NoProfile -ExecutionPolicy Bypass -File "$dogfood_script" -ArchiveRoot "$archive_root")
            if [[ "$FORCE" == true ]]; then
                winget_command+=(-Force)
            fi
            if [[ "$DRY_RUN" == true ]]; then
                say_info "[DRY RUN] Would install Aspire CLI with WinGet artifact: ${winget_command[*]}"
                return 0
            fi

            "${winget_command[@]}"
            ;;
        homebrew)
            local homebrew_command=(bash "$dogfood_script" --archive-root "$archive_root")
            if [[ "$DRY_RUN" == true ]]; then
                say_info "[DRY RUN] Would install Aspire CLI with Homebrew artifact: ${homebrew_command[*]}"
                return 0
            fi

            "${homebrew_command[@]}"
            ;;
        *)
            say_error "Unsupported installer mode: $INSTALL_MODE"
            return 1
            ;;
    esac
}

validate_installer_mode_environment() {
    if [[ "$DRY_RUN" == true ]]; then
        return 0
    fi

    case "$INSTALL_MODE" in
        winget)
            if [[ "$HOST_OS" != "win" ]]; then
                say_error "--install-mode winget can only be executed on Windows. Use --dry-run to preview downloads from another OS."
                return 1
            fi
            if ! command -v pwsh >/dev/null 2>&1; then
                say_error "--install-mode winget requires PowerShell (pwsh) to run the WinGet dogfood installer."
                return 1
            fi
            if ! command -v winget >/dev/null 2>&1; then
                say_error "--install-mode winget requires WinGet to install the generated manifest artifact."
                return 1
            fi
            ;;
        homebrew)
            if [[ "$HOST_OS" != "osx" ]]; then
                say_error "--install-mode homebrew can only be executed on macOS. Use --dry-run to preview downloads from another OS."
                return 1
            fi
            if ! command -v brew >/dev/null 2>&1; then
                say_error "--install-mode homebrew requires Homebrew (brew) to install the generated cask artifact."
                return 1
            fi
            ;;
    esac
}

# Function to check if VS Code CLI is available
check_vscode_cli_dependency() {
    local vscode_cmd="code"
    if [[ "$USE_INSIDERS" == true ]]; then
        vscode_cmd="code-insiders"
    fi

    if ! command -v "$vscode_cmd" >/dev/null 2>&1; then
        if [[ "$USE_INSIDERS" == true ]]; then
            say_warn "VS Code Insiders CLI (code-insiders) is not available in PATH. Extension installation will be skipped."
            say_info "To install VS Code Insiders extensions, ensure VS Code Insiders is installed and the 'code-insiders' command is available."
        else
            say_warn "VS Code CLI (code) is not available in PATH. Extension installation will be skipped."
            say_info "To install VS Code extensions, ensure VS Code is installed and the 'code' command is available."
        fi
        return 1
    fi
    return 0
}

# Function to download VS Code extension artifact
download_aspire_extension() {
    local workflow_run_id="$1"
    local temp_dir="$2"
    local download_dir="$temp_dir/extension"

    say_info "Downloading VS Code extension from GitHub - $EXTENSION_ARTIFACT_NAME ..."

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would download extension artifact: $EXTENSION_ARTIFACT_NAME"
        echo "$download_dir"
        return 0
    fi

    mkdir -p "$download_dir"
    if ! gh run download "$workflow_run_id" --name "$EXTENSION_ARTIFACT_NAME" --dir "$download_dir" --repo "$REPO"; then
        say_warn "Failed to download VS Code extension artifact"
        say_info "This could mean the extension artifact is not available for this build."
        return 1
    fi

    echo "$download_dir"
    return 0
}

# Function to install VS Code extension
install_aspire_extension() {
    local download_dir="$1"
    local vscode_cmd="code"
    if [[ "$USE_INSIDERS" == true ]]; then
        vscode_cmd="code-insiders"
    fi

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would install VS Code extension from: $download_dir using $vscode_cmd"
        return 0
    fi

    # Find the .vsix file directly (the artifact contains the .vsix file, not a zip)
    local vsix_file
    vsix_file=$(find "$download_dir" -name "*.vsix" | head -n 1)

    if [[ -z "$vsix_file" ]]; then
        say_warn "No .vsix file found in downloaded artifact"
        if [[ "$VERBOSE" == true ]]; then
            say_verbose "Files found in download directory:"
            find "$download_dir" -type f | while read -r file; do
                say_verbose "  $(basename "$file")"
            done
        fi
        return 1
    fi

    local extension_target="VS Code"
    if [[ "$USE_INSIDERS" == true ]]; then
        extension_target="VS Code Insiders"
    fi

    say_info "Installing $extension_target extension: $(basename "$vsix_file")"
    if "$vscode_cmd" --install-extension "$vsix_file"; then
        say_success "$extension_target extension successfully installed"
        return 0
    else
        say_warn "Failed to install $extension_target extension (exit code: $?)"
        return 1
    fi
}

# Function to install a raw 'dotnet build' / 'dotnet publish' CLI binary tree.
# Used by the auto-detect raw-build branch of install_from_local_dir to bypass the
# archive (.tar.gz/.zip) search & extraction. Searches recursively under "$source_dir"
# for 'aspire' or 'aspire.exe' and copies the containing directory's files into "$cli_install_dir".
install_aspire_cli_from_binary() {
    local source_dir="$1"
    local cli_install_dir="$2"

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would install raw CLI binary from $source_dir to $cli_install_dir"
        return 0
    fi

    local -a exe_paths=()
    while IFS= read -r -d '' f; do
        exe_paths+=("$f")
    done < <(find "$source_dir" -type f \( -name "aspire" -o -name "aspire.exe" \) -print0 | sort -z)

    if [[ ${#exe_paths[@]} -eq 0 ]]; then
        say_error "No 'aspire' or 'aspire.exe' executable found in: $source_dir"
        say_info "Expected raw 'dotnet build' or 'dotnet publish' output containing the aspire executable."
        say_info "Showing up to first 25 files under local directory (for debugging):"
        find "$source_dir" -type f | head -25 | sed 's/^/  /'
        return 1
    fi

    local exe_path=""
    if [[ ${#exe_paths[@]} -eq 1 ]]; then
        exe_path="${exe_paths[0]}"
    else
        # When multiple matches exist (e.g. both build and publish outputs are present),
        # prefer the 'publish' directory because it carries the full set of runtime deps.
        local p
        for p in "${exe_paths[@]}"; do
            if [[ "$p" == *"/publish/"* ]]; then
                exe_path="$p"
                break
            fi
        done
        if [[ -z "$exe_path" ]]; then
            say_error "Multiple aspire executables found under $source_dir (specify a more precise --local-dir):"
            printf '  %s\n' "${exe_paths[@]}"
            return 1
        fi
        say_verbose "Multiple aspire executables found; preferring publish output: $exe_path"
    fi

    local exe_dir
    exe_dir=$(dirname "$exe_path")
    say_verbose "Installing raw CLI binary tree from: $exe_dir"

    if [[ ! -d "$cli_install_dir" ]]; then
        say_verbose "Creating install directory: $cli_install_dir"
        mkdir -p "$cli_install_dir"
    fi

    # Copy the contents of the exe's directory (binary + runtime deps + config) into the install dir.
    # 'cp -R "$exe_dir"/. "$cli_install_dir"/' preserves attributes and copies the directory contents,
    # not the directory itself.
    if ! cp -R "$exe_dir"/. "$cli_install_dir"/; then
        say_error "Failed to copy CLI binary files from $exe_dir to $cli_install_dir"
        return 1
    fi

    local installed_exe="$cli_install_dir/$(basename "$exe_path")"
    say_info "Aspire CLI successfully installed from raw build to: ${GREEN}$installed_exe${RESET}"
    return 0
}

# Computes the CLI install directory. PR installs are isolated under
# <prefix>/dogfood/pr-<N>/bin; without PR_NUMBER, falls back to the shared
# script-route bin dir.
compute_cli_install_dir() {
    if [[ -n "$PR_NUMBER" ]]; then
        printf '%s' "$INSTALL_PREFIX/dogfood/pr-$PR_NUMBER/bin"
    else
        printf '%s' "$INSTALL_PREFIX/bin"
    fi
}

# Writes the PR-source sidecar (.aspire-install.json) next to the binary at
# <install_prefix>/dogfood/pr-<N>/bin/.aspire-install.json. The sidecar marks
# the install as PR-sourced so downstream consumers (e.g. 'aspire update')
# know not to assume the stable script source. Per-RID archives produced
# by eng/clipack are shared across routes and ship without a baked sidecar;
# this write is the PR-route's authoritative author. If a future or
# external archive ever smuggles a sidecar at the same path, this write
# overwrites it by design. Under --dry-run the script is describe-but-do-
# not-do: print the path it would write to and skip the filesystem mutation
# so a real user's sidecar is never overwritten.
write_pr_route_sidecar() {
    local install_prefix="$1"
    local pr_number="$2"

    local sidecar_dir="$install_prefix/dogfood/pr-$pr_number/bin"
    local sidecar_path="$sidecar_dir/.aspire-install.json"
    local sidecar_content='{"source":"pr"}'

    if [[ "$DRY_RUN" == true ]]; then
        printf 'DRYRUN: would write route sidecar to: %s\n' "$sidecar_path"
    else
        mkdir -p "$sidecar_dir"
        printf '%s\n' "$sidecar_content" > "$sidecar_path"
    fi
}

# Function to install downloaded CLI
install_aspire_cli() {
    local cli_archive_path="$1"
    local cli_install_dir="$2"

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would install CLI archive to: $cli_install_dir"
        # Emit the install path as an informational message that tests can parse,
        # instead of touching the filesystem.
        local binary_path="$cli_install_dir/aspire"
        printf 'DRYRUN: would install Aspire CLI binary to: %s\n' "$binary_path"
        return 0
    fi

    if ! install_archive "$cli_archive_path" "$cli_install_dir"; then
        return 1
    fi

    # Determine CLI executable name and path
    local cli_path
    # Check whether aspire.exe or aspire exists on disk, and use that
    if [[ -f "$cli_install_dir/aspire.exe" ]]; then
        cli_path="$cli_install_dir/aspire.exe"
    else
        cli_path="$cli_install_dir/aspire"
    fi

    say_info "Aspire CLI successfully installed to: ${GREEN}$cli_path${RESET}"
    return 0
}

# Main function to install from a local directory of pre-built artifacts
install_from_local_dir() {
    local local_dir="$1"

    if [[ ! -d "$local_dir" ]]; then
        say_error "Local directory does not exist: $local_dir"
        return 1
    fi

    say_info "Installing from local directory: $local_dir"

    # PR-route installs are isolated under <prefix>/dogfood/pr-<N>/bin so they
    # don't collide with the script-route prefix or with other PR installs.
    # Hives remain shared under <prefix>/hives/<label>/packages.
    local cli_install_dir
    cli_install_dir="$(compute_cli_install_dir)"
    local hive_label
    if [[ -n "$HIVE_LABEL" ]]; then
        hive_label="$HIVE_LABEL"
    else
        # Auto-detect PR identity from .nupkg filenames (e.g. "13.4.0-pr.16820.g3703c5c4")
        # so PR-built packages land in the same hive the CLI's CliExecutionContext.Channel
        # resolves to ("pr-<N>"). Falls back to "local" for true local-dev builds.
        local detected_suffix
        if detected_suffix=$(extract_version_suffix_from_packages "$local_dir") \
            && [[ "$detected_suffix" =~ ^pr\.([0-9]+)\.[0-9a-g]+$ ]]; then
            hive_label="pr-${BASH_REMATCH[1]}"
        else
            hive_label="local"
        fi
    fi
    local nuget_hive_dir="$INSTALL_PREFIX/hives/$hive_label/packages"

    say_info "Using hive label: $hive_label"

    # Compute RID
    local rid
    if ! rid=$(get_runtime_identifier "$OS_ARG" "$ARCH_ARG"); then
        return 1
    fi
    say_verbose "Computed RID: $rid"

    # Find CLI archive in local directory, use installer artifact, or auto-detect raw build output.
    if [[ "$HIVE_ONLY" == true ]]; then
        say_info "Skipping CLI installation due to --hive-only flag"
    elif [[ "$INSTALL_MODE" == "tool" ]]; then
        say_verbose "Skipping CLI archive lookup in local directory (install mode: tool)"
    elif is_installer_mode; then
        if ! install_with_installer_artifact "$local_dir" "$local_dir"; then
            return 1
        fi
    else
        local -a cli_files=()
        while IFS= read -r -d '' f; do
            cli_files+=("$f")
        done < <(find "$local_dir" -type f \( -name "${ASPIRE_CLI_ARTIFACT_NAME_PREFIX}-*.tar.gz" -o -name "${ASPIRE_CLI_ARTIFACT_NAME_PREFIX}-*.zip" \) -print0 | sort -z)

        if [[ ${#cli_files[@]} -eq 0 ]]; then
            # Auto-detect: no archive present, try raw 'dotnet build' / 'dotnet publish' output.
            local raw_exe=""
            raw_exe=$(find "$local_dir" -type f \( -name 'aspire' -o -name 'aspire.exe' \) -print -quit 2>/dev/null || true)
            if [[ -n "$raw_exe" ]]; then
                say_verbose "No CLI archive found; detected raw aspire executable at: $raw_exe (raw-build flow)"
                if ! install_aspire_cli_from_binary "$local_dir" "$cli_install_dir"; then
                    return 1
                fi
            else
                say_error "No CLI archive (${ASPIRE_CLI_ARTIFACT_NAME_PREFIX}-*.tar.gz or .zip) and no 'aspire'/'aspire.exe' executable found in: $local_dir"
                say_info "Expected either a published CLI archive or a 'dotnet build'/'dotnet publish' output directory."
                say_info "Showing up to first 25 files under local directory (for debugging):"
                find "$local_dir" -type f | head -25 | sed 's/^/  /'
                return 1
            fi
        elif [[ ${#cli_files[@]} -gt 1 ]]; then
            say_error "Multiple CLI archives found (expected exactly one). Matches:"
            printf '  %s\n' "${cli_files[@]}"
            return 1
        else
            local cli_archive_path="${cli_files[0]}"
            say_verbose "Using CLI archive: $cli_archive_path"

            if ! install_aspire_cli "$cli_archive_path" "$cli_install_dir"; then
                return 1
            fi
        fi
    fi

    # Populate the hive from the local directory. In tool mode this gives `aspire
    # new` the PR/local Aspire.AppHost.Sdk + Aspire.Hosting + Aspire.ProjectTemplates
    # so the generated project can build against the dogfood build.
    if ! install_built_nugets "$local_dir" "$nuget_hive_dir"; then
        say_error "Failed to install nuget packages from local directory"
        return 1
    fi

    # Extract and print the version suffix from packages
    local version_suffix
    if version_suffix=$(extract_version_suffix_from_packages "$local_dir"); then
        say_info "Package version suffix: $version_suffix"
    else
        say_warn "Could not extract version suffix from local packages"
    fi

    # In tool mode, install/update the dotnet tool from the populated hive (durable
    # `--add-source` for any future `dotnet tool update`).
    if [[ "$HIVE_ONLY" != true && "$INSTALL_MODE" == "tool" ]]; then
        local tool_path=""
        if [[ "$INSTALL_PREFIX_EXPLICIT" == true ]]; then
            tool_path="$cli_install_dir"
        fi
        if ! install_or_update_aspire_cli_tool "$nuget_hive_dir" "$tool_path"; then
            return 1
        fi
    fi

    # PR installs from archives get a sidecar. --local-dir installs are unmanaged, and
    # dotnet-tool packages embed their own source=dotnet-tool sidecar.
    if [[ "$HIVE_ONLY" != true && "$INSTALL_MODE" == "archive" && -n "$PR_NUMBER" ]]; then
        write_pr_route_sidecar "$INSTALL_PREFIX" "$PR_NUMBER"
    fi

}

# Main function to download and install from PR or workflow run ID
download_and_install_from_pr() {
    # Parameters:
    #   $1 - temp_dir (required)
    local temp_dir="$1"
    local head_sha workflow_run_id rid

    # If a workflow run ID was explicitly provided via arguments, use that directly.
    # (Previously this checked the uninitialized local variable 'workflow_run_id', which was always empty.)
    if [[ -n "$WORKFLOW_RUN_ID" ]]; then
        if [[ -n "$PR_NUMBER" ]]; then
            say_info "Starting download and installation for PR #$PR_NUMBER with workflow run ID: $WORKFLOW_RUN_ID"
        else
            say_info "Starting download and installation for workflow run ID: $WORKFLOW_RUN_ID"
        fi
        workflow_run_id="$WORKFLOW_RUN_ID"
    else
        if [[ -z "$PR_NUMBER" ]]; then
            say_error "Either a PR number or --run-id <ID> must be provided."
            return 1
        fi
        # When only PR number is provided, find the workflow run
        say_info "Starting download and installation for PR #$PR_NUMBER"

        # Find the workflow run
        if ! head_sha=$(get_pr_head_sha "$PR_NUMBER"); then
            return 1
        fi

        if ! workflow_run_id=$(find_workflow_run "$head_sha"); then
            return 1
        fi
    fi

    say_info "Using workflow run https://github.com/${REPO}/actions/runs/$workflow_run_id"

    # PR-route installs are isolated under <prefix>/dogfood/pr-<N>/bin so they
    # don't collide with the script-route prefix or with other PR installs.
    # Hives remain shared under <prefix>/hives/<label>/packages.
    local cli_install_dir
    cli_install_dir="$(compute_cli_install_dir)"
    local hive_label
    if [[ -n "$HIVE_LABEL" ]]; then
        hive_label="$HIVE_LABEL"
    elif [[ -n "$PR_NUMBER" ]]; then
        hive_label="pr-$PR_NUMBER"
    else
        # The installed CLI's identity (CliExecutionContext.Channel) is baked at build
        # time via AspireCliChannel — one of pr-<N>/staging/daily/local. There is no
        # 'run-<id>' channel, so packages dropped into hives/run-<id>/packages would
        # be invisible to the CLI. Reject early with actionable guidance instead of
        # silently producing an unusable layout.
        say_error "Cannot determine hive label from --run-id alone."
        say_error "The installed CLI's package channel is baked at build time (pr-<N>/staging/daily/local)"
        say_error "and will not look in a 'run-<id>' hive. Re-run with --pr-number <N> (preferred) or"
        say_error "--hive-label <label> matching the CLI's baked AspireCliChannel."
        return 1
    fi
    local nuget_hive_dir="$INSTALL_PREFIX/hives/$hive_label/packages"

    # First, download both artifacts
    local cli_archive_path nuget_download_dir
    # Compute RID once
    if ! rid=$(get_runtime_identifier "$OS_ARG" "$ARCH_ARG"); then
        return 1
    fi
    say_verbose "Computed RID: $rid"
    if [[ "$HIVE_ONLY" == true ]]; then
        say_info "Skipping CLI download due to --hive-only flag"
    elif [[ "$INSTALL_MODE" == "tool" ]]; then
        say_verbose "Skipping CLI native archive download (install mode: tool)"
    elif is_installer_mode; then
        if ! download_installer_artifacts "$workflow_run_id" "$temp_dir"; then
            return 1
        fi
    else
        if ! cli_archive_path=$(download_aspire_cli "$workflow_run_id" "$rid" "$temp_dir"); then
            return 1
        fi
    fi

    # Both modes need the cross-platform built-nugets (for the hive: Aspire.Hosting,
    # Aspire.AppHost.Sdk, Aspire.ProjectTemplates, ...) and the RID-specific one
    # (CLI archive in archive mode, or Aspire.Cli.<rid> tool pack in tool mode).
    # download_built_nugets fetches both into the same temp directory.
    if ! nuget_download_dir=$(download_built_nugets "$workflow_run_id" "$rid" "$temp_dir"); then
        say_error "Failed to download nuget packages"
        return 1
    fi

    # Extract and print the version suffix from downloaded packages
    local version_suffix
    if version_suffix=$(extract_version_suffix_from_packages "$nuget_download_dir"); then
        say_info "Package version suffix: $version_suffix"
    else
        say_warn "Could not extract version suffix from downloaded packages"
    fi

    # Download VS Code extension if not skipped
    local extension_download_dir=""
    if [[ "$SKIP_EXTENSION_INSTALL" != true ]]; then
        if extension_download_dir=$(download_aspire_extension "$workflow_run_id" "$temp_dir"); then
            say_verbose "Extension downloaded to: $extension_download_dir"
        else
            say_verbose "Extension download failed, will skip installation"
            extension_download_dir=""
        fi
    else
        say_info "Skipping VS Code extension download due to --skip-extension flag"
    fi

    # Then, install both artifacts
    say_info "Installing artifacts..."
    if [[ "$HIVE_ONLY" == true ]]; then
        say_info "Skipping CLI installation due to --hive-only flag"
    elif [[ "$INSTALL_MODE" == "tool" ]]; then
        say_verbose "Skipping CLI archive installation (install mode: tool)"
    elif is_installer_mode; then
        if ! install_with_installer_artifact "$INSTALLER_ARTIFACT_DIR" "$INSTALLER_ARCHIVE_ROOT"; then
            return 1
        fi
    else
        if ! install_aspire_cli "$cli_archive_path" "$cli_install_dir"; then
            return 1
        fi
    fi

    # Populate the PR hive with both cross-platform and RID-specific nupkgs.
    # In tool mode the hive is what `aspire new` discovers so it picks the PR
    # version of Aspire.AppHost.Sdk / Aspire.Hosting / Aspire.ProjectTemplates.
    if ! install_built_nugets "$nuget_download_dir" "$nuget_hive_dir"; then
        say_error "Failed to install nuget packages"
        return 1
    fi

    # In tool mode, install/update the dotnet tool from the populated hive.
    # Using the hive directory (rather than the temp download dir) gives `dotnet
    # tool install --add-source` a durable source, which matters if the user later
    # runs `dotnet tool update Aspire.Cli` against the same `--tool-path`.
    if [[ "$HIVE_ONLY" != true && "$INSTALL_MODE" == "tool" ]]; then
        local tool_path=""
        if [[ "$INSTALL_PREFIX_EXPLICIT" == true ]]; then
            tool_path="$cli_install_dir"
        fi
        if ! install_or_update_aspire_cli_tool "$nuget_hive_dir" "$tool_path"; then
            return 1
        fi
    fi

    # Install VS Code extension if downloaded
    if [[ -n "$extension_download_dir" && "$SKIP_EXTENSION_INSTALL" != true ]]; then
        if check_vscode_cli_dependency; then
            install_aspire_extension "$extension_download_dir"
        fi
    fi

    # Write the PR-route sidecar only for installs from archives. Tool-mode packages
    # carry their own source=dotnet-tool sidecar.
    if [[ "$HIVE_ONLY" != true && "$INSTALL_MODE" == "archive" && -n "$PR_NUMBER" ]]; then
        write_pr_route_sidecar "$INSTALL_PREFIX" "$PR_NUMBER"
    fi

}

# Main entry point — wraps everything after function definitions.
# Guarded so that `source get-aspire-cli-pr.sh` loads functions without
# executing the main flow (enables Tier-1 unit tests).
main() {
    # Parse command line arguments
    parse_args "$@"

    if [[ "$SHOW_HELP" == true ]]; then
        show_help
        exit 0
    fi

    HOST_OS=$(detect_os)

    if [[ "$HOST_OS" == "unsupported" ]]; then
        say_error "Unsupported operating system detected: $(uname -s). Supported values: win (Git Bash/MinGW/MSYS), linux, linux-musl, osx. Use --os to override when appropriate."
        exit 1
    fi

    # Validate mutually exclusive options
    if [[ -n "$LOCAL_DIR" ]]; then
        if [[ -n "$PR_NUMBER" || -n "$WORKFLOW_RUN_ID" ]]; then
            say_error "--local-dir is mutually exclusive with PR_NUMBER and --run-id"
            exit 1
        fi
    fi

    if [[ "$HIVE_ONLY" == true && "$INSTALL_MODE" != "archive" ]]; then
        say_error "--hive-only cannot be combined with --install-mode $INSTALL_MODE: --hive-only skips the CLI install, but this install mode installs Aspire CLI through a package or tool manager."
        say_info "Drop one of the two flags. All install modes populate the hive."
        exit 1
    fi

    if [[ "$INSTALL_MODE" != "tool" && "$INSTALL_MODE" != "winget" && "$FORCE" == true ]]; then
        say_error "--force can only be combined with --install-mode tool or --install-mode winget."
        say_info "Use --install-mode tool/winget with --force, or drop --force."
        exit 1
    fi

    if [[ "$INSTALL_MODE" == "tool" ]]; then
        if ! validate_tool_mode_runtime_identifier; then
            exit 1
        fi

        if ! check_dotnet_dependency; then
            exit 1
        fi
    fi

    if is_installer_mode && ! validate_installer_mode_environment; then
        exit 1
    fi

    # Check gh dependency (not needed for --local-dir mode)
    if [[ -z "$LOCAL_DIR" ]]; then
        check_gh_dependency
    fi

    # Set default install prefix if not provided
    if [[ -z "$INSTALL_PREFIX" ]]; then
        INSTALL_PREFIX="$HOME/.aspire"
        INSTALL_PREFIX_UNEXPANDED="\$HOME/.aspire"
    else
        INSTALL_PREFIX_UNEXPANDED="$INSTALL_PREFIX"
    fi

    # Set paths based on install prefix.
    # PR-route installs go under $INSTALL_PREFIX/dogfood/pr-<N>/bin to isolate them from
    # the script-route prefix and from other PR installs.
    if [[ -n "$PR_NUMBER" ]]; then
        cli_install_dir="$INSTALL_PREFIX/dogfood/pr-$PR_NUMBER/bin"
        INSTALL_PATH_UNEXPANDED="$INSTALL_PREFIX_UNEXPANDED/dogfood/pr-$PR_NUMBER/bin"
    else
        cli_install_dir="$INSTALL_PREFIX/bin"
        INSTALL_PATH_UNEXPANDED="$INSTALL_PREFIX_UNEXPANDED/bin"
    fi

    # Create a temporary directory for downloads
    if [[ "$DRY_RUN" == true ]]; then
        temp_dir="/tmp/aspire-cli-pr-dry-run"
    else
        temp_dir=$(mktemp -d -t aspire-cli-pr-download-XXXXXX)
        say_verbose "Creating temporary directory: $temp_dir"
    fi

    # Set trap for cleanup on exit
    cleanup() {
        remove_temp_dir "$temp_dir"
    }
    trap cleanup EXIT

    # Download and install from PR/workflow run ID, or install from local directory
    if [[ -n "$LOCAL_DIR" ]]; then
        if ! install_from_local_dir "$LOCAL_DIR"; then
            exit 1
        fi
    else
        if ! download_and_install_from_pr "$temp_dir"; then
            exit 1
        fi
    fi

    # Add to shell profile for persistent PATH. Package-manager modes and default tool installs own
    # their own PATH guidance; explicit tool-path installs use cli_install_dir.
    # PR installs deliberately skip the persistent profile write: a PR build is a per-session
    # dogfood activation. Touching ~/.zshrc / ~/.bashrc would silently demote a developer's
    # daily/stable install on every new terminal until they hunt down the stale `export PATH=`
    # line. The activation hint printed below shows how to opt in manually.
    if [[ "$HIVE_ONLY" != true ]]; then
        if script_manages_cli_path; then
            if [[ "$SKIP_PATH" == true ]]; then
                say_info "Skipping PATH configuration due to --skip-path flag"
            else
                local path_to_add="$cli_install_dir"
                local path_to_add_unexpanded="$INSTALL_PATH_UNEXPANDED"

                if path_contains "$path_to_add"; then
                    say_info "Path $path_to_add already exists in \$PATH, skipping addition"
                else
                    if [[ -n "$PR_NUMBER" ]]; then
                        say_info "PR install: leaving shell profile untouched; the activation hint below shows the PATH line to use."
                    else
                        add_to_shell_profile "$path_to_add" "$path_to_add_unexpanded"
                    fi

                    # Add to current session PATH, if the path is not already in PATH
                    if [[ "$DRY_RUN" == true ]]; then
                        say_info "[DRY RUN] Would add $path_to_add to PATH"
                    else
                        export PATH="$path_to_add:$PATH"
                    fi
                fi
            fi
        fi
    fi

    # Print PATH activation hint for PR installs.
    # Goes to stdout (not stderr) so it's visible in normal install output and tests can grep it.
    # Printed in success path (after install completes) and also under --dry-run.
    if [[ "$HIVE_ONLY" != true && -n "$PR_NUMBER" ]] && script_manages_cli_path; then
        local profile_path_unexpanded="$INSTALL_PATH_UNEXPANDED"
        echo "Add to your shell profile: export PATH=\"$profile_path_unexpanded:\$PATH\""
    fi
}

# Only run main when executed directly (not when sourced for unit tests).
# Use ${BASH_SOURCE[0]:-$0} so the guard works under `curl | bash -s` where BASH_SOURCE is unset.
if [[ "${BASH_SOURCE[0]:-$0}" == "${0}" ]]; then
    main "$@"
fi
