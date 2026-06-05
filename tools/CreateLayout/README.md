# CreateLayout Tool

This tool creates the Aspire bundle layout for distribution. It assembles all components (CLI, Dashboard, DCP, runtime, and tools) into a self-contained package that can run without requiring a globally-installed .NET SDK.

## Purpose

The bundle layout enables polyglot app hosts (TypeScript, Python, Go, etc.) to use Aspire without needing a .NET SDK installed. The bundle includes:

- **Aspire CLI** - Native AOT compiled command-line interface
- **.NET Runtime** - Shared runtime for managed components
- **Dashboard** - Blazor-based monitoring UI
- **DCP** - Developer Control Plane (orchestrator)
- **AppHost Server** - Pre-built server for running app models
- **NuGet Helper** - Package search and restore operations
- **Dev-certs** - HTTPS certificate management

## Prerequisites

Before running CreateLayout, you must:

1. Build the Aspire solution with the required components published
2. Have the following publish outputs available in the artifacts directory:
   - `Aspire.Managed` → `artifacts/bin/Aspire.Managed/{config}/{tfm}/publish/`

The build scripts (`./build.sh -bundle` / `./build.cmd -bundle`) handle this automatically.

## Usage

```bash
dotnet run --project tools/CreateLayout/CreateLayout.csproj -- [options]
```

### Required Options

| Option | Description |
|--------|-------------|
| `-o, --output <path>` | Output directory for the layout |
| `-a, --artifacts <path>` | Path to build artifacts directory |

### Optional Options

| Option | Description |
|--------|-------------|
| `-r, --runtime <path>` | Path to existing .NET runtime to include |
| `--rid <rid>` | Runtime identifier (default: current platform) |
| `--bundle-version <ver>` | Version string for the layout |
| `--download-runtime` | Download .NET and ASP.NET runtimes from Microsoft |
| `--runtime-version <ver>` | Specific .NET SDK version to download |
| `--archive` | Create archive (zip/tar.gz) after building |
| `--verbose` | Enable verbose output |

### Examples

**Build layout with runtime download:**
```bash
dotnet run --project tools/CreateLayout/CreateLayout.csproj -- \
  --output ./artifacts/bundle/linux-x64 \
  --artifacts ./artifacts \
  --rid linux-x64 \
  --bundle-version 13.2.0 \
  --download-runtime \
  --archive \
  --verbose
```

**Build layout with existing runtime:**
```bash
dotnet run --project tools/CreateLayout/CreateLayout.csproj -- \
  --output ./artifacts/bundle/win-x64 \
  --artifacts ./artifacts \
  --runtime /path/to/dotnet \
  --rid win-x64
```

## Output Structure

The tool creates the following layout:

```
{output}/
├── aspire[.exe]             # Native AOT CLI executable
├── runtime/                 # .NET shared runtime
│   ├── dotnet[.exe]
│   └── shared/
│       ├── Microsoft.NETCore.App/{version}/
│       └── Microsoft.AspNetCore.App/{version}/
├── dashboard/               # Aspire Dashboard (framework-dependent)
├── dcp/                     # DCP binaries
├── aspire-server/           # Pre-built AppHost server (framework-dependent)
└── tools/
    ├── aspire-nuget/        # NuGet helper tool
    └── dev-certs/           # Certificate management
```

## How It Works

1. **Copies CLI** - Finds the native AOT compiled CLI from artifacts and copies to root
2. **Downloads/Copies Runtime** - Either downloads from Microsoft or copies from specified path
3. **Copies Dashboard** - Copies the published Dashboard output
4. **Copies DCP** - Finds DCP binaries from NuGet package restore output
5. **Copies AppHost Server** - Copies the published RemoteHost (server) output
6. **Copies NuGet Helper** - Copies the published NuGet helper tool
7. **Copies Dev-certs** - Copies the dev-certs tool from SDK
8. **Creates Archive** - Optionally creates .zip (Windows) or .tar.gz (Linux/macOS)

## Runtime Download

When `--download-runtime` is specified, the tool:

1. Downloads the .NET SDK from `builds.dotnet.microsoft.com` (using `--runtime-version` for the SDK version)
2. Extracts the .NET runtime and ASP.NET Core runtime from the SDK to the `runtime/` directory
3. Extracts the `dotnet-dev-certs` tool from the SDK to `tools/dev-certs/`

## Integration with Build Scripts

The recommended way to build the bundle is through the main build scripts:

**Linux/macOS:**
```bash
./build.sh -bundle
```

**Windows:**
```powershell
.\build.cmd -bundle
```

These scripts handle:
- Building the solution
- Publishing bundle components
- Running CreateLayout with appropriate arguments

## Troubleshooting

### "AppHost Server publish output not found"
Run `dotnet publish` on `Aspire.Hosting.RemoteHost` first:
```bash
dotnet publish src/Aspire.Hosting.RemoteHost/Aspire.Hosting.RemoteHost.csproj -c Release
```

### "Dashboard publish output not found"
Run `dotnet publish` on `Aspire.Dashboard` first:
```bash
dotnet publish src/Aspire.Dashboard/Aspire.Dashboard.csproj -c Release
```

### "DCP not found"
DCP binaries come from the NuGet package. Ensure the solution has been restored and built.
