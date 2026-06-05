#!/usr/bin/env bash

# get-aspire-cli.sh - Download and unpack the Aspire CLI for the current platform
# Usage: ./get-aspire-cli.sh [OPTIONS]
#        curl -sSL <url>/get-aspire-cli.sh | bash -s -- [OPTIONS]

set -euo pipefail

# Global constants
readonly USER_AGENT="get-aspire-cli.sh/1.0"
readonly ARCHIVE_DOWNLOAD_TIMEOUT_SEC=600
readonly CHECKSUM_DOWNLOAD_TIMEOUT_SEC=120
readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly YELLOW='\033[1;33m'
readonly RESET='\033[0m'

# Variables (defaults set after parsing arguments)
INSTALL_PATH=""
VERSION=""
QUALITY=""
OS=""
ARCH=""
SHOW_HELP=false
VERBOSE=false
KEEP_ARCHIVE=false
DRY_RUN=false
INSTALL_EXTENSION=false
USE_INSIDERS=false
SKIP_PATH=false
DEFAULT_QUALITY="release"
EXTENSION_ARTIFACT_NAME="aspire-vscode.vsix.zip"

# Function to show help
show_help() {
    cat << 'EOF'
Aspire CLI Download Script

DESCRIPTION:
    Downloads and installs the Aspire CLI for the current platform from the specified version and quality.
    Automatically updates the current session's PATH environment variable and supports GitHub Actions.

    Running with `-Quality release` download the latest release version of the Aspire CLI for your platform and architecture.
    Running with `-Quality staging` will download the latest staging version, or the release version if no staging is available.
    Running with `-Quality dev` will download the latest dev build from `main`.

    The default quality is `${DEFAULT_QUALITY}`.

    Pass a specific version to get CLI for that version.

USAGE:
    ./get-aspire-cli.sh [OPTIONS]

    -i, --install-path PATH     Directory to install the CLI (default: $HOME/.aspire/bin)
    -q, --quality QUALITY       Quality to download (default: ${DEFAULT_QUALITY}). Supported values: dev, staging, release
    --version VERSION           Version of the Aspire CLI to download (default: unset)
    --os OS                     Operating system (default: auto-detect)
    --arch ARCH                 Architecture (default: auto-detect)
    --install-extension         Install VS Code extension along with the CLI
    --use-insiders              Install extension to VS Code Insiders instead of VS Code (requires --install-extension)
    --skip-path                 Do not add the install path to PATH environment variable (useful for portable installs)
    -k, --keep-archive          Keep downloaded archive files and temporary directory after installation
    --dry-run                   Show what would be done without actually performing any actions
    -v, --verbose               Enable verbose output
    -h, --help                  Show this help message

EXAMPLES:
    ./get-aspire-cli.sh
    ./get-aspire-cli.sh --install-path "~/bin"
    ./get-aspire-cli.sh --quality "staging"
    ./get-aspire-cli.sh --version "9.5.0-preview.1.25366.3"
    ./get-aspire-cli.sh --os "linux" --arch "x64"
    ./get-aspire-cli.sh --install-extension
    ./get-aspire-cli.sh --install-extension --use-insiders
    ./get-aspire-cli.sh --keep-archive
    ./get-aspire-cli.sh --dry-run
    ./get-aspire-cli.sh --help

    # Piped execution (like wget <url> | bash or curl <url> | bash):
    curl -sSL https://aka.ms/aspire/get/install.sh | bash
    curl -sSL https://aka.ms/aspire/get/install.sh | bash -s -- --install-path "~/bin"

EOF
}

