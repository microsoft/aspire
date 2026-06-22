# Nix package for the Aspire CLI

This directory contains the first-party Nix packaging for the Aspire CLI.

The package is a binary package: it fetches the official versioned GitHub release archive for the current system, pins it by hash, unpacks the full archive contents, and writes the Nix install-route sidecar into the package output. It does not build the Aspire repository from source.

## Usage

Run the Aspire CLI directly:

```sh
nix run github:microsoft/aspire#aspire-cli
```

Install it into a Nix profile:

```sh
nix profile install github:microsoft/aspire#aspire-cli
```

Use it from a project flake:

```nix
{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    aspire.url = "github:microsoft/aspire";
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

Use the overlay:

```nix
{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    aspire.url = "github:microsoft/aspire";
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

The `release-publish-nuget` Azure DevOps pipeline dispatches `.github/workflows/update-nix-cli-flake.yml` after stable CLI archives and `.sha512` sidecars are uploaded to the GitHub release. That workflow runs the updater below, commits the Nix manifest change to the `update-baseline-<version>` branch created by `release-github-tasks.yml`, and creates or updates the baseline PR so the post-release stable version updates stay together.

To update the manifest manually after a stable Aspire release publishes native CLI assets to GitHub, pass the same stable version used by the release baseline PR:

```sh
eng/nix/update-versions.sh --version <stable-release-version>
```

The script downloads each official `.sha512` checksum asset and rewrites `versions.json` with Nix SRI hashes. The manifest must use versioned GitHub release URLs, not mutable `aka.ms` channel redirects, so Nix fixed-output fetches remain reproducible.

## Relationship to nixpkgs

This flake is the in-repository first-party package. An upstream `NixOS/nixpkgs` derivation can reuse the same release asset URL shape, runtime identifier mapping, and `nix` install-route sidecar behavior to provide the `pkgs.aspire-cli` experience.
