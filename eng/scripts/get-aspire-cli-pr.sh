#!/usr/bin/env bash

# get-aspire-cli-pr.sh - Download and unpack the Aspire CLI from a specific PR's build artifacts
# Usage: ./get-aspire-cli-pr.sh PR_NUMBER [OPTIONS]

set -euo pipefail

# Global constants / defaults
readonly BUILT_NUGETS_ARTIFACT_NAME="built-nugets"
readonly BUILT_NUGETS_RID_ARTIFACT_NAME="built-nugets-for"
readonly CLI_ARCHIVE_ARTIFACT_NAME_PREFIX="cli-native-archives"
readonly ASPIRE_CLI_ARTIFACT_NAME_PREFIX="aspire-cli"
readonly EXTENSION_ARTIFACT_NAME="aspire-extension"

# Repository: Allow override via ASPIRE_REPO env var (owner/name). Default: dotnet/aspire
readonly REPO="${ASPIRE_REPO:-dotnet/aspire}"
readonly GH_REPOS_BASE="repos/${REPO}"

# Global constants
readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly YELLOW='\033[1;33m'
readonly RESET='\033[0m'

# Variables (defaults set after parsing arguments)
INSTALL_PREFIX=""
PR_NUMBER=""
WORKFLOW_RUN_ID=""
OS_ARG=""
ARCH_ARG=""
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

    PR_NUMBER                   Pull request number (required)
    --run-id, -r WORKFLOW_ID    Workflow run ID to download from (optional)
    -i, --install-path PATH     Directory prefix to install (default: ~/.aspire)
                                CLI installs to: <install-path>/bin
                                NuGet hive:      <install-path>/hives/pr-<PR_NUMBER>/packages
    --os OS                     Override OS detection (win, linux, linux-musl, osx)
    --arch ARCH                 Override architecture detection (x64, arm64)
    --hive-only                 Only install NuGet packages to the hive, skip CLI download
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
    ./get-aspire-cli-pr.sh 1234 --install-path ~/my-aspire
    ./get-aspire-cli-pr.sh 1234 --os linux --arch arm64 --verbose
    ./get-aspire-cli-pr.sh 1234 --hive-only
    ./get-aspire-cli-pr.sh 1234 --skip-extension
    ./get-aspire-cli-pr.sh 1234 --use-insiders
    ./get-aspire-cli-pr.sh 1234 --skip-path
    ./get-aspire-cli-pr.sh 1234 --dry-run

    curl -fsSL https://raw.githubusercontent.com/dotnet/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- <PR_NUMBER>

REQUIREMENTS:
    - GitHub CLI (gh) must be installed and authenticated
    - Permissions to download artifacts from the target repository
    - VS Code extension installation requires VS Code CLI (code) to be available in PATH

ENVIRONMENT VARIABLES:
    ASPIRE_REPO            Override repository (owner/name). Default: dotnet/aspire
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
        say_error "At least one argument is required. The first argument must be a PR number."
        say_info "Use --help for usage information."
        exit 1
    fi

    # First argument must be the PR number (cannot start with --)
    if [[ "$1" == --* ]]; then
        say_error "First argument must be a PR number, not an option. Got: '$1'"
        say_info "Use --help for usage information."
        exit 1
    fi

    # Validate that the first argument is a valid PR number (positive integer)
    if [[ "$1" =~ ^[1-9][0-9]*$ ]]; then
        PR_NUMBER="$1"
        shift
    else
        say_error "First argument must be a valid PR number"
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
            -i|--install-path)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                INSTALL_PREFIX="$2"
                shift 2
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
            say_error "Architecture $architecture not supported. If you think this is a bug, report it at https://github.com/dotnet/aspire/issues"
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
            say_error "Architecture $uname_m not supported. If you think this is a bug, report it at https://github.com/dotnet/aspire/issues"
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

# Function to save global settings using the aspire CLI
# Uses 'aspire config set -g' to set global configuration values
# Parameters:
#   $1 - cli_path: Path to the aspire CLI executable
#   $2 - key: The configuration key to set
#   $3 - value: The value to set
# Expected schema of ~/.aspire/globalsettings.json:
# {
#   "channel": "string"  // The channel name (e.g., "daily", "staging", "pr-1234")
# }
save_global_settings() {
    local cli_path="$1"
    local key="$2"
    local value="$3"
    
    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would run: $cli_path config set -g $key $value"
        return 0
    fi
    
    say_verbose "Setting global config: $key = $value"
    
    if ! "$cli_path" config set -g "$key" "$value" 2>/dev/null; then
        say_warn "Failed to set global config via aspire CLI"
        return 1
    fi
    
    say_verbose "Global config saved: $key = $value"
}

