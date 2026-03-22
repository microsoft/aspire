# Aspire CLI Download Scripts

This directory contains scripts to download and install the Aspire CLI for different platforms.

## Scripts

### CLI installers

- **`get-aspire-cli.sh`** - Bash script for Unix-like systems (Linux, macOS)
- **`get-aspire-cli.ps1`** - PowerShell script for cross-platform use (Windows, Linux, macOS)

## Current Limitations

Supported Quality values:

- `dev` - builds from the `main` branch
- `staging` - builds from the current `release` branch like `release/9.4`
- `release` - build for the latest release

## Parameters

### Bash Script (`get-aspire-cli.sh`)

| Parameter        | Short | Description                                       | Default               |
|------------------|-------|---------------------------------------------------|-----------------------|
| `--install-path` | `-i`  | Directory to install the CLI                      | `$HOME/.aspire/bin`   |
| `--version`      |       | Version of the Aspire CLI to download             | `9.0`                 |
| `--quality`      | `-q`  | Quality to download                               | `release`             |
| `--os`           |       | Operating system (auto-detected if not specified) | auto-detect           |
| `--arch`         |       | Architecture (auto-detected if not specified)     | auto-detect           |
| `--keep-archive` | `-k`  | Keep downloaded archive files after installation  | `false`               |
| `--verbose`      | `-v`  | Enable verbose output                             | `false`               |
| `--help`         | `-h`  | Show help message                                 |                       |

### PowerShell Script (`get-aspire-cli.ps1`)

| Parameter       | Description                                       | Default                                                            |
|-----------------|---------------------------------------------------|--------------------------------------------------------------------|
| `-InstallPath`  | Directory to install the CLI                      | `$HOME/.aspire/bin` (Unix) / `%USERPROFILE%\.aspire\bin` (Windows) |
| `-Version`      | Version of the Aspire CLI to download             | `9.0`                                                              |
| `-Quality`      | Quality to download                               | `release`                                                          |
| `-OS`           | Operating system (auto-detected if not specified) | auto-detect                                                        |
| `-Architecture` | Architecture (auto-detected if not specified)     | auto-detect                                                        |
| `-KeepArchive`  | Keep downloaded archive files after installation  | `false`                                                            |
| `-Help`         | Show help message                                 |                                                                    |

## Install Path Parameter

The `--install-path` (bash) or `-InstallPath` (PowerShell) parameter specifies where the Aspire CLI will be installed:

- **Default behavior**:
  - **Unix systems**: `$HOME/.aspire/bin`
  - **Windows**: `%USERPROFILE%\.aspire\bin`
- **Custom path**: You can specify any directory path where you want the CLI installed
- **Directory creation**: The scripts will automatically create the directory if it doesn't exist
- **PATH integration**: The scripts automatically update the current session's PATH and add to shell profiles for persistent access
- **Final location**: The CLI executable will be placed directly in the specified directory as:
  - `aspire` (on Linux/macOS)
  - `aspire.exe` (on Windows)

### Example Install Paths

```bash
# Default - installs to $HOME/.aspire/bin/aspire
./get-aspire-cli.sh

# Custom path - installs to /usr/local/bin/aspire
./get-aspire-cli.sh --install-path "/usr/local/bin"

# Relative path - installs to ../tools/aspire-cli/aspire
./get-aspire-cli.sh --install-path "../tools/aspire-cli"
```

## Usage Examples

### Bash Script Examples

```bash
# Basic usage - download to default location ($HOME/.aspire/bin)
./get-aspire-cli.sh

# Specify custom install directory
./get-aspire-cli.sh --install-path "/usr/local/bin"

# Download with verbose output
./get-aspire-cli.sh --verbose

# Keep the downloaded archive files for inspection
./get-aspire-cli.sh --keep-archive

# Force specific OS and architecture (useful for cross-compilation scenarios)
./get-aspire-cli.sh --os "linux" --arch "x64"

# Combine multiple options
./get-aspire-cli.sh --install-path "/tmp/aspire" --verbose --keep-archive
```

