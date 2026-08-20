# CreateLayout Tool

This tool creates the Aspire bundle layout for distribution. It assembles Aspire.Managed, the Native AOT Dashboard, and DCP into a payload that the build embeds in the Native AOT Aspire CLI. The payload does not require a globally-installed .NET SDK or a separate shared runtime.

## Purpose

The bundle layout enables polyglot app hosts (TypeScript, Python, Go, etc.) to use Aspire without needing a .NET SDK installed. The bundle includes:

- **Aspire.Managed** - Self-contained executable containing the AppHost server, NuGet helper, and terminal host
- **Dashboard** - Native AOT compiled Blazor-based monitoring UI, native dependencies, and static assets
- **DCP** - Developer Control Plane (orchestrator)

## Prerequisites

Before running CreateLayout, you must:

1. Restore the repository with `./restore.sh` (Linux/macOS) or `.\restore.cmd` (Windows) to set up the local SDK.
2. Publish `Aspire.Managed` as self-contained and `Aspire.Dashboard` with Native AOT for the RID and configuration passed to CreateLayout. Building Native AOT output requires the native toolchain for the target platform.
3. Have the publish outputs available in the artifacts directory:
  - `Aspire.Managed`: `artifacts/bin/Aspire.Managed/{config}/net10.0/{rid}/publish/`
  - `Aspire.Dashboard`: `artifacts/bin/Aspire.Dashboard/{config}/net11.0/{rid}/publish/`
  - When publishing with `PlatformName={rid}`, the path includes an additional `{rid}/` immediately after the project name. The bundle build uses this layout for the Dashboard.
4. Restore the DCP NuGet package for the target RID. CreateLayout searches `NUGET_PACKAGES`, or the default NuGet package cache when that variable is not set.

After the initial restore, the build scripts (`./build.sh -bundle` / `./build.cmd -bundle`) handle publishing components and restoring DCP automatically. See the troubleshooting commands below for manual publishing.

## Usage

```bash
dotnet run --project tools/CreateLayout/CreateLayout.csproj -- [options]
```

### Required Options

| Option | Description |
|--------|-------------|
| `-o, --output <path>` | Output directory for the layout |
| `-a, --artifacts <path>` | Path to build artifacts directory |
| `--rid <rid>` | Runtime identifier of the published components, such as `linux-x64` or `win-x64` |
| `-c, --configuration <name>` | Build configuration of the published components, such as `Debug` or `Release`. Output from other configurations is not used. |

### Optional Options

| Option | Description |
|--------|-------------|
| `--bundle-version <ver>` | Version string for the layout (default: `0.0.0-dev`) |
| `--archive` | Create a `.tar.gz` archive after building, including on Windows |
| `--verbose` | Enable verbose output |

### Examples

**Build a Linux layout and archive from published components:**
```bash
dotnet run --project tools/CreateLayout/CreateLayout.csproj -- \
  --output ./artifacts/bundle/linux-x64 \
  --artifacts ./artifacts \
  --rid linux-x64 \
  --configuration Release \
  --bundle-version 13.5.0 \
  --archive \
  --verbose
```

**Build a Windows layout from published components (PowerShell):**
```powershell
dotnet run --project tools/CreateLayout/CreateLayout.csproj -- `
  --output ./artifacts/bundle/win-x64 `
  --artifacts ./artifacts `
  --configuration Release `
  --rid win-x64
```

## Output Structure

The tool creates the following layout:

```text
{output}/
├── managed/
│   └── aspire-managed[.exe] # AppHost server, NuGet helper, and terminal host
├── dashboard/
│   ├── Aspire.Dashboard[.exe]
│   ├── <SQLite native library>
│   ├── <other non-symbol publish files>
│   └── wwwroot/             # Dashboard static assets
└── dcp/                     # DCP binaries
```

## How It Works

1. **Copies aspire-managed** - Copies the self-contained AppHost server, NuGet helper, and terminal host executable
2. **Copies Dashboard** - Copies the complete Native AOT Dashboard publish payload, including native libraries and `wwwroot` static assets, excluding `.pdb`, `.dbg`, and `.dSYM` debug symbols
3. **Copies DCP** - Finds DCP binaries from NuGet package restore output
4. **Creates Archive** - Optionally creates `aspire-{version}-{rid}.tar.gz` beside the output directory on all platforms, including Windows

Dashboard packaging requires the executable, the `wwwroot` directory, and a nonempty SQLite native library for the target RID: `e_sqlite3.dll` on Windows, `libe_sqlite3.so` on Linux, or `libe_sqlite3.dylib` on macOS. CreateLayout fails before copying the Dashboard if any of these requirements is not met.

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
- Embedding the resulting `.tar.gz` payload in the Native AOT CLI

## Troubleshooting

The following publish commands use `linux-x64` and `Release`. Replace them with the RID and configuration passed to CreateLayout. When `PlatformName` is specified, it must match that RID too. Publish the Dashboard on a platform with the required Native AOT toolchain.

### "Aspire.Managed publish output not found"
Publish the self-contained `Aspire.Managed` executable first:
```bash
dotnet publish src/Aspire.Managed/Aspire.Managed.csproj -c Release -r linux-x64 --self-contained
```

### "Aspire.Dashboard publish output not found"
Publish `Aspire.Dashboard` for the target RID first. The project enables `PublishAot`, and `dotnet publish` infers self-contained output:
```bash
dotnet publish src/Aspire.Dashboard/Aspire.Dashboard.csproj -c Release -r linux-x64 -p:PlatformName=linux-x64
```

This writes the publish payload to `artifacts/bin/Aspire.Dashboard/linux-x64/Release/net11.0/linux-x64/publish/`. A non-RID publish is not accepted by CreateLayout. Keep the complete publish payload, including the SQLite native library and `wwwroot`, available for packaging.

### "DCP not found"
DCP binaries come from the NuGet package. Ensure the solution has been restored and built.
