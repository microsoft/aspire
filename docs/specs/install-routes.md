# Aspire CLI install-route sidecar

> Pairs with `docs/specs/bundle.md` (bundle extraction layout) and `docs/ci/native-cli-packaging.md` (how archives are produced).

The CLI binary identifies its install route by reading a single
`.aspire-install.json` sidecar that lives next to the binary. The sidecar's
`source` field selects the extract-dir shape used by `BundleService` and, for
portable installs, the Aspire home used for hives and local state.

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
| `localhive`    | `localhive.{sh,ps1}` (locally-built dev install)       |

`BundleService.ComputeDefaultExtractDir` maps `source` to extract-dir shape:

- `winget` / `brew` / `dotnet-tool` → `binaryDir` (flat: bundle extracts beside the binary).
- `script` / `pr` / `localhive` → `Path.GetDirectoryName(binaryDir)` (bin layout: bundle extracts as a sibling of `bin/`).
- missing, unreadable, malformed, or unknown `source` sidecar → default Aspire home (`ASPIRE_HOME` when set, otherwise `$HOME/.aspire`).

`CliPathHelper.GetAspireHomeDirectory` maps sidecar-owned portable installs to
their install prefix so hives, caches, logs, and SDK state stay with the
install. `script` and `localhive` use the parent of `bin`; `pr` uses the parent
of `dogfood/pr-<N>/bin`. Package-manager installs and sidecar-less binaries keep
the default Aspire home (`ASPIRE_HOME` when set, otherwise `$HOME/.aspire`).

## Per-route authorship

**The shared per-RID CLI archives (`aspire-cli-<rid>-*.zip` / `.tar.gz`) ship sidecar-free.** Those archives are reused across brew, winget, the release script, and the PR script — none of them owns the route label. Each route writes its own sidecar at install time.

| Route       | Archive shape                          | Sidecar writer                                                      |
|-------------|----------------------------------------|---------------------------------------------------------------------|
| brew        | shared per-RID tarball                 | cask `postflight` block in `eng/homebrew/aspire.rb.template`        |
| winget      | shared per-RID zip                     | CLI first-run probe (`WingetFirstRunProbe`) — uses the WinGet portable ARP registry entry to confirm the running binary was placed by winget, then stamps the sidecar |
| script      | shared per-RID archive                 | `eng/scripts/get-aspire-cli.{sh,ps1}` (post-extraction)             |
| PR script   | shared per-RID archive                 | `eng/scripts/get-aspire-cli-pr.{sh,ps1}` (post-extraction)          |
| dotnet-tool | route-exclusive nupkg                  | payload-embedded (staged by `Aspire.Cli.csproj` `_PreparePreBuiltCliBinaryForPackTool`) |
| localhive   | local-only (no shared archive)         | `localhive.{sh,ps1}` writes the sidecar after copying the CLI binary into `<prefix>/bin/`. When `--output PATH` is used, the sidecar is written inside the output dir, which is appropriate because localhive archives are route-exclusive (only consumed as localhive installs). |

The dotnet-tool nupkg is the one exception that payload-embeds the sidecar: the nupkg is route-exclusive (only `dotnet tool install` consumes it), so the embedded sidecar cannot leak into another route's prefix.

## Why no payload-embed in shared archives

Until PR 16817 the per-RID archives baked `{"source":"brew"}` (osx-*) and `{"source":"winget"}` (win-*) into the archive root via an MSBuild target. Because the osx-* tarball is also consumed by `get-aspire-cli-pr.sh`, the smuggled `brew` sidecar landed in the script-route prefix at `<prefix>/dogfood/pr-<N>/bin/.aspire-install.json`, and `BundleService` then selected `binaryDir` (the `brew` flat-layout case) as the extract dir — producing `<prefix>/dogfood/pr-<N>/bin/versions/<v>/` instead of `<prefix>/dogfood/pr-<N>/versions/<v>/`.

Removing the MSBuild target and moving each route to author its own sidecar at install time makes the per-RID archive route-agnostic and prevents the leak by construction.

## Producer-side invariants (build / CI)

Two mechanical checks guard the contract:

1. **MSBuild**: a `_AssertNoSidecarInArchiveStaging` target in `eng/clipack/Common.projitems` fails the build if anything stages `.aspire-install.json` into the archive output path. The build cannot ship a sidecar-bearing per-RID archive.
2. **Verify scripts**: `eng/scripts/verify-cli-archive.ps1` (`Test-ArchiveSidecar`) extracts the produced archive and asserts no `.aspire-install.json` is present. `eng/scripts/verify-cli-tool-nupkg.ps1` asserts the dotnet-tool nupkg DOES contain the sidecar at the expected path. Both run in the AzDO `build_sign_native` pipeline against the signed artifacts.

## Reader-side invariants (runtime)

`BundleService.ComputeDefaultExtractDir` is the single point of truth for layout selection. It performs no path-shape detection: layout is a pure function of the sidecar `source` value (or the fallback when the sidecar is absent, unreadable, malformed, or has an unknown `source`). Unknown `source` values fall back to the default Aspire home so unrecognized installs do not try to write next to the CLI binary. Coverage lives in `tests/Aspire.Cli.Tests/Bundles/BundleServiceCrossRouteExtractionTests.cs` as a theory over (source × prefix-shape) rows, including the cross-route case where a `brew` sidecar lands under a script-style prefix.

`CliPathHelper.GetAspireHomeDirectory` is the single point of truth for Aspire-home selection. It reads the same sidecar but only changes home for Aspire-owned portable routes (`script`, `pr`, and `localhive`); package-manager routes use the user-profile home because their install roots are package-manager-owned. Coverage lives in `tests/Aspire.Cli.Tests/Utils/CliPathHelperTests.cs`.

> **Discovery scope (dotnet-tool route).** Install discovery walks the default `dotnet tool install -g` location at `~/.dotnet/tools/.store/aspire.cli` only. Custom `--tool-path` installs are not discovered today: the dotnet CLI has no machine-wide registry of arbitrary `--tool-path` installs to enumerate, and walking the filesystem would balloon the cost of `aspire doctor`. Users with a custom-`--tool-path` install can confirm it directly with `<tool-path>/aspire doctor --self`.

For read-only install discovery (`aspire doctor --format json`), sidecar existence is the trust signal for peer probing. A candidate with any readable sidecar is probed even when `source` is not in the known route table; the raw `source` string is surfaced as the installation `route` so future package-manager routes can appear before this consumer updates. Sidecar-less, unreadable, or malformed candidates are listed without executing the binary.
