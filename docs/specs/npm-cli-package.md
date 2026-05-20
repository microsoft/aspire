# Aspire CLI npm package POC

## Summary

The Aspire CLI npm package POC distributes the native Aspire CLI through npm using the same native binary archives that feed the dotnet tool packages. The npm package shape follows the native-package convention used by tools such as esbuild and @vscode/ripgrep: a small top-level package exposes the command, while platform-specific packages carry the native payload.

The POC package name is `@microsoft/aspire-cli`. It is intentionally a POC and does not yet include npm publishing, provenance, registry authentication, or CLI-side npm self-update behavior.

## Research and prior art

This design follows the native npm package pattern used by established packages rather than introducing a custom installer.

- npm's package metadata defines the primitives this POC uses: `bin` exposes a command on PATH, `optionalDependencies` allow platform-specific packages to be skipped when they do not apply, `files` limits packed content, and `os`/`cpu` select packages by `process.platform` and `process.arch`. See the npm package.json documentation for [`bin`](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#bin), [`optionalDependencies`](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#optionaldependencies), [`files`](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#files), [`os`](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#os), and [`cpu`](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#cpu).
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

## Non-goals

- Implementing `aspire update --self` for npm installs.
- Replacing the dotnet tool package flow.

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

The launcher sets these environment variables for future CLI-side npm install detection:

```text
ASPIRE_NPM_PACKAGE
ASPIRE_NPM_PACKAGE_VERSION
ASPIRE_NPM_PACKAGE_RID
```

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

`eng\scripts\stage-native-cli-tool-packages.ps1` stages npm `.tgz` artifacts alongside existing native CLI nupkgs. Npm packages are additive: if no npm packages are present, staging warns and continues so older nupkg-only flows remain valid.

Only one pointer package is staged. The default canonical pointer source is `native_archives_win_x64`, matching the existing native CLI package staging convention. All RID-specific packages are staged.

## Pipeline integration

The native archive workflows verify npm tarballs after the existing nupkg verification.

GitHub Actions uploads `microsoft-aspire-cli*.tgz` with the RID-specific package artifacts. Azure Pipelines installs Node.js before native package build because `npm pack` runs during packaging, verifies the npm packages, downloads `microsoft-aspire-cli*.tgz` in the staging job, and stages them with the native CLI packages.

## Publishing

The npm packages are published using the manual GitHub Actions workflow `.github/workflows/publish-npm.yml`.

### Prerequisites

Before publishing npm packages, the following external setup must be complete:

1. **@microsoft scope reservation**: The `@microsoft` scope must be reserved and managed by Microsoft on the npm registry.
2. **npm Trusted Publisher configuration**: The microsoft/aspire repository must be configured as a Trusted Publisher on npm to enable provenance via OIDC. This eliminates the need for long-lived NPM_TOKEN secrets.
3. **Microsoft/1ES-managed publishing**: npm publishing should be managed through Microsoft's 1ES-approved processes and infrastructure.

The workflow includes a temporary fallback to `secrets.NPM_TOKEN` authentication, but Trusted Publisher OIDC is the recommended approach. The fallback will be removed once Trusted Publisher setup is complete.

### Publishing workflow

The publish workflow downloads staged npm tarballs from a completed native archive build and publishes them to npm with provenance:

```bash
# Example: Publish packages from a build run
gh workflow run publish-npm.yml \
  --ref main \
  --field release_version="9.2.0" \
  --field run_id="12345678" \
  --field dist_tag="latest" \
  --field dry_run="false"
```

Key features:

- **Dry run by default**: The workflow defaults to `dry_run: true` to validate packages before real publish.
- **Platform-first ordering**: RID-specific packages are published before the meta package to avoid `optionalDependencies` validation races.
- **Propagation wait**: The workflow polls npm until all RID packages for the target version are visible before publishing the meta package.
- **Recovery options**: The `only_rid` and `skip_meta` inputs support publishing specific RID packages or skipping the meta package if recovery is needed.
- **Provenance**: All publishes use `npm publish --provenance` when not in dry-run mode.
- **Authorization**: Non-dry-run publishes require admin or maintain repository permissions.

For full workflow documentation, see the workflow file header and inline comments.

## Open follow-ups

- Decide whether the launcher should use stronger cache freshness than file-size comparison.
- Implement CLI-side npm install detection and self-update guidance.
- Add end-to-end installation tests against a real npm install layout.
