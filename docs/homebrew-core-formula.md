# Aspire CLI homebrew-core formula — build requirements

Acceptance criteria for the Aspire CLI binary produced by
`brew install aspire` (the `Homebrew/homebrew-core` *formula* route, built
from source — distinct from the cask route under `eng/homebrew/`, which
installs a prebuilt binary).

The formula's `install` block delegates to
`eng/homebrew-core/install-formula.sh` → `eng/homebrew-core/build-cli.sh`,
which mirrors the internal native CLI build
(`eng/pipelines/templates/build_sign_native.yml`) minus signing.

## Requirements

| # | Requirement | How it's met |
|---|---|---|
| 1 | **NativeAOT, correct arch** — Mach-O `arm64`/`x86_64` on macOS, ELF on Linux. | NativeAOT publish of `Aspire.Cli` via `eng/Bundle.proj`. Verify with `file <libexec>/aspire`. |
| 2 | **Proper stable version** (e.g. `13.5.0`). | Built with the version an internal `-ci` build carries, computed from `eng/Versions.props` and stamped via `VersionPrefixOverride`/`VersionSuffix` (which Bundle.proj forwards to the nested publish): stabilized `VersionPrefix` at a release branch/tag (`StabilizePackageVersion=true`), `VersionPrefix-ci` otherwise. The formula `version` is set to the same value, so `aspire --version` matches it (and the `test do` assertion holds). Verified on osx-arm64 dogfood: `aspire --version` → `13.5.0-ci`. |
| 3 | **channel = stable**. | `install-formula.sh` passes `--channel stable`; `aspire doctor` reports Route=brew, Channel=stable. |

## Out of scope (explicitly dropped)

- **Notarization** — does not apply to the from-source formula route. The
  binary is compiled on the user's machine, so it carries no quarantine
  attribute and Gatekeeper runs it without notarization; per-user
  notarization at `brew install` time is not possible. Notarization is the
  **cask** route's concern (prebuilt binary signed + notarized by
  `build_sign_native.yml`).
- **Code signing** — homebrew-core formulae ship unsigned (built from
  source). Verified: `brew test` (`aspire new`) runs on macOS unsigned.

## Build-command parity (with `build_sign_native.yml`, minus signing)

- Same publish/restore/layout commands, driven through `eng/Bundle.proj`'s
  `Build` target (`_PublishManagedProjects` → `_RestoreDcpPackage` →
  `_RunCreateLayout` → `_PublishNativeCli`); the pipeline only splits these
  into separate steps to interleave signing, which the formula has none of.
- **Version** is stamped via `VersionPrefixOverride`/`VersionSuffix` (which
  Bundle.proj forwards to the nested `dotnet publish`) rather than by building
  under `ContinuousIntegrationBuild=true`. That property is not forwarded to
  the nested publishes, and running the whole bundle build under CI would also
  rebuild aspire-managed + Aspire.Dashboard under CI — which the pipeline does
  not do. The override value is computed to equal what an internal `-ci` build
  produces (see the version row above).
- `--no-embed`: the formula lays out the unwrapped bundle
  (`versions/<v>/{managed,dcp}` + `bundle` symlink) under `libexec/` instead
  of embedding the self-extracting archive. This is the intentional
  divergence from the pipeline (saves ~100 MB, no runtime extraction).