# Function to parse command line arguments
parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            -i|--install-path)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                INSTALL_PATH="$2"
                shift 2
                ;;
            --version)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                VERSION="$2"
                shift 2
                ;;
            -q|--quality)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                QUALITY="$2"
                shift 2
                ;;
            --os)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                OS="$2"
                shift 2
                ;;
            --arch)
                if [[ $# -lt 2 || -z "$2" ]]; then
                    say_error "Option '$1' requires a non-empty value"
                    say_info "Use --help for usage information."
                    exit 1
                fi
                ARCH="$2"
                shift 2
                ;;
            --install-extension)
                INSTALL_EXTENSION=true
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
            -k|--keep-archive)
                KEEP_ARCHIVE=true
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
            -h|--help)
                SHOW_HELP=true
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

# Function for verbose logging
say_verbose() {
    if [[ "$VERBOSE" == true ]]; then
        echo -e "${YELLOW}$1${RESET}" >&2
    fi
}

say_error() {
    echo -e "${RED}Error: $1${RESET}\n" >&2
}

say_warn() {
    echo -e "${YELLOW}Warning: $1${RESET}\n" >&2
}

say_info() {
    echo -e "$1" >&2
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

# Common function for HTTP requests with centralized configuration
secure_curl() {
    local url="$1"
    local output_file="$2"
    local timeout="${3:-300}"
    local user_agent="${4:-$USER_AGENT}"
    local max_retries="${5:-5}"
    local method="${6:-GET}"

    local curl_args=(
        --fail
        --show-error
        --location
        --tlsv1.2
        --tls-max 1.3
        --max-time "$timeout"
        --user-agent "$user_agent"
        --max-redirs 10
        --retry "$max_retries"
        --retry-delay 1
        --retry-max-time 60
        --request "$method"
    )

    # Add extra args based on method
    if [[ "$method" == "HEAD" ]]; then
        curl_args+=(--silent --head)
    else
        curl_args+=(--progress-bar)
    fi

    # Add output file only for GET requests
    if [[ "$method" == "GET" ]]; then
        curl_args+=(--output "$output_file")
    fi

    say_verbose "curl ${curl_args[*]} $url"
    curl "${curl_args[@]}" "$url"
}

# Validate content type via HEAD request
validate_content_type() {
    local url="$1"

    say_verbose "Validating content type for $url"

    # Get headers via HEAD request
    local headers
    if headers=$(secure_curl "$url" /dev/null 60 "$USER_AGENT" 3 "HEAD" 2>&1); then
        # Check if response suggests HTML content (error page)
        if echo "$headers" | grep -qi "content-type:.*text/html"; then
            say_error "Server returned HTML content instead of expected file. Make sure the URL is correct: $url"
            return 1
        fi
    else
        # If HEAD request fails, continue anyway as some servers don't support it
        say_verbose "HEAD request failed, proceeding with download."
    fi

    return 0
}

# General-purpose file download wrapper
download_file() {
    local url="$1"
    local output_path="$2"
    local timeout="${3:-300}"
    local max_retries="${4:-5}"
    local validate_content_type="${5:-true}"
    local use_temp_file="${6:-true}"

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would download $url"
        return 0
    fi

    local target_file="$output_path"
    if [[ "$use_temp_file" == true ]]; then
        target_file="${output_path}.tmp"
    fi

    # Validate content type via HEAD request if requested
    if [[ "$validate_content_type" == true ]]; then
        if ! validate_content_type "$url"; then
            return 1
        fi
    fi

    say_verbose "Downloading $url to $target_file"
    say_info "Downloading from: $url"

    # Download the file
    if secure_curl "$url" "$target_file" "$timeout" "$USER_AGENT" "$max_retries"; then
        # Move temp file to final location if using temp file
        if [[ "$use_temp_file" == true ]]; then
            mv "$target_file" "$output_path"
        fi

        say_verbose "Successfully downloaded file to: $output_path"
        return 0
    else
        say_error "Failed to download $url"
        return 1
    fi
}

# Validate the checksum of the downloaded file
validate_checksum() {
    local archive_file="$1"
    local checksum_file="$2"

    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would validate checksum of $archive_file using $checksum_file"
        return 0
    fi

    # Determine the checksum command to use
    local checksum_cmd=""
    if command -v sha512sum >/dev/null 2>&1; then
        checksum_cmd="sha512sum"
    elif command -v shasum >/dev/null 2>&1; then
        checksum_cmd="shasum -a 512"
    else
        say_error "Neither sha512sum nor shasum is available. Please install one of them to validate checksums."
        return 1
    fi

    # Read the expected checksum from the file
    local expected_checksum
    expected_checksum=$(tr -d '\n\r' < "$checksum_file" | tr '[:upper:]' '[:lower:]')

    # Calculate the actual checksum
    local actual_checksum
    actual_checksum=$(${checksum_cmd} "$archive_file" | cut -d' ' -f1)

    # Compare checksums
    if [[ "$expected_checksum" == "$actual_checksum" ]]; then
        return 0
    else
        # Limit expected checksum display to 128 characters for output
        local expected_checksum_display
        if [[ ${#expected_checksum} -gt 128 ]]; then
            expected_checksum_display="${expected_checksum:0:128}"
        else
            expected_checksum_display="$expected_checksum"
        fi

        say_error "Checksum validation failed for $archive_file with checksum from $checksum_file !"
        say_info "Expected: $expected_checksum_display"
        say_info "Actual:   $actual_checksum"
        return 1
    fi
}

# Function to install/unpack archive files
install_archive() {
    local archive_file="$1"
    local destination_path="$2"
    local os="$3"

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

    if [[ "$os" == "win" ]]; then
        # Use unzip for ZIP files
        if ! command -v unzip >/dev/null 2>&1; then
            say_error "unzip command not found. Please install unzip to extract ZIP files."
            return 1
        fi

        if ! unzip -o "$archive_file" -d "$destination_path"; then
            say_error "Failed to extract ZIP archive: $archive_file"
            return 1
        fi
    else
        # Use tar for tar.gz files on Unix systems
        if ! command -v tar >/dev/null 2>&1; then
            say_error "tar command not found. Please install tar to extract tar.gz files."
            return 1
        fi

        if ! tar -xzf "$archive_file" -C "$destination_path"; then
            say_error "Failed to extract tar.gz archive: $archive_file"
            return 1
        fi
    fi

    say_verbose "Successfully installed archive"
}

# Function to map quality to channel name
# Parameters:
#   $1 - quality: The quality string (release, staging, dev)
# Returns: The corresponding channel name (stable, staging, daily)
map_quality_to_channel() {
    local quality="$1"
    
    case "$quality" in
        release)
            printf "stable"
            ;;
        staging)
            printf "staging"
            ;;
        dev)
            printf "daily"
            ;;
        *)
            # Unknown quality, return as-is
            printf "%s" "$quality"
            ;;
    esac
}

# Function to save the global settings using the aspire CLI
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
    
    local output
    output=$("$cli_path" config set -g "$key" "$value" 2>&1)
    local exit_code=$?
    if [[ $exit_code -ne 0 ]]; then
        say_warn "Failed to set global config via aspire CLI: $output"
        return 1
    fi
    if [[ -n "$output" ]]; then
        say_verbose "$output"
    fi
    
    say_verbose "Global config saved: $key = $value"
}

# Function to remove a global setting using the aspire CLI
# Uses 'aspire config delete -g' to remove global configuration values
# This is used when installing the release/stable channel to avoid forcing nuget.config creation
# Parameters:
#   $1 - cli_path: Path to the aspire CLI executable
#   $2 - key: The configuration key to remove
remove_global_settings() {
    local cli_path="$1"
    local key="$2"
    
    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would run: $cli_path config delete -g $key"
        return 0
    fi
    
    say_verbose "Removing global config: $key"
    
    local output
    output=$("$cli_path" config delete -g "$key" 2>&1)
    local exit_code=$?
    if [[ $exit_code -ne 0 ]]; then
        say_verbose "Failed to delete global config via aspire CLI: $output"
        return 1
    fi
    
    say_verbose "Global config removed: $key"
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
        echo -e "\n# Added by get-aspire-cli.sh" >> "$config_file"
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

    # Check for existing config files
    for file in $config_files; do
        if [[ -f "$file" ]]; then
            config_file="$file"
            break
        fi
    done

    # If no config file exists, create the default one for the detected shell
    if [[ -z "${config_file:-}" ]]; then
        say_verbose "No config file found for $shell_name. Will create default config file. Checked: $config_files"

        # Determine the default config file to create based on shell
        case "$shell_name" in
            bash)
                config_file="$HOME/.bashrc"
                ;;
            zsh)
                config_file="$HOME/.zshrc"
                ;;
            fish)
                config_file="$HOME/.config/fish/config.fish"
                ;;
            sh)
                config_file="$HOME/.profile"
                ;;
            *)
                # For unknown shells, default to bash config
                config_file="$HOME/.bashrc"
                ;;
        esac

        say_verbose "Attempting to create config file: $config_file"

        if [[ "$DRY_RUN" == true ]]; then
            say_info "[DRY RUN] Would create config file: $config_file"
            return 0
        else
            # Create parent directory if needed (for fish config)
            config_dir=$(dirname "$config_file")
            if [[ ! -d "$config_dir" ]]; then
                if mkdir -p "$config_dir" 2>/dev/null; then
                    say_verbose "Created directory: $config_dir"
                else
                    say_error "Failed to create directory: $config_dir"
                    show_manual_path_instructions "$shell_name" "$bin_path_unexpanded" ""
                    return 1
                fi
            fi

            # Create the config file
            if touch "$config_file" 2>/dev/null; then
                say_info "Created new shell config file: $config_file"
            else
                say_error "Failed to create config file: $config_file"
                show_manual_path_instructions "$shell_name" "$bin_path_unexpanded" "$config_file"
                return 1
            fi
        fi
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

    printf "\nTo use the Aspire CLI in new terminal sessions, restart your terminal or run:\n"
    say_info "  source $config_file"

    return 0
}

