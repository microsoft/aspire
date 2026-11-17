# Native CLI Packaging

This document explains how CI produces Aspire CLI native archives and dotnet-tool packages, and why official signed builds treat the native CLI archive as the canonical signed payload.

## Produced artifacts

The native CLI packaging flow produces two related artifact families:

1. Native CLI archives named `aspire-cli-<rid>-<version>.zip` or `aspire-cli-<rid>-<version>.tar.gz`.
2. Dotnet-tool NuGet packages:
   - RID-specific packages such as `Aspire.Cli.osx-arm64.<version>.nupkg`.
   - The `Aspire.Cli.<version>.nupkg` pointer package used by `dotnet tool install`.

The archive contains the native `aspire` executable plus runtime files that must stay beside it for direct installation and installer feeds such as Homebrew, WinGet, and Nix. The RID-specific tool package contains the same native CLI executable under the dotnet tool layout.

## GitHub Actions PR builds

The GitHub Actions test workflow builds native CLI archives per OS/RID and uploads them as `cli-native-archives-*` artifacts. Package-dependent test lanes download the RID-specific packages produced by the matching archive job.

These PR artifacts are useful for validation and dogfooding, but they are not the final official signed release artifacts.

## Internal signed builds

The internal Azure DevOps `build_sign_native` stage is responsible for producing signed native CLI artifacts. Windows and macOS native jobs run with code signing enabled so each OS/arch-specific native executable can be signed or notarized on the appropriate platform.

MicroBuild treats each CLI archive as a signing container. It extracts signable nested files such as `aspire.exe`, `aspire`, and native libraries to `artifacts/tmp/<Configuration>/ContainerSigning`, signs them, and writes the signed streams back into the archive. It does not update Arcade's original `PublishToDisk` staging directory.

Because of that, the archive is the canonical payload after signing. Consumers that need the final native CLI binary should read it from the signed archive, not from `artifacts/obj/.../output` or another intermediate staging directory.

### macOS tray notarization checks

`src/Aspire.Tray/Mac/Aspire.Tray.Mac.csproj` staples and validates the restored,
notarized tray app. `tools/CreateLayout/verify-tray-payload.sh` validates it again
after extracting the packaged payload, between signature verification and
Gatekeeper assessment. Both use `tools/CreateLayout/macos-stapler-with-retry.sh`.

Apple's `stapler` requires internet access for both operations. The helper retries
only failures containing `Error Domain=NSURLErrorDomain Code=-1005` (connection
lost), with four total attempts and 5, 15, and 30 second delays. Each attempt's
output and each retry are logged. Other failures stop immediately; exit 68 alone
is not retryable because it can also mean missing notarization or an unexpected
ticket-service response. Exhaustion propagates the final failing exit code.

Retries repeat only the failed stapler operation, not signing, notarization
submission, or the build. All signature, ticket, and Gatekeeper checks remain
required; a network failure never turns verification into success.

## Dotnet-tool package signing constraints

The native `aspire` binary has to be signed/notarized on the OS/arch-specific native jobs. That means the macOS native job can produce a signed/notarized `aspire` binary and archive, but it cannot produce a signed `.nupkg` because NuGet package signing only happens on Windows.

If the macOS job produced a tool `.nupkg` from an unsigned binary and the main Windows job signed that package later, NuGet signing would fail because the `aspire` binary inside the package is not signed.

The tool packaging flow therefore extracts the signed `aspire` binary from the signed native archive and packs that into the RID-specific tool `.nupkg`. This produces an unsigned `.nupkg` that contains the signed `aspire` CLI; the `.nupkg` itself is signed later by the main Windows job.

Linux native jobs do not sign ELF binaries in `build_sign_native`. For Linux, extracting from the native archive gives the same unsigned ELF payload, and the resulting NuGet packages are signed later as packages by the main Windows build.

## Native AOT Dashboard package signing

All seven Dashboard SDK NuGet packages follow the CLI package-signing path: `assemble` stages them together in `Shipping` before its Windows signing pass. There is no second Dashboard staging pass after signing. When NuGet signing is enabled, this ensures Linux packages receive NuGet signatures even though Linux native jobs skip ELF signing.

NuGet signing follows `PostBuildSign` in `eng/pipelines/common-variables.yml`: release-branch and manually queued official builds set it to `false`, enabling Arcade's default package-signing inputs. Automatic main builds set it to `true`, excluding NuGet packages from in-build signing; native executable and npm signing are selected separately.

Assembly verifies every staged NuGet release package's signature before publishing build artifacts only when real signing is enabled and `PostBuildSign` is not `true`. This matches package-signing selection rather than assuming that a real-signed build signs every artifact. The unsigned `*.symbols.nupkg` files also staged in `Shipping` are excluded because they are symbol-server inputs, not NuGet release packages.

## Nix flake packaging

The root `flake.nix` packages the stable Aspire CLI from the versioned GitHub release archive URLs and hashes tracked in `eng/nix/versions.json`. It is a binary package, not a Nix source build of this repository. This keeps the Nix package aligned with the same canonical signed native archive consumed by the other installers.

For stable releases, `release-publish-nuget`'s `UpdateNixPackageJob` dispatches `.github/workflows/update-nix-cli-flake.yml`. The job reads the `aspire-cli-*.tar.gz.sha512` checksums from the signed source build's `BlobArtifacts` and passes them to the workflow as inputs, so the manifest is built from the build — not from the GitHub release. This lets it run while the release is still a **draft** (immutable releases don't serve draft assets from the public download URL). The workflow runs:

```sh
eng/nix/update-versions.sh --version <VERSION> --sha512 osx-arm64=<hex> --sha512 osx-x64=<hex> --sha512 linux-arm64=<hex> --sha512 linux-x64=<hex>
```

The workflow commits the Nix manifest change to the `update-baseline-<VERSION>` branch created by `release-github-tasks.yml`, then creates or updates the baseline PR. Merging that PR is the in-repo Nix "ship" step: it publishes the flake metadata that points at the versioned release download URLs (which go live when the release manager publishes the draft). When the `--sha512` inputs are supplied the updater uses them directly; run manually without them (against an already-published release) it downloads the official `.sha512` assets instead. Either way it writes Nix-compatible SRI hashes. Do not point the manifest at mutable `aka.ms` channel URLs; Nix fixed-output fetches require stable versioned URLs.

The Nix derivation writes `{"source":"nix"}` to `.aspire-install.json` next to the packaged native binary. `BundleService` treats this route as read-only and extracts the embedded bundle payload into the user-owned Aspire home instead of the Nix store.
