# Aspire CLI npm package

## Summary

The Aspire CLI is distributed through npm using the same signed native binary archives that feed the dotnet tool packages. The npm package shape follows the native-package convention used by tools such as esbuild and `@vscode/ripgrep`: a small top-level package exposes the command, while platform-specific packages carry the native payload.

The published package is `@microsoft/aspire-cli`. It supports global installation (`npm install -g @microsoft/aspire-cli`) on Windows, macOS, and Linux (glibc and musl). The CLI detects npm installs at runtime and routes `aspire update --self` and update notifications through the npm package manager rather than the GitHub-binary downloader.

## Research and prior art

This design follows the native npm package pattern used by established packages rather than introducing a custom installer.

- npm's package metadata defines the primitives this package uses: `bin` exposes a command on PATH, `optionalDependencies` allow platform-specific packages to be skipped when they do not apply, `files` limits packed content, and `os`/`cpu` select packages by `process.platform` and `process.arch`. See the npm package.json documentation for [`bin`](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#bin), [`optionalDependencies`](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#optionaldependencies), [`files`](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#files), [`os`](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#os), and [`cpu`](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#cpu).
- [esbuild](https://github.com/evanw/esbuild/blob/main/npm/esbuild/package.json) uses a top-level package with a `bin` entry and platform-specific packages such as [`@esbuild/linux-x64`](https://github.com/evanw/esbuild/blob/main/npm/@esbuild/linux-x64/package.json) listed as optional dependencies. Its platform resolver maps the current Node platform to an optional package and resolves the binary through Node package resolution rather than hardcoded `node_modules` paths.
- [@vscode/ripgrep](https://github.com/microsoft/vscode-ripgrep/blob/main/packages/ripgrep/package.json) uses the same top-level package plus optional platform package shape for an external CLI binary. A platform package such as [`@vscode/ripgrep-linux-x64`](https://github.com/microsoft/vscode-ripgrep/blob/main/packages/ripgrep-linux-x64/package.json) contains a `bin/` payload and declares `os`/`cpu` metadata.
- Rollup, SWC, and sharp show the modern `libc` split for Linux native packages. Examples include [`@rollup/rollup-linux-x64-gnu`](https://github.com/rollup/rollup/blob/master/npm/linux-x64-gnu/package.json), [`@swc/core-linux-x64-gnu`](https://github.com/swc-project/swc/blob/main/packages/core/scripts/npm/linux-x64-gnu/package.json), and [`@img/sharp-linux-x64`](https://github.com/lovell/sharp/blob/main/npm/linux-x64/package.json). Their runtime loaders also distinguish glibc from musl before selecting a native package.
- Yarn's package portability guidance says packages should not write inside their own package directory outside postinstall because package directories may be read-only or backed by package-manager stores. See ["Packages should never write inside their own folder outside of postinstall"](https://yarnpkg.com/advanced/rulebook#packages-should-never-write-inside-their-own-folder-outside-of-postinstall).

The Aspire-specific adaptation is the writable cache. Unlike most native npm CLIs, the Aspire CLI self-extracts an embedded bundle relative to its process path on first run. That means running the binary directly from the RID package would make the extraction root depend on the package-manager layout. Copying to `~/.aspire/npm/<version>/<rid>/bin` keeps npm installation mechanics separate from Aspire's first-run extraction layout.

## Goals

- Produce npm tarballs as part of the same native CLI package build that produces the dotnet tool packages.
- Keep the signed native CLI archive as the canonical payload for both dotnet tool and npm package outputs.
- Use a top-level npm package with a JavaScript `bin` launcher and RID-specific optional native packages.
- Avoid running the self-extracting native binary directly from `node_modules` or package-manager stores.
- Verify generated npm packages against the native archive before staging them.
- Validate the published packages with a real `npm install -g` test in CI before allowing the release pipeline to submit them.
- Publish through the Azure DevOps release pipeline using Microsoft's ESRP-backed MicroBuild publishing flow.
- Detect npm installs at runtime so `aspire update --self` and update notifications use `npm install -g @microsoft/aspire-cli@latest` instead of the GitHub-binary downloader.

## Non-goals

- Replacing the dotnet tool package flow.
- Emitting a Sigstore provenance attestation (`npm publish --provenance`). The ESRP-backed publish path does not currently issue Sigstore attestations from a public OIDC publisher. Integrity is anchored at the signed native binary and the ESRP-tracked submission identity instead. See "Product and security tradeoffs" below.

## Package layout

The top-level package is:

```text
@microsoft/aspire-cli
```

It contains:

```text
package.json
README.md
bin/aspire.js
bin/aspire-package-map.json
```

The top-level `package.json` declares:

- `bin.aspire = "bin/aspire.js"`
- `optionalDependencies` for every supported RID package at the same version
- `files = ["bin", "README.md"]`

RID-specific packages are named by appending the RID:

```text
@microsoft/aspire-cli-win-x64
@microsoft/aspire-cli-win-arm64
@microsoft/aspire-cli-linux-x64
@microsoft/aspire-cli-linux-arm64
@microsoft/aspire-cli-linux-musl-x64
@microsoft/aspire-cli-osx-x64
@microsoft/aspire-cli-osx-arm64
```

Each RID package contains:

```text
package.json
README.md
bin/aspire
```

or on Windows:

```text
package.json
README.md
bin/aspire.exe
```

RID package metadata uses npm's platform selectors:

- Windows packages set `os = ["win32"]` and the matching `cpu`.
- macOS packages set `os = ["darwin"]` and the matching `cpu`.
- Linux glibc packages set `os = ["linux"]`, matching `cpu`, and `libc = ["glibc"]`.
- Linux musl packages set `os = ["linux"]`, matching `cpu`, and `libc = ["musl"]`.

## Launcher behavior

The top-level `aspire` command runs `bin/aspire.js`.

The launcher:

1. Loads `bin/aspire-package-map.json`.
2. Detects the current RID from `process.platform`, `process.arch`, and libc detection for Linux.
3. Resolves the matching RID package with `require.resolve("<rid-package>/package.json")`.
4. Finds `bin/aspire` or `bin/aspire.exe` inside that package.
5. Copies the native binary to an Aspire-owned writable cache.
6. Spawns the cached binary with inherited stdio and forwards all command-line arguments.

The default cache path is:

```text
~/.aspire/npm/<version>/<rid>/bin/aspire
```

or on Windows:

```text
%USERPROFILE%\.aspire\npm\<version>\<rid>\bin\aspire.exe
```

The cache root can be overridden with `ASPIRE_NPM_CACHE_DIR` for tests and diagnostics.

The copy step is required because the native Aspire CLI self-extracts its embedded bundle relative to the process path on first run. Running the binary directly from `node_modules` could make the extraction target a package directory, pnpm store, Yarn unplugged location, global npm cache, or other read-only package-manager path. Copying to the Aspire cache makes first-run extraction land under an Aspire-owned writable layout.

The launcher sets these environment variables so the CLI can detect that it was launched from an npm install (see `Aspire.Cli.Utils.NpmInstallDetection`) and route `aspire update --self` and update notifications through `npm` instead of the GitHub-binary downloader:

```text
ASPIRE_NPM_PACKAGE
ASPIRE_NPM_PACKAGE_VERSION
ASPIRE_NPM_PACKAGE_RID
```

The launcher's cache-freshness check compares both file size and `mtime`. A cached binary is reused only when its size matches the source and its `mtime` is greater than or equal to the source binary's `mtime`. Any other state (different size, older `mtime`, or unreadable cache target) copies the source through a temp file and atomically renames the temp file over the cache target.

## Build integration

`eng\clipack\Common.projitems` wires npm packing into the existing native CLI package flow.

`PackDotnetTool` depends on `PackNpmPackage`, so the native package build produces:

- the existing dotnet tool pointer package
- the existing dotnet tool RID-specific package
- the npm pointer tarball
- the npm RID-specific tarball

`PackNpmPackage` depends on `_ExtractNativeBinaryFromArchive`, which extracts `aspire` or `aspire.exe` from the native CLI archive. The npm pack script receives that extracted binary and repackages it without rebuilding or substituting another payload.

`eng\scripts\pack-cli-npm-package.ps1` generates temporary npm package directories and invokes `npm pack` for the RID package and pointer package.

## Verification and staging

`eng\scripts\verify-cli-npm-package.ps1` verifies each RID build output by:

1. Finding exactly one RID-specific npm tarball.
2. Finding exactly one npm pointer tarball.
3. Extracting both tarballs.
4. Extracting the native CLI archive.
5. Comparing the RID tarball binary byte-for-byte with the archive binary.
6. Verifying pointer package metadata, `bin/aspire.js`, `aspire-package-map.json`, and optional dependency version alignment.

`eng/pipelines/templates/prepare-npm-cli-packages.yml` runs after the byte-for-byte verification and performs real end-to-end installation tests on Windows, Linux, and the native macOS build-pool RID:

1. Installs the just-built pointer and matching RID tarball into a scratch npm prefix (`npm install -g`).
2. Invokes the installed `aspire --version` and asserts the version matches the build version.
3. Confirms the launcher's runtime cache landed under the expected `~/.aspire/npm/<version>/<rid>/bin` layout.
4. Emits a `validation-summary.json` for each platform listing each check's status. The release pipeline refuses to submit the npm packages to ESRP without successful Windows, Linux, and macOS summaries, mirroring the brew cask flow.

The source-build install validation covers three install environments: `win-x64`, `linux-x64`, and whichever macOS RID the hosted macOS pool provides (`osx-x64` or `osx-arm64`). It does not perform a real `npm install -g` smoke test for all seven RID packages on every build because the source pipeline does not currently have cheap native agents for every target RID, and cross-installing platform-filtered optional dependencies would not exercise npm's real platform selection behavior. The remaining RID coverage comes from package-shape checks, byte-for-byte payload comparison against the signed native archive, the launcher RID-drift tests, exact RID set validation in the release pipeline, and the pointer preflight that verifies every pinned RID package is present on npm before the top-level package is published. If a future change adds new RID packages, update the pack script, launcher RID mapping/tests, source-build validation artifact list, release validation summary gate, and this document together.

`eng\scripts\stage-native-cli-tool-packages.ps1` stages npm `.tgz` artifacts alongside existing native CLI nupkgs. Official CI invokes the script with `-RequireNpmPackages`, so source builds fail before release if the npm tarballs are missing. The default remains warning-only for local or older nupkg-only flows that call the script directly.

Only one pointer package is staged. The default canonical pointer source is `native_archives_win_x64`, matching the existing native CLI package staging convention. All RID-specific packages are staged.

## Pipeline integration

The native archive workflows verify npm tarballs after the existing nupkg verification and then run the end-to-end install test above.

Azure Pipelines installs Node.js before native package build because `npm pack` runs during packaging, verifies the npm packages, downloads `microsoft-aspire-cli*.tgz` in the staging job, and stages them with the native CLI packages. The source build then signs the npm `.tgz` files with detached `.tgz.sig` sidecars and publishes both as shipping flat blob artifacts so the release pipeline can consume them without treating npm tarballs as NuGet-like BAR package assets. The install-test jobs also publish platform-specific validation summary artifacts that the release pipeline aggregates into `NpmValidationSummary` and requires before publishing.

## Publishing

The npm packages are published from `eng/pipelines/release-publish-nuget.yml`, not from GitHub Actions. The release pipeline extends the MicroBuild publish-enabled 1ES template and submits packages through `MicroBuild.Publish.yml@MicroBuildTemplate` with `intent: PackageDistribution`, `contentType: npm`, and `contentSource: Folder`.

The release pipeline prepares two npm artifact folders and one validation artifact:

1. `NpmRidPackageArtifacts` contains the seven RID packages.
2. `NpmPointerPackageArtifacts` contains the top-level `@microsoft/aspire-cli` pointer package.
3. `NpmValidationSummary` contains the platform `validation-summary.json` files emitted by the source-build install tests. The release pipeline reads these summaries and refuses to invoke `MicroBuild.Publish` unless every required check passed on Windows, Linux, and macOS.

The package split is intentional. The release job submits RID packages first, waits for the ESRP submission to complete, waits an additional npm registry propagation delay, and then submits the pointer package. Publishing the pointer package last avoids optional dependency resolution races when a user installs the top-level package immediately after release.

Before publishing, the release pipeline validates that exactly one pointer tarball and exactly one tarball for each supported RID are present, that every tarball has a detached `.tgz.sig` sidecar, that all tarballs have one version, and that the `NpmValidationSummary` artifact reports `validatedByPreparePipeline: true` with every required check `passed` for Windows, Linux, and macOS install validation. Non-dry-run npm publishing resolves the default ESRP owner and approver aliases from `eng/pipelines/common-variables.yml`; explicit `NpmPublishOwners` and `NpmPublishApprovers` overrides are allowed only if they still include the required release aliases and do not overlap.

The release pipeline checks only the package groups scheduled for publishing before invoking MicroBuild. If `SkipNpmRidPublish=false`, every staged RID tarball is checked with `npm view <name>@<version> version`; if `SkipNpmPointerPublish=false`, the pointer tarball is checked the same way. Any scheduled package version that already exists on npm fails before ESRP submission, because npm versions are immutable and a duplicate publish would otherwise fail later in MicroBuild. Re-runs after partial success should use `SkipNpmRidPublish=true` only when every RID package for the selected version is already live, `SkipNpmPointerPublish=true` only when the pointer package is already live, and `SkipNpmPublish=true` only after the entire npm publish path has completed.

Stable Aspire npm releases publish through npm's default `latest` dist-tag because MicroBuild's npm publish template does not currently expose a dist-tag parameter. To prevent older servicing releases from moving `@microsoft/aspire-cli@latest` backward, the release pipeline compares the scheduled pointer package version with the current public `@microsoft/aspire-cli@latest` version and fails if the scheduled version is lower. Older servicing releases should set `SkipNpmPublish=true`; `AllowNpmLatestDistTagMove=true` exists only as an emergency release-owner override for an intentional latest-tag move.

MicroBuild's npm publish template documentation does not currently expose an npm `dist-tag` parameter. Non-dry-run prerelease npm publishing is blocked until preview packages can be submitted under a non-`latest` tag; release managers can still use `DryRun=true` to inspect the npm publish set without submitting packages.

## Product and security tradeoffs

- **No GitHub npm publishing token**: Publishing is centralized through ESRP/MicroBuild and the Microsoft1ES npm maintainer identity instead of storing an npm token in GitHub or using a repository-local GitHub Actions publisher.
- **Signed payload and tarball sidecar**: The npm tarball is created from the native CLI archive produced by the signed CI flow, and verification compares the RID tarball binary byte-for-byte with that archive. The `.tgz` package also receives an Arcade/MicroBuild detached signature sidecar (`.tgz.sig`); the release pipeline validates sidecar coverage while passing only `.tgz` package files to the npm publishing folders.
- **No Sigstore `npm publish --provenance` attestation**: ESRP's npm publish path is not currently configured to emit Sigstore provenance attestations from a public OIDC publisher. Integrity is anchored at the signed native binary, the Microsoft1ES npm maintainer identity, and the ESRP submission audit trail. When ESRP gains support for npm provenance attestations, the release pipeline should opt in and this tradeoff should be revisited.
- **Writable cache**: Copying the native binary to `~/.aspire/npm/<version>/<rid>/bin` avoids self-extraction into package-manager stores, but it means the launcher owns cache creation and freshness checks. The launcher uses size + `mtime` comparison so a stale cache from an earlier same-version install does not silently shadow a re-downloaded binary.
- **Update behavior**: npm updates remain package-manager driven (`npm install -g @microsoft/aspire-cli@latest`). The CLI detects npm installs via the launcher's environment variables and prints the npm-specific update command instead of attempting to overwrite the npm-owned binary with the GitHub-binary downloader.
- **Linux libc split**: Separate glibc and musl packages avoid shipping one Linux binary that is wrong for Alpine-style environments, at the cost of one additional optional dependency package.

## Open follow-ups

- Add Sigstore provenance once ESRP/MicroBuild's npm publish path supports it.
- Add `npm install --no-optional` guidance and a clearer error message in the launcher when no RID package is installed.
- Extract the large release-job PowerShell validation scripts once `releaseJob` can safely consume repository scripts. They remain inline today because `eng/pipelines/release-publish-nuget.yml` runs the ESRP-backed release job with `checkout: none`; extracting them now would require adding a trusted script artifact or changing release-job checkout behavior.