### PowerShell Script Examples

```powershell
# Basic usage - download to default location (%USERPROFILE%\.aspire\bin or $HOME/.aspire/bin)
.\get-aspire-cli.ps1

# Specify custom install directory
.\get-aspire-cli.ps1 -InstallPath "C:\Tools\Aspire"

# Download with verbose output
.\get-aspire-cli.ps1 -Verbose

# Keep the downloaded archive files for inspection
.\get-aspire-cli.ps1 -KeepArchive

# Force specific OS and architecture
.\get-aspire-cli.ps1 -OS "win" -Architecture "x64"

# Combine multiple options
.\get-aspire-cli.ps1 -InstallPath "C:\temp\aspire" -Verbose -KeepArchive
```

## Supported Runtime Identifiers

The following runtime identifier (RID) combinations are available:

| Runtime Identifier | AOTed |
|-------------------|-------------|
| `win-x64` | ✅ |
| `win-arm64` | ✅ |
| `linux-x64` | ✅ |
| `linux-arm64` | ❌ |
| `linux-musl-x64` | ❌ |
| `osx-x64` | ✅ |
| `osx-arm64` | ✅ |

The non-aot binaries are self-contained executables.

## Troubleshooting

### Common Issues

1. **"Unsupported platform" error**: Your OS/architecture combination may not be supported
2. **"Failed to download" error**: Check your internet connection and firewall settings
3. **"Checksum validation failed" error**: The download may have been corrupted; try again
4. **"HTML error page" error**: The requested version/platform combination may not be available

### Getting Help

Run the scripts with the help flag to see detailed usage information:

```bash
./get-aspire-cli.sh --help
```

```powershell
.\get-aspire-cli.ps1 -Help
```

## Contributing

When modifying these scripts, ensure:
- Both scripts maintain feature parity where possible
- Error handling is comprehensive and user-friendly
- Platform detection logic is robust
- Security best practices are followed for downloads and file handling

## PR Artifact Retrieval Scripts

Additional scripts exist to fetch CLI and NuGet artifacts from a pull request build:

- `get-aspire-cli-pr.sh`
- `get-aspire-cli-pr.ps1`

Quick fetch (Bash):
```bash
curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- <PR_NUMBER>
```

Quick fetch (PowerShell):
```powershell
iex "& { $(irm https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.ps1) } <PR_NUMBER>"
```

NuGet hive path pattern: `~/.aspire/hives/pr-<PR_NUMBER>/packages`

### Repository Override

You can point the PR artifact retrieval scripts at a fork by setting the `ASPIRE_REPO` environment variable to `owner/name` before invoking the script (defaults to `microsoft/aspire`).

Examples:

```bash
export ASPIRE_REPO=myfork/aspire
./get-aspire-cli-pr.sh 1234
```

```powershell
$env:ASPIRE_REPO = 'myfork/aspire'
./get-aspire-cli-pr.ps1 1234
```

## Self-Extracting Binary

The Aspire CLI can also be distributed as a self-extracting binary that embeds the full bundle
payload inside the native AOT executable. This is the simplest installation method:

```bash
# Linux/macOS - download and extract
mkdir -p ~/.aspire/bin
curl -fsSL <url>/aspire -o ~/.aspire/bin/aspire
chmod +x ~/.aspire/bin/aspire
~/.aspire/bin/aspire setup

# Add to PATH
export PATH="$HOME/.aspire/bin:$PATH"
```

```powershell
# Windows - download and extract
New-Item -ItemType Directory -Force -Path "$env:LOCALAPPDATA\Aspire\bin"
Invoke-WebRequest -Uri <url>/aspire.exe -OutFile "$env:LOCALAPPDATA\Aspire\bin\aspire.exe"
& "$env:LOCALAPPDATA\Aspire\bin\aspire.exe" setup
```

The `aspire setup` command extracts the embedded payload to the parent directory of the CLI binary.
Alternatively, extraction happens lazily on the first command that needs the bundle layout
(e.g., `aspire run` with a polyglot project).