# Function to add PATH to shell configuration file
# Parameters:
#   $1 - config_file: Path to the shell configuration file
#   $2 - bin_path: The binary path to add to PATH
#   $3 - command: The command to add to the configuration file
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

    if [[ ":$PATH:" == *":$bin_path:"* ]]; then
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
    local config_file

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
#   $1 - endpoint: The GitHub API endpoint (e.g., "repos/dotnet/aspire/pulls/123")
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

    local head_sha
    if ! head_sha=$(gh_api_call "${GH_REPOS_BASE}/pulls/$pr_number" ".head.sha" "Failed to get HEAD SHA for PR #$pr_number"); then
        say_info "This could mean:"
        say_info "  - The PR number does not exist"
        say_info "  - You don't have access to the repository"
        exit 1
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
        # Return a mock version for dry run
        printf "pr.1234.a1b2c3d4"
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

# Function to install downloaded CLI
install_aspire_cli() {
    local cli_archive_path="$1"
    local cli_install_dir="$2"

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would install CLI archive to: $cli_install_dir"
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

# Main function to download and install from PR or workflow run ID
download_and_install_from_pr() {
    # Parameters:
    #   $1 - temp_dir (required)
    local temp_dir="$1"
    local head_sha workflow_run_id rid

    # If a workflow run ID was explicitly provided via arguments, use that directly.
    # (Previously this checked the uninitialized local variable 'workflow_run_id', which was always empty.)
    if [[ -n "$WORKFLOW_RUN_ID" ]]; then
        say_info "Starting download and installation for PR #$PR_NUMBER with workflow run ID: $WORKFLOW_RUN_ID"
        workflow_run_id="$WORKFLOW_RUN_ID"
    else
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

    # Set installation paths
    local cli_install_dir="$INSTALL_PREFIX/bin"
    local nuget_hive_dir="$INSTALL_PREFIX/hives/pr-$PR_NUMBER/packages"

    # First, download both artifacts
    local cli_archive_path nuget_download_dir
    # Compute RID once
    if ! rid=$(get_runtime_identifier "$OS_ARG" "$ARCH_ARG"); then
        return 1
    fi
    say_verbose "Computed RID: $rid"
    if [[ "$HIVE_ONLY" == true ]]; then
        say_info "Skipping CLI download due to --hive-only flag"
    else
    if ! cli_archive_path=$(download_aspire_cli "$workflow_run_id" "$rid" "$temp_dir"); then
            return 1
        fi
    fi

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
    else
        if ! install_aspire_cli "$cli_archive_path" "$cli_install_dir"; then
            return 1
        fi
    fi

    if ! install_built_nugets "$nuget_download_dir" "$nuget_hive_dir"; then
        say_error "Failed to install nuget packages"
        return 1
    fi

    # Install VS Code extension if downloaded
    if [[ -n "$extension_download_dir" && "$SKIP_EXTENSION_INSTALL" != true ]]; then
        if check_vscode_cli_dependency; then
            install_aspire_extension "$extension_download_dir"
        fi
    fi

    # Save the global channel setting to the PR hive channel
    # This allows 'aspire new' and 'aspire init' to use the same channel by default
    if [[ "$HIVE_ONLY" != true ]]; then
        # Determine CLI path
        local cli_path
        if [[ -f "$cli_install_dir/aspire.exe" ]]; then
            cli_path="$cli_install_dir/aspire.exe"
        else
            cli_path="$cli_install_dir/aspire"
        fi
        save_global_settings "$cli_path" "channel" "pr-$PR_NUMBER"
    fi
}

# =============================================================================
# Main Execution
# =============================================================================

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

# Check gh dependency
check_gh_dependency

# Set default install prefix if not provided
if [[ -z "$INSTALL_PREFIX" ]]; then
    INSTALL_PREFIX="$HOME/.aspire"
    INSTALL_PREFIX_UNEXPANDED="\$HOME/.aspire"
else
    INSTALL_PREFIX_UNEXPANDED="$INSTALL_PREFIX"
fi

# Set paths based on install prefix
cli_install_dir="$INSTALL_PREFIX/bin"
INSTALL_PATH_UNEXPANDED="$INSTALL_PREFIX_UNEXPANDED/bin"

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

# Download and install from PR or workflow run ID
if ! download_and_install_from_pr "$temp_dir"; then
    exit 1
fi

# Add to shell profile for persistent PATH
if [[ "$HIVE_ONLY" != true ]]; then
    if [[ "$SKIP_PATH" == true ]]; then
        say_info "Skipping PATH configuration due to --skip-path flag"
    else
        add_to_shell_profile "$cli_install_dir" "$INSTALL_PATH_UNEXPANDED"

        # Add to current session PATH, if the path is not already in PATH
        if  [[ ":$PATH:" != *":$cli_install_dir:"* ]]; then
            if [[ "$DRY_RUN" == true ]]; then
                say_info "[DRY RUN] Would add $cli_install_dir to PATH"
            else
                export PATH="$cli_install_dir:$PATH"
            fi
        fi
    fi
fi