# Helper function to show manual PATH configuration instructions
show_manual_path_instructions() {
    local shell_name="$1"
    local bin_path_unexpanded="$2"
    local config_file="$3"

    say_info "Please manually create the file $config_file and add the following line:"
    case "$shell_name" in
        fish)
            say_info "  fish_add_path $bin_path_unexpanded"
            ;;
        *)
            say_info "  export PATH=\"$bin_path_unexpanded:\$PATH\""
            ;;
    esac
}

# Function to check VS Code CLI dependency
test_vscode_cli() {
    local vscode_cmd
    local vscode_name

    if [[ "$USE_INSIDERS" == true ]]; then
        vscode_cmd="code-insiders"
        vscode_name="VS Code Insiders"
    else
        vscode_cmd="code"
        vscode_name="VS Code"
    fi

    if ! command -v "$vscode_cmd" >/dev/null 2>&1; then
        say_warn "$vscode_name CLI ($vscode_cmd) not found in PATH"
        return 1
    fi

    say_verbose "$vscode_name CLI ($vscode_cmd) found"
    return 0
}

# Function to construct the URL for VS Code extension download
construct_aspire_extension_url() {
    local version="$1"
    local quality="$2"
    local base_url
    local extension="vsix.zip"

    if [[ -z "$version" ]]; then
        # When version is not set use aka.ms URLs based on quality
        case "$quality" in
            dev)
                base_url="https://aka.ms/dotnet/9/aspire/daily"
                ;;
            staging)
                base_url="https://aka.ms/dotnet/9/aspire/rc/daily"
                ;;
            release)
                base_url="https://aka.ms/dotnet/9/aspire/ga/daily"
                ;;
            *)
                say_error "Unsupported quality '$quality'. Supported values are: dev, staging, release."
                return 1
                ;;
        esac

        printf "${base_url}/aspire-vscode.${extension}"
    else
        # When version is set, use ci.dot.net URL
        base_url="https://ci.dot.net/public/aspire/"
        printf "${base_url}${version}/aspire-vscode-${version}.${extension}"
    fi
}

