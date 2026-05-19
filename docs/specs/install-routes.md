# Aspire CLI install-route sidecar

> Pairs with `docs/specs/bundle.md` (bundle extraction layout) and `docs/ci/native-cli-packaging.md` (how archives are produced).

The CLI binary identifies its install route by reading a single
`.aspire-install.json` sidecar that lives next to the binary. The sidecar's
`source` field selects the extract-dir shape used by `BundleService`.

## File contract

`.aspire-install.json` sits beside the running `aspire` binary
(`<binaryDir>/.aspire-install.json`) and contains exactly one field:

```json
{ "source": "<route>" }
```

| `source` value | Install route                                          |
|----------------|--------------------------------------------------------|
| `brew`         | Homebrew cask                                          |
| `winget`       | WinGet portable manifest                               |
| `dotnet-tool`  | `dotnet tool install -g Aspire.Cli`                    |
| `script`       | `get-aspire-cli.{sh,ps1}`                              |
| `pr`           | `get-aspire-cli-pr.{sh,ps1}`                           |

`BundleService.ComputeDefaultExtractDir` maps `source` to extract-dir shape:

- `winget` / `brew` / `dotnet-tool` → `binaryDir` (flat: bundle extracts beside the binary).
- `script` / `pr` → `Path.GetDirectoryName(binaryDir)` (bin layout: bundle extracts as a sibling of `bin/`).
- missing or unknown sidecar → parent-of-binary, matching the legacy heuristic for pre-sidecar installs.

## Per-route authorship

**The shared per-RID CLI archives (`aspire-cli-<rid>-*.zip` / `.tar.gz`) ship sidecar-free.** Those archives are reused across brew, winget, the release script, and the PR script — none of them owns the route label. Each route writes its own sidecar at install time.

| Route       | Archive shape                          | Sidecar writer                                                      |
|-------------|----------------------------------------|---------------------------------------------------------------------|
| brew        | shared per-RID tarball                 | cask `postflight` block in `eng/homebrew/aspire.rb.template`        |
| winget      | shared per-RID zip                     | CLI first-run probe (`WingetFirstRunProbe`) — uses the WinGet portable ARP registry entry to confirm the running binary was placed by winget, then stamps the sidecar |
| script      | shared per-RID archive                 | `eng/scripts/get-aspire-cli.{sh,ps1}` (post-extraction)             |
| PR script   | shared per-RID archive                 | `eng/scripts/get-aspire-cli-pr.{sh,ps1}` (post-extraction)          |
| dotnet-tool | route-exclusive nupkg                  | payload-embedded (staged by `Aspire.Cli.csproj` `_PreparePreBuiltCliBinaryForPackTool`) |

The dotnet-tool nupkg is the one exception that payload-embeds the sidecar: the nupkg is route-exclusive (only `dotnet tool install` consumes it), so the embedded sidecar cannot leak into another route's prefix.

## Why no payload-embed in shared archives

Until PR 16817 the per-RID archives baked `{"source":"brew"}` (osx-*) and `{"source":"winget"}` (win-*) into the archive root via an MSBuild target. Because the osx-* tarball is also consumed by `get-aspire-cli-pr.sh`, the smuggled `brew` sidecar landed in the script-route prefix at `<prefix>/dogfood/pr-<N>/bin/.aspire-install.json`, and `BundleService` then selected `binaryDir` (the `brew` flat-layout case) as the extract dir — producing `<prefix>/dogfood/pr-<N>/bin/versions/<v>/` instead of `<prefix>/dogfood/pr-<N>/versions/<v>/`.

Removing the MSBuild target and moving each route to author its own sidecar at install time makes the per-RID archive route-agnostic and prevents the leak by construction.

## Producer-side invariants (build / CI)

Two mechanical checks guard the contract:

1. **MSBuild**: a `_AssertNoSidecarInArchiveStaging` target in `eng/clipack/Common.projitems` fails the build if anything stages `.aspire-install.json` into the archive output path. The build cannot ship a sidecar-bearing per-RID archive.
2. **Verify scripts**: `eng/scripts/verify-cli-archive.ps1` (`Test-ArchiveSidecar`) extracts the produced archive and asserts no `.aspire-install.json` is present. `eng/scripts/verify-cli-tool-nupkg.ps1` asserts the dotnet-tool nupkg DOES contain the sidecar at the expected path. Both run in the AzDO `build_sign_native` pipeline against the signed artifacts.

## Reader-side invariants (runtime)

`BundleService.ComputeDefaultExtractDir` is the single point of truth for layout selection. It performs no path-shape detection: layout is a pure function of the sidecar `source` value (or the fallback when the sidecar is absent or unreadable). Coverage lives in `tests/Aspire.Cli.Tests/Bundles/BundleServiceCrossRouteExtractionTests.cs` as a theory over (source × prefix-shape) rows, including the cross-route case where a `brew` sidecar lands under a script-style prefix.
