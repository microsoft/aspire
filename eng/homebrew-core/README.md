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

- `aspire.rb.template` — canonical formula template (`class Aspire`, rendered to
  `aspire.rb`). Placeholders (`SDK_VERSION`, `SHA_*`, `SRC_VERSION`, `SRC_SHA`)
  are substituted per release. The `def install` block is a thin delegation: it
  stages the per-RID SDK resource (the one step that must stay Ruby) and calls
  `install-formula.sh`. Keeping the imperative logic out of the formula means the
  submitted homebrew-core formula only bumps url/sha256/version and resource SHAs.
- `install-formula.sh` — entry point the formula's `install` block invokes. Builds
  the CLI (via `build-cli.sh --no-embed`) and lays out the keg in the canonical
  sidecar-route shape under `libexec/` (`versions/<v>/{managed,dcp}`, the `bundle`
  symlink, the `.aspire-install.json` sidecar, and the `bin/aspire` symlink). This
  is where layout changes land — they travel in the source tarball and are
  validated by `homebrew-formula.yml`, so new releases keep working without a
  hand-edited formula.
- `build-cli.sh` — single-shot bundled build `install-formula.sh` invokes. With
  `--no-embed` it produces an unbundled NativeAOT `aspire` plus the sibling bundle
  layout (`managed/`, `dcp/`) that `install-formula.sh` reorganizes under `libexec/`.
- Rendering + validation are driven by `.github/workflows/homebrew-formula.yml`
  (it resolves SDK/source SHAs and renders the template inline); there is no
  standalone `generate-formula.sh` yet. The release-pipeline bump automation
  (service account `aspire-homebrew-bot`) will add a generator when submission
  lands.

## Open decisions — resolved

- **Formula name:** `aspire` (fallback `aspire-cli` only if homebrew-core
  maintainers object at submission).
- **Bump-PR identity:** service account `aspire-homebrew-bot` (used by the
  release-pipeline bump automation; not built yet).
- **First-submission target:** validated against latest `main` (a preview
  version) for now; switch to a stable release tag before submitting.
- **SDK:** vendor per-RID `resource` blocks (see below).

## Design decisions (issue #17880 spike outcomes)

- **SDK acquisition:** per-RID `resource` blocks, version sourced from
  `global.json`. Not `depends_on "dotnet"` — homebrew-core's `dotnet` formula
  tracks the 1xx feature band only, and we need the exact SDK we develop
  against.
- **Bundle layout:** the `install` block builds with `--no-embed` and lays the
  unwrapped bundle out under `libexec/versions/<v>/{managed,dcp}` with a
  `libexec/bundle` symlink (the canonical sidecar-route shape). No
  self-extracting transport and no runtime extraction, so the Cellar prefix is
  not mutated post-install and nothing lands in `~/.aspire/{versions,bundles,bin}`.
- **Bottle matrix:** ship all four — `arm64_sonoma`, `sonoma`,
  `arm64_linux`, `x86_64_linux`. NativeAOT publishes cleanly on all four
  with only system stdlib runtime deps.
- **Install-route sidecar:** writes `{"source":"brew"}` to
  `.aspire-install.json` next to the installed binary. Same wire value as the
  cask; the CLI treats them identically via `InstallSource.Brew`.

## Validation rings

| Ring | Scope | Where it runs |
|---|---|---|
| A — static | `ruby -c`, `brew style`, `brew audit --strict --new`, `brew test-bot --only-tap-syntax` | local; PR CI on `eng/homebrew-core/**` change |
| B — build / install | `brew install --build-from-source aspire` on each supported platform; the formula's `test do`; `aspire doctor` route + `update --self` gate + no-`~/.aspire`-mutation post-install checks | PR CI on `eng/homebrew-core/**` change; release pipeline pre-bump |
| B2 — CLI e2e | `HomebrewInstalledCliTests` drives the brew-installed CLI in a non-Docker host terminal: doctor route, `update --self` gate, config round-trip, and a `new→start→ps→describe→stop` lifecycle (set `ASPIRE_E2E_HOMEBREW=true`) | PR CI install job (osx-arm64 + linux-x64) |
| C — live / online | `brew audit --online --new aspire` against generated formula pointing at the live release URLs | release pipeline post-tag, before opening the bump PR |