# Function to construct the base URL for the Aspire CLI download
construct_aspire_cli_url() {
    local version="$1"
    local quality="$2"
    local rid="$3"
    local extension="$4"
    local checksum="${5:-false}"
    local base_url
    local filename

    # Add .sha512 to extension if checksum is true
    if [[ "$checksum" == "true" ]]; then
        extension="${extension}.sha512"
    fi

    if [[ -z "$version" ]]; then
        # When version is not set use aka.ms URLs based on quality
        case "$quality" in
            dev)
                base_url="https://aka.ms/dotnet/9/aspire/daily"
                ;;
            staging)
                base_url="https://aka.ms/dotnet/9/aspire/rc/daily"
                ;;
            release)
                base_url="https://aka.ms/dotnet/9/aspire/ga/daily"
                ;;
            *)
                say_error "Unsupported quality '$quality'. Supported values are: dev, staging, release."
                return 1
                ;;
        esac

        printf "${base_url}/aspire-cli-${rid}.${extension}"
    else
        # When version is set, use ci.dot.net URL

        if [[ "$checksum" == "true" ]]; then
            # For checksum URLs, use the public-checksums URL
            base_url="https://ci.dot.net/public-checksums/aspire"
        else
            base_url="https://ci.dot.net/public/aspire/"
        fi

        printf "${base_url}/${version}/aspire-cli-${rid}-${version}.${extension}"
    fi
}

# Function to download VS Code extension
download_aspire_extension() {
    local temp_dir="$1"
    local version="$2"
    local quality="$3"
    local url extension_archive

    say_info "Downloading Aspire VS Code extension"

    if ! url=$(construct_aspire_extension_url "$version" "$quality"); then
        return 1
    fi

    extension_archive="${temp_dir}/${EXTENSION_ARTIFACT_NAME}"

    say_info "Downloading from: $url"
    if ! download_file "$url" "$extension_archive" $ARCHIVE_DOWNLOAD_TIMEOUT_SEC; then
        return 1
    fi

    say_verbose "Successfully downloaded extension archive"
    echo "$extension_archive"
}

# Function to install VS Code extension
install_aspire_extension() {
    local extension_archive="$1"
    local vscode_cmd vscode_name extract_dir vsix_file

    if [[ "$USE_INSIDERS" == true ]]; then
        vscode_cmd="code-insiders"
        vscode_name="VS Code Insiders"
    else
        vscode_cmd="code"
        vscode_name="VS Code"
    fi

    say_info "Installing Aspire extension to $vscode_name"

    # Extract the zip to get the VSIX file
    extract_dir=$(mktemp -d -t aspire-extension-XXXXXXXX)

    # Extract archive
    if [[ "$DRY_RUN" != true ]]; then
        if command -v unzip >/dev/null 2>&1; then
            if ! unzip -q "$extension_archive" -d "$extract_dir"; then
                say_error "Failed to extract extension archive"
                rm -rf "$extract_dir"
                return 1
            fi
        else
            say_error "unzip command not found"
            rm -rf "$extract_dir"
            return 1
        fi

        say_verbose "Extracted extension archive"
    fi

    # Find the VSIX file
    vsix_file=$(find "$extract_dir" -name "*.vsix" | head -n 1)

    if [[ -z "$vsix_file" ]]; then
        say_error "No VSIX file found in extension archive"
        rm -rf "$extract_dir"
        return 1
    fi

    say_verbose "Found VSIX file: $(basename "$vsix_file")"

    # Install the extension
    if [[ "$DRY_RUN" == true ]]; then
        say_info "[DRY RUN] Would run: $vscode_cmd --install-extension $vsix_file --force"
    else
        say_verbose "Running: $vscode_cmd --install-extension $vsix_file --force"
        if ! "$vscode_cmd" --install-extension "$vsix_file" --force; then
            say_error "Extension installation failed"
            rm -rf "$extract_dir"
            return 1
        fi

        say_info "${GREEN}Successfully installed Aspire extension to $vscode_name${RESET}"
    fi

    # Clean up extraction directory
    rm -rf "$extract_dir"
    return 0
}

