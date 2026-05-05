# Native CLI Packaging

This document explains how CI produces Aspire CLI native archives and dotnet-tool packages, and why official signed builds treat the native CLI archive as the canonical signed payload.

## Produced artifacts

The native CLI packaging flow produces two related artifact families:

1. Native CLI archives named `aspire-cli-<rid>-<version>.zip` or `aspire-cli-<rid>-<version>.tar.gz`.
2. Dotnet-tool NuGet packages:
   - RID-specific packages such as `Aspire.Cli.osx-arm64.<version>.nupkg`.
   - The `Aspire.Cli.<version>.nupkg` pointer package used by `dotnet tool install`.

The archive contains the native `aspire` executable for direct installation and for installer feeds such as Homebrew and WinGet. The RID-specific tool package contains the same native CLI executable under the dotnet tool layout.

## GitHub Actions PR builds

The GitHub Actions test workflow builds native CLI archives per OS/RID and uploads them as `cli-native-archives-*` artifacts. Package-dependent test lanes download the RID-specific packages produced by the matching archive job.

These PR artifacts are useful for validation and dogfooding, but they are not the final official signed release artifacts.

## Internal signed builds

The internal Azure DevOps `build_sign_native` stage is responsible for producing signed native CLI artifacts. Windows and macOS native jobs run with code signing enabled so each OS/arch-specific native executable can be signed or notarized on the appropriate platform.

MicroBuild treats each CLI archive as a signing container. It extracts signable nested files such as `aspire.exe`, `aspire`, and native libraries to `artifacts/tmp/<Configuration>/ContainerSigning`, signs them, and writes the signed streams back into the archive. It does not update Arcade's original `PublishToDisk` staging directory.

Because of that, the archive is the canonical payload after signing. Consumers that need the final native CLI binary should read it from the signed archive, not from `artifacts/obj/.../output` or another intermediate staging directory.

## Dotnet-tool package signing constraints

The native `aspire` binary has to be signed/notarized on the OS/arch-specific native jobs. That means the macOS native job can produce a signed/notarized `aspire` binary and archive, but it cannot produce a signed `.nupkg` because NuGet package signing only happens on Windows.

If the macOS job produced a tool `.nupkg` from an unsigned binary and the main Windows job signed that package later, NuGet signing would fail because the `aspire` binary inside the package is not signed.

The tool packaging flow therefore extracts the signed `aspire` binary from the signed native archive and packs that into the RID-specific tool `.nupkg`. This produces an unsigned `.nupkg` that contains the signed `aspire` CLI; the `.nupkg` itself is signed later by the main Windows job.

Linux native jobs do not sign ELF binaries in `build_sign_native`. For Linux, extracting from the native archive gives the same unsigned ELF payload, and the resulting NuGet packages are signed later as packages by the main Windows build.
