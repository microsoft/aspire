# Aspire CLI — homebrew-core formula

This directory holds the source-of-truth for the Aspire CLI formula intended
for `Homebrew/homebrew-core`. It is parallel to (not a replacement for)
`eng/homebrew/`, which holds the cask shipped via the Microsoft custom tap.

## Why a homebrew-core formula

The cask in `Homebrew/homebrew-cask` (and the equivalent in the Microsoft tap)
distributes prebuilt binaries; homebrew-core formulae build from source. Both
paths exist for different audiences:

| Surface | Audience | Path |
|---|---|---|
| Microsoft custom tap (cask) | Internal / early adopters via `brew tap` | `eng/homebrew/` |
| `Homebrew/homebrew-cask` (cask) | macOS users wanting a prebuilt CLI | TBD (Open Item #1 in `eng/homebrew/README.md`) |
| **`Homebrew/homebrew-core` (formula)** | **macOS + Linux users wanting `brew install aspire` with no tap** | **this directory** |

## Files

- `aspire.rb.template` — canonical formula template. Placeholders (`SDK_VERSION`,
  `SHA_*`, `SRC_VERSION`, `SRC_SHA`) are filled in per release.
- `generate-formula.sh` — *(to be added)* substitutes release-tag values into
  the template; reads SDK version from the release tag's `global.json` and
  resolves per-RID SDK URLs from `https://builds.dotnet.microsoft.com/dotnet/release-metadata/...`.
- `validate-formula.sh` — *(to be added)* drives the local Ring A + Ring B
  validation gauntlet (see issue #17880).

## Design decisions (issue #17880 spike outcomes)

- **SDK acquisition:** per-RID `resource` blocks, version sourced from
  `global.json`. Not `depends_on "dotnet"` — homebrew-core's `dotnet` formula
  tracks the 1xx feature band only, and we need the exact SDK we develop
  against. See `spike-sdk-strategy.md`.
- **Bundle extraction:** install-time, via the existing hidden command
  `aspire setup --install-path libexec --force`. No CLI change. The installed
  formula does not extract anything at runtime, so the Cellar prefix is not
  mutated post-install. See `spike-extract-dir.md`.
- **Bottle matrix:** ship all four — `arm64_sonoma`, `sonoma`,
  `arm64_linux`, `x86_64_linux`. NativeAOT publishes cleanly on all four
  with only system stdlib runtime deps. See `spike-nativeaot.md`.
- **Install-route sidecar:** writes `{"source":"brew"}` to
  `.aspire-install.json` next to the installed binary. Same wire value as the
  cask; the CLI treats them identically via `InstallSource.Brew`.

## Validation rings

| Ring | Scope | Where it runs |
|---|---|---|
| A — static | `ruby -c`, `brew style`, `brew audit --strict --new`, `brew test-bot --only-tap-syntax` | local; PR CI on `eng/homebrew-core/**` change |
| B — build / install | `brew install --build-from-source aspire` on each supported platform; cross-route coexistence with cask; upgrade scenarios | PR CI on `eng/homebrew-core/**` change; release pipeline pre-bump |
| C — live / online | `brew audit --online --new aspire` against generated formula pointing at the live release URLs | release pipeline post-tag, before opening the bump PR |