# Function to download and install archive
download_and_install_archive() {
    local temp_dir="$1"
    local os arch runtimeIdentifier url filename checksum_url checksum_filename extension
    local cli_exe cli_path

    # Detect OS and architecture if not provided
    if [[ -z "$OS" ]]; then
        if ! os=$(detect_os); then
            say_error "Unsupported operating system. Current platform: $(uname -s)"
            return 1
        fi
    else
        os="$OS"
    fi

    if [[ -z "$ARCH" ]]; then
        if ! arch=$(get_cli_architecture_from_architecture "<auto>"); then
            return 1
        fi
    else
        if ! arch=$(get_cli_architecture_from_architecture "$ARCH"); then
            return 1
        fi
    fi

    # Construct the runtime identifier
    runtimeIdentifier="${os}-${arch}"

    # Determine file extension based on OS
    if [[ "$os" == "win" ]]; then
        extension="zip"
    else
        extension="tar.gz"
    fi

    # Construct the URLs using the new function
    if ! url=$(construct_aspire_cli_url "$VERSION" "$QUALITY" "$runtimeIdentifier" "$extension"); then
        return 1
    fi
    if ! checksum_url=$(construct_aspire_cli_url "$VERSION" "$QUALITY" "$runtimeIdentifier" "$extension" "true"); then
        return 1
    fi

    filename="${temp_dir}/aspire-cli-${runtimeIdentifier}.${extension}"
    checksum_filename="${temp_dir}/aspire-cli-${runtimeIdentifier}.${extension}.sha512"

    # Download the Aspire CLI archive
    if ! download_file "$url" "$filename" $ARCHIVE_DOWNLOAD_TIMEOUT_SEC; then
        return 1
    fi

    # Download and test the checksum
    if ! download_file "$checksum_url" "$checksum_filename" $CHECKSUM_DOWNLOAD_TIMEOUT_SEC; then
        return 1
    fi

    if ! validate_checksum "$filename" "$checksum_filename"; then
        return 1
    fi

    if [[ "$DRY_RUN" != true ]]; then
        say_verbose "Successfully downloaded and validated: $filename"
    fi

    # Install the archive
    if ! install_archive "$filename" "$INSTALL_PATH" "$os"; then
        return 1
    fi

    if [[ "$os" == "win" ]]; then
        cli_exe="aspire.exe"
    else
        cli_exe="aspire"
    fi
    cli_path="${INSTALL_PATH}/${cli_exe}"

    say_info "Aspire CLI successfully installed to: ${GREEN}$cli_path${RESET}"

    # Save the global channel setting if using quality-based download (not version-specific)
    # This allows 'aspire new' and 'aspire init' to use the same channel by default
    # For release/stable channel, remove the setting to avoid forcing nuget.config creation
    if [[ -z "$VERSION" ]]; then
        local channel
        channel=$(map_quality_to_channel "$QUALITY")
        if [[ "$channel" == "stable" ]]; then
            remove_global_settings "$cli_path" "channel"
        else
            save_global_settings "$cli_path" "channel" "$channel"
        fi
    fi

    # Download and install VS Code extension if requested
    if [[ "$INSTALL_EXTENSION" == true ]]; then
        printf "\n"
        say_info "Installing VS Code extension"

        if test_vscode_cli; then
            if extension_archive=$(download_aspire_extension "$temp_dir" "$VERSION" "$QUALITY"); then
                if ! install_aspire_extension "$extension_archive"; then
                    say_warn "Failed to install VS Code extension"
                    say_warn "The CLI was installed successfully, but the extension installation failed"
                fi
            else
                say_warn "Failed to download VS Code extension"
                say_warn "The CLI was installed successfully, but the extension download failed"
            fi
        else
            say_warn "Cannot install extension: VS Code CLI not found in PATH"
            say_info "Please ensure VS Code is installed and available in PATH"
        fi
    fi
}

