# Nix package for the Aspire CLI

This directory contains the first-party Nix packaging for the Aspire CLI.

The package is a binary package: it fetches the official versioned GitHub release archive for the current system, pins it by hash, expands the embedded bundle during installation, and writes the Nix install-route sidecar into the package output. It does not build the Aspire repository from source.

## Bundle installation and NixOS

The derivation runs `aspire setup --install-path` to expand the bundle under `$out/lib/aspire-cli` while the output is writable. On Linux it first patches the outer CLI so setup can run in the Nix sandbox, then uses `autoPatchelfHook`'s helper to patch the expanded host executables and shared libraries, including DCP, the managed host, Dashboard, and TerminalHost when present. DCP's container-side tunnel client (`dcptun_c`) remains unchanged because it runs inside an Azure Linux container, not on the Nix host. The wrapper supplies the library search path for libraries loaded dynamically, including OpenSSL and ICU. All shipped binaries retain their existing dynamic linking; no static builds or system-wide `programs.nix-ld` configuration are required.

At runtime, the `nix` install route uses the package-owned layout without extracting the embedded payload again or creating bundle leases. Caches, logs, and other user state still use the normal Aspire home directory. Upgrade or repair the bundle through Nix, not by extracting new binaries into the store.

## Runtime validation

The Nix CI job builds `runtime-image.nix` from the current CLI archive and runs `runtime-smoke/run.sh` in a non-root Nix-only image with a read-only root filesystem and writable workspace and `/tmp` mounts. The image has no generic Linux ELF interpreter or `nix-ld`; an unpatched DCP control must fail before the packaged CLI is exercised.

The smoke creates an AppHost from the matching template package, starts an HTTP project, and verifies a container can reach it through Aspire's container tunnel. It checks Dashboard availability, package-owned executable paths, absence of runtime bundle extraction, and cleanup across two start/stop cycles. Host networking, the Docker socket, and identical host/container workspace paths are used only on the disposable Linux CI runner.

This validates the loader and filesystem constraints relevant to NixOS, not a booted NixOS system or the macOS tray. Diagnostics are uploaded as the `nix-runtime-smoke` artifact.

## Usage

Run the Aspire CLI directly:

```sh
nix run github:microsoft/aspire#aspire-cli
```

Install it into a Nix profile:

```sh
nix profile add github:microsoft/aspire#aspire-cli
```

Use it from a project flake by making Aspire follow the same `nixpkgs` input your project already pins. The `nixpkgs` input controls the Nix package set used to evaluate the derivation; the Aspire CLI version is pinned by the `aspire` input revision and `eng/nix/versions.json`.

```nix
{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/<your-pinned-branch-or-rev>";
    aspire.url = "github:microsoft/aspire";
    aspire.inputs.nixpkgs.follows = "nixpkgs";
  };

  outputs =
    { nixpkgs, aspire, ... }:
    let
      system = "x86_64-linux";
      pkgs = nixpkgs.legacyPackages.${system};
    in
    {
      devShells.${system}.default = pkgs.mkShell {
        packages = [
          aspire.packages.${system}.aspire-cli
        ];
      };
    };
}
```

Use the overlay the same way:

```nix
{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/<your-pinned-branch-or-rev>";
    aspire.url = "github:microsoft/aspire";
    aspire.inputs.nixpkgs.follows = "nixpkgs";
  };

  outputs =
    { nixpkgs, aspire, ... }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs {
        inherit system;
        overlays = [
          aspire.overlays.default
        ];
      };
    in
    {
      devShells.${system}.default = pkgs.mkShell {
        packages = [
          pkgs.aspire-cli
        ];
      };
    };
}
```

## Supported systems

| Nix system | Aspire CLI runtime identifier |
|---|---|
| `x86_64-linux` | `linux-x64` |
| `aarch64-linux` | `linux-arm64` |
| `x86_64-darwin` | `osx-x64` |
| `aarch64-darwin` | `osx-arm64` |

## Updating the packaged version

The `release-publish-nuget` Azure DevOps pipeline dispatches `.github/workflows/update-nix-cli-flake.yml` as part of a stable release. The pipeline reads the `aspire-cli-*.tar.gz.sha512` checksums from the signed source build's `BlobArtifacts` (the same artifacts it verifies and uploads to the release) and passes them to the workflow as inputs, so the manifest is built from the build rather than the GitHub release. This lets the manifest be produced while the release is still an unpublished **draft** — required for GitHub's immutable releases, where a draft's assets are not served from the public `releases/download/...` URL. The workflow commits the Nix manifest change to the `update-baseline-<version>` branch created by `release-github-tasks.yml`, and creates or updates the baseline PR so the post-release stable version updates stay together.

Merging the baseline PR is the in-repo Nix "ship" step: it updates the flake metadata in `main` to point at the GitHub release assets and hashes (the `url` fields are always the public versioned release download URLs, which become live once the release manager publishes the draft). There is no separate Nix registry publish step in this repository.

To update the manifest manually against an **already-published** stable release, pass the same stable version used by the release baseline PR:

```sh
eng/nix/update-versions.sh --version <stable-release-version>
```

Run manually like this, the script downloads each official `.sha512` checksum asset from the published release and rewrites `versions.json` with Nix SRI hashes. When the release pipeline runs it instead, it passes `--sha512 <rid>=<hex>` for all four platforms so no release read happens (see the script header for the offline-mode contract). Either way the manifest uses versioned GitHub release URLs, not mutable `aka.ms` channel redirects, so Nix fixed-output fetches remain reproducible.

## Relationship to nixpkgs

This flake is the in-repository first-party package. An upstream `NixOS/nixpkgs` derivation can reuse the same release asset URL shape, runtime identifier mapping, and `nix` install-route sidecar behavior to provide the `pkgs.aspire-cli` experience.
