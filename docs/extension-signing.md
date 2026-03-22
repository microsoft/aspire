# VS Code Extension Signing

This document explains how the Aspire VS Code extension is signed for publication to the Visual Studio Marketplace. The signing process involves several files across the repository and runs as part of the internal CI pipeline.

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

The main build step (`./build.sh -restore -build -pack -sign -publish`) builds the VS Code extension via `extension/Extension.proj`. This project runs `vsce package` to create the `.vsix` file and `vsce generate-manifest` to create a manifest file that contains a hash of the VSIX contents.

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

Finally, `vsce publish` uploads the VSIX along with its manifest and signature to the VS Marketplace.

## Configuration

In `eng/Signing.props`, the `.vsix` extension is mapped to `CertificateName="None"`:

```xml
<FileExtensionSignInfo Include=".vsix" CertificateName="None" />
```

The VSIX is also excluded from `ItemsToSign` to prevent Arcade's signing infrastructure from modifying it. Again, VS Code extensions are authenticated using the signature file and manifest, which is which `vsce publish` accepts signature and manifest arguments.