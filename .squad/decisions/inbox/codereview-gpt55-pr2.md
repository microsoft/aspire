# Code Review — PR #16817 (PR2 route primitives) — gpt-5.5

## Summary

I found three substantive issues. The biggest gap is that the PR documents Homebrew/WinGet sidecars but does not actually add route-specific sidecars to the native archives those package managers install. The resolver and PR PowerShell sidecar also diverge from the v3 contract in ways that will break PR3 consumers or Windows update guidance.

## Findings

### Blocking (must-fix before merge)

1. **WinGet and Homebrew packages do not actually ship `.aspire-install.json`.**

   The PR2 contract requires sidecars across all five routes, with WinGet and Homebrew using Mode B (`<prefix>/{aspire(.exe), .aspire-install.json}`). The changed WinGet and Homebrew files only add comments saying the zip/tarball ships the sidecar (`eng/winget/microsoft.aspire/Aspire.installer.yaml.template`, `eng/homebrew/aspire.rb.template`). I could not find any corresponding packaging change that writes route-specific sidecars into the native archive roots, and `eng/clipack/Common.projitems` still stages/packs the native archive around the published binary only. `verify-cli-archive.ps1` also still validates only that the binary runs, not that the archive contains the route sidecar.

   Impact: WinGet and Homebrew installs will have no sidecar at the package prefix, so `InstallPathResolver` will return `Unknown`; PR3 route detection / `aspire update --self` delegation will fail for two of the five required routes. Fix by adding packaging-time sidecars for the WinGet zip (`{ "route": "winget", "updateCommand": "winget upgrade Microsoft.Aspire" }`) and Homebrew tarballs (`{ "route": "brew", "updateCommand": "brew upgrade aspire" }`) and by extending archive verification/tests to assert them. Be careful not to put these sidecars into the generic script-route extraction path in a way that would make Mode B win for script installs.

### Major (should-fix before merge)

1. **`InstallPathResolver` returns an unusable empty prefix for `Unknown`, contrary to the canonical startup algorithm.**

   `InstallPathResolver.Resolve` returns `(InstallMode.Unknown, string.Empty)` when neither sidecar location exists (`src/Aspire.Cli/Acquisition/InstallPathResolver.cs`), and `InstallPathResolverTests.Resolve_NoSidecar_ReturnsUnknown` asserts that behavior. The canonical design §2.4 says the unknown branch returns `(Mode.Unknown, binaryDir)`, and §2.3 says a missing/malformed sidecar should still let the CLI run while treating route as unknown.

   Impact: PR3 consumers that use `Prefix` for bundle/version layout cannot safely operate in the documented no-sidecar fallback path; they will get an empty path instead of a concrete prefix. Fix the unknown branch to return the resolved binary directory (or the exact fallback prefix chosen by the final PR3 startup algorithm) and update the test so the primitive preserves a usable prefix even when route detection fails.

2. **The PowerShell PR installer writes a Unix/bash update command into Windows PR sidecars.**

   Both PowerShell PR install paths write `{ "route": "pr", "updateCommand": "get-aspire-cli-pr.sh -r <N>" }` (`eng/scripts/get-aspire-cli-pr.ps1` in `Start-InstallFromLocalDir` and `Start-DownloadAndInstall`). That command uses the `.sh` script and the bash `-r` spelling, not the PowerShell script users just ran.

   Impact: on Windows/PowerShell PR-route installs, `aspire update --self` will eventually print an update command that is not directly runnable in the user's environment and may not exist on PATH. Fix the `.ps1` sidecar content to use a PowerShell-appropriate command, e.g. `get-aspire-cli-pr.ps1 -PRNumber <N>` (or the repo-approved invocation form), and update the PowerShell tests so they do not assert the bash command.

### Minor (nice-to-have)

1. **Help text for PR installers still describes the old default CLI install location.**

   The bash help says `CLI installs to: <install-path>/bin`, and the PowerShell parameter help says `CLI will be installed to InstallPath\bin`; PR installs now use `<install-path>/dogfood/pr-<N>/bin`. This is documentation drift rather than a runtime bug, but it will mislead users dogfooding PR builds.

### Compliments / patterns to keep

- PR number handling is strongly constrained: bash accepts only positive decimal PR numbers, and PowerShell uses `[int]` plus `[ValidateRange]`, so the new `dogfood/pr-<N>` path construction is not exposed to PR-number path injection.
- The `IsRidSpecificToolPackage` gate is in the right direction for dotnet-tool packages: RID-specific pack gets the sidecar, pointer pack omits it, and the verifier checks both.
- The resolver avoids reflection/serialization and is AOT-safe for the new primitive.

## Out-of-band notes

- I did not post GitHub review comments; this report is the requested deliverable.
- I did not run the full build/test suite because this was a review-only pass and the issues above are visible from the diff and design contract.
