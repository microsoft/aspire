# CreateLayout Tool

Creates the payload embedded in the Aspire CLI: the unified `aspire-managed`
executable (Dashboard, AppHost Server, NuGet), DCP, and the native macOS or Windows tray companion.
The tool copies prepared components; it does not publish or sign them.

## Prerequisites

- Publish `Aspire.Managed` for the target RID.
- Restore the target RID's DCP NuGet package.
- For macOS, publish the tray app with `PackageTray` and complete any official signing **before** assembling the layout.
- For Windows, publish the matching x64/ARM64 native tray executable and icon, and complete any official signing **before** assembling the layout.

`eng/Bundle.proj` orchestrates these steps and the final CLI publish:

```bash
./dotnet.sh msbuild eng/Bundle.proj /p:Configuration=Release /p:TargetRid=osx-arm64
```

`SkipNativeBuild=true` skips the outer CLI publish, not the native tray build.
`SkipTrayBuild=true` reuses a prepared tray payload, particularly after official signing.
Never republish or modify a signed app before copying it into the payload.
`CliPublishDir` redirects the final native CLI publish to a separate directory
for local validation without overwriting an executable that is currently running.

## Options

| Option | Description |
|--------|-------------|
| `-o, --output <path>` | Required output directory (replaced on each build) |
| `-a, --artifacts <path>` | Required build artifacts directory |
| `--rid <rid>` | Required target runtime identifier |
| `--bundle-version <version>` | Archive version; default `0.0.0-dev` |
| `--tray-app <path>` | Prepared `.app` directory, required for macOS; ignored on Linux/Windows |
| `--tray-windows <path>` | Prepared native tray directory containing `aspire-tray.exe` and `Aspire.ico`, required for Windows |
| `--archive` | Create a tar.gz payload archive |
| `--verbose` | Enable detailed output |

## macOS Example

```bash
bash src/Aspire.Tray/Mac/publish.sh osx-arm64

./dotnet.sh run --project tools/CreateLayout/CreateLayout.csproj -- \
  --output artifacts/bundle/osx-arm64 \
  --artifacts artifacts \
  --rid osx-arm64 \
  --bundle-version 13.6.0-dev \
  --tray-app "artifacts/bin/Aspire.Tray.Mac/Release/net10.0/osx-arm64/app/Aspire Tray.app" \
  --archive
```

## Payload Structure

```text
{output}/
├── managed/
│   ├── aspire-managed[.exe]
│   └── wwwroot/
├── dcp/
│   └── ...
└── tray/                              # macOS only
    └── Aspire Tray.app/
        └── Contents/
            ├── MacOS/aspire-tray
            ├── Info.plist
            ├── Resources/Aspire.icns
            └── _CodeSignature/
```

The complete macOS app is copied, including hidden files and signatures, with
Unix file modes preserved. Missing executable/plist/icon/signature or missing execute bits
fail packaging rather than silently producing a broken macOS bundle. Layouts
installed by older CLIs remain valid without a tray. Windows layouts instead
contain `tray/aspire-tray.exe` and `tray/Aspire.ico`; Linux layouts have no tray.
The Windows companion is experimental but is included in Windows CLI bundles.

The tar.gz archive is placed next to the output directory and embedded during
the CLI's NativeAOT publish. See [the bundle specification](../../docs/specs/bundle.md)
for signing order, discovery, installation and lease behavior.

`Bundle.proj` verifies every produced macOS archive with
`verify-tray-payload.sh`: it extracts the app under
`artifacts/tray-payload-verification/{rid}/`, checks the executable/plist/icon/signature,
verifies the Mach-O architecture against the requested RID, execute bits and the
app seal, and never launches the GUI. This runs
before embedding, including the official layout-only signing pipeline path.
Windows archives are verified by `verify-windows-tray-payload.ps1`, including the PE
architecture, original icon, and required signature policy.
The shared `tests/Aspire.Tray.Tests` contract/lifecycle suite runs through the
normal test project discovery and selective matrix, including macOS. Platform
project changes also select the shared suite and packaging regression tests.
