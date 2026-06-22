{
  lib,
  stdenv,
  fetchurl,
  makeWrapper,
  autoPatchelfHook ? null,
  icu,
  openssl,
  zlib,
  manifest ? builtins.fromJSON (builtins.readFile ./versions.json),
}:
let
  system = stdenv.hostPlatform.system;
  entry =
    manifest.systems.${system}
      or (throw "Aspire CLI does not publish a Nix package for system '${system}'.");
  isLinux = stdenv.hostPlatform.isLinux;
  linuxLibraries = [
    icu
    openssl
    zlib
    stdenv.cc.cc.lib
  ];
  linuxLibraryPath = lib.makeLibraryPath linuxLibraries;
in
stdenv.mkDerivation {
  pname = "aspire-cli";
  inherit (manifest) version;

  src = fetchurl {
    inherit (entry) url hash;
  };

  sourceRoot = ".";
  nativeBuildInputs =
    [ makeWrapper ]
    ++ lib.optionals isLinux [
      autoPatchelfHook
    ];
  buildInputs = lib.optionals isLinux linuxLibraries;

  dontConfigure = true;
  dontBuild = true;
  dontStrip = true;

  installPhase = ''
    runHook preInstall

    mkdir -p "$out/lib/aspire-cli" "$out/bin"
    cp -R . "$out/lib/aspire-cli/"
    chmod 755 "$out/lib/aspire-cli/aspire"

    printf '%s\n' '{"source":"nix"}' > "$out/lib/aspire-cli/.aspire-install.json"

    makeWrapper "$out/lib/aspire-cli/aspire" "$out/bin/aspire" ${lib.optionalString isLinux ''--prefix LD_LIBRARY_PATH : "${linuxLibraryPath}"''}

    runHook postInstall
  '';

  passthru = {
    inherit manifest;
    inherit (entry) archiveName rid;
  };

  meta = {
    description = "Command line tool for Aspire developers";
    homepage = "https://aspire.dev";
    changelog = "https://github.com/microsoft/aspire/releases/tag/${manifest.releaseTag}";
    license = lib.licenses.mit;
    sourceProvenance = with lib.sourceTypes; [ binaryNativeCode ];
    platforms = builtins.attrNames manifest.systems;
    mainProgram = "aspire";
  };
}
