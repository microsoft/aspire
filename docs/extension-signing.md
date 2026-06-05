# VS Code Extension Signing

This document explains how the Aspire VS Code extension is signed for publication to the Visual Studio Marketplace. Signing runs as part of the internal CI pipeline; Marketplace publishing runs later from the release pipeline.

VS Code extensions require a PKCS#7 signature file (`.signature.p7s`) alongside a manifest to be verified by the Marketplace and by users. Unlike VS extensions that are Authenticode-signed, the VSIX package itself should remain unchanged after the manifest is generated—otherwise the integrity check fails.

## Key Files

The signing process is spread across these files:

- **extension/Extension.proj** — Builds the VSIX and generates the manifest
- **extension/signing/signVsix.proj** — Signs the `.signature.p7s` file with MicroBuild
- **eng/Signing.props** — Configures which files get which certificates
- **eng/Publishing.props** — Publishes signed artifacts to blob storage

## Signing Flow

The signing happens in distinct phases during the internal CI build.

### 1. Build and Package

The main build step (`./build.sh -restore -build -pack -sign -publish`) builds the VS Code extension via `extension/Extension.proj`. This project runs `vsce package` to create the `.vsix` file and `vsce generate-manifest` to create a manifest file that contains a hash of the VSIX contents. Queue the main CI build with `Package VS Code Extension as Pre-Release=true` when the signed VSIX artifact should be marked as a Marketplace pre-release package.

> **Note:** The manifest is generated from the VSIX *before* any signing occurs. The VSIX is not be modified after this point, or else the hash won't match and signature verification will fail.

### 2. Sign the Signature File

After the main build completes, the pipeline runs `extension/signing/signVsix.proj`. This project:

1. Copies the manifest file to create a `.signature.p7s` file
2. Signs the `.signature.p7s` with the `VSCodePublisher` certificate using MicroBuild
3. Validates exactly one manifest and one signature file exist

The signed `.signature.p7s` is a PKCS#7 format file that the VS Marketplace and `vsce verify-signature` can validate.

### 3. Verify

The pipeline runs `vsce verify-signature` to confirm the signature is valid before publishing.

### 4. Publish

The internal CI pipeline publishes the signed VSIX, manifest, and signature as the `aspire-vscode-extension` build artifact. The `release-publish-nuget` Azure DevOps release pipeline consumes that artifact in a 1ES `releaseJob`, verifies the signature again, verifies the `VscePublishToken`, and then runs `vsce publish` with the VSIX, manifest, and signature paths. When the release run has `IsPrerelease=true`, the extension publish step also passes `--pre-release` to `vsce`.

## Configuration

In `eng/Signing.props`, the `.vsix` extension is mapped to `CertificateName="None"`:

```xml
<FileExtensionSignInfo Include=".vsix" CertificateName="None" />
```

The VSIX is also excluded from `ItemsToSign` to prevent Arcade's signing infrastructure from modifying it. Again, VS Code extensions are authenticated using the signature file and manifest, which is why `vsce publish` accepts signature and manifest arguments.
