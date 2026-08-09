{
  description = "Aspire CLI";

  inputs = {
    # Default package set used when evaluating this flake directly. Consumers can
    # make this input follow their own pinned nixpkgs input.
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
  };

  outputs =
    { self, nixpkgs }:
    let
      manifest = builtins.fromJSON (builtins.readFile ./eng/nix/versions.json);
      supportedSystems = builtins.attrNames manifest.systems;
      forAllSystems = nixpkgs.lib.genAttrs supportedSystems;

      packageFor =
        system:
        let
          pkgs = nixpkgs.legacyPackages.${system};
        in
        pkgs.callPackage ./eng/nix/package.nix {
          inherit manifest;
        };
    in
    {
      packages = forAllSystems (
        system:
        let
          aspireCli = packageFor system;
        in
        {
          aspire-cli = aspireCli;
          default = aspireCli;
        }
      );

      apps = forAllSystems (system: {
        aspire-cli = {
          type = "app";
          program = "${self.packages.${system}.aspire-cli}/bin/aspire";
          meta.description = "Run the Aspire CLI";
        };
        default = self.apps.${system}.aspire-cli;
      });

      overlays.default = final: _prev: {
        aspire-cli = final.callPackage ./eng/nix/package.nix {
          inherit manifest;
        };
      };

      checks = forAllSystems (system: {
        aspire-cli = self.packages.${system}.aspire-cli;
      });

      formatter = forAllSystems (system: nixpkgs.legacyPackages.${system}.nixfmt);
    };
}