# Parse command line arguments
parse_args "$@"

if [[ "$SHOW_HELP" == true ]]; then
    show_help
    exit 0
fi

# Validate that both --version and --quality are not provided together
if [[ -n "$VERSION" && -n "$QUALITY" ]]; then
    say_error "Cannot specify both --version and --quality. Use --version for a specific version or --quality for a quality level."
    say_info "Use --help for usage information."
    exit 1
fi

# Initialize default values after parsing arguments
if [[ -z "$QUALITY" ]]; then
    # Default quality if not provided
    QUALITY="${DEFAULT_QUALITY}"
fi

# Validate extension installation is only allowed with dev quality
if [[ "$INSTALL_EXTENSION" == true && "$QUALITY" != "dev" ]]; then
    say_error "Extension installation is only supported with --quality dev. Current quality: $QUALITY"
    say_info "Use --help for usage information."
    exit 1
fi

# Set default install path if not provided
if [[ -z "$INSTALL_PATH" ]]; then
    INSTALL_PATH="$HOME/.aspire/bin"
    INSTALL_PATH_UNEXPANDED="\$HOME/.aspire/bin"
else
    INSTALL_PATH_UNEXPANDED="$INSTALL_PATH"
fi

# Create a temporary directory for downloads
if [[ "$DRY_RUN" == true ]]; then
    temp_dir="/tmp/aspire-cli-dry-run"
else
    temp_dir=$(mktemp -d -t aspire-cli-download-XXXXXXXX)
    say_verbose "Creating temporary directory: $temp_dir"
fi

# Cleanup function for temporary directory
cleanup() {
    # shellcheck disable=SC2317  # Function is called via trap
    if [[ "$DRY_RUN" == true ]]; then
        # No cleanup needed in dry-run mode
        return 0
    fi

    if [[ -n "${temp_dir:-}" ]] && [[ -d "$temp_dir" ]]; then
        if [[ "$KEEP_ARCHIVE" != true ]]; then
            say_verbose "Cleaning up temporary files..."
            rm -rf "$temp_dir" || say_warn "Failed to clean up temporary directory: $temp_dir"
        else
            printf "Archive files kept in: %s\n" "$temp_dir"
        fi
    fi
}

# Set trap for cleanup on exit
trap cleanup EXIT

# Download and install the archive
if ! download_and_install_archive "$temp_dir"; then
    exit 1
fi

# Skip PATH configuration if --skip-path is set
if [[ "$SKIP_PATH" != true ]]; then
    # Handle GitHub Actions environment
    if [[ -n "${GITHUB_ACTIONS:-}" ]] && [[ "${GITHUB_ACTIONS}" == "true" ]]; then
        if [[ -n "${GITHUB_PATH:-}" ]]; then
            if [[ "$DRY_RUN" == true ]]; then
                say_info "[DRY RUN] Would add $INSTALL_PATH to \$GITHUB_PATH"
            else
                echo "$INSTALL_PATH" >> "$GITHUB_PATH"
                say_verbose "Added $INSTALL_PATH to \$GITHUB_PATH"
            fi
        fi
    fi

    # Add to shell profile for persistent PATH
    add_to_shell_profile "$INSTALL_PATH" "$INSTALL_PATH_UNEXPANDED"

    # Add to current session PATH, if the path is not already in PATH
    if [[ ":$PATH:" != *":$INSTALL_PATH:"* ]]; then
        if [[ "$DRY_RUN" == true ]]; then
            say_info "[DRY RUN] Would add $INSTALL_PATH to PATH"
        else
            export PATH="$INSTALL_PATH:$PATH"
        fi
    fi
else
    say_info "Skipping PATH configuration due to --skip-path flag"
fi
