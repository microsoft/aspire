{
  lib,
  stdenv,
  fetchurl,
  makeWrapper,
  autoPatchelfHook ? null,
  icu,
  openssl,
  zlib,
  at-spi2-core,
  cairo,
  glib,
  gtk3,
  gtk-layer-shell ? null,
  libayatana-appindicator ? null,
  libx11,
  pango,
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
  trayLibraries = [
    at-spi2-core
    cairo
    glib
    gtk3
    gtk-layer-shell
    libayatana-appindicator
    libx11
    pango
  ];
  trayLibraryPath = lib.makeLibraryPath trayLibraries;
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
  buildInputs = lib.optionals isLinux (linuxLibraries ++ trayLibraries);

  dontConfigure = true;
  dontBuild = true;
  dontStrip = true;
  dontAutoPatchelf = true;

  # The release archive already contains the complete CLI bundle. Keep the real
  # binary and install-route sidecar together under $out/lib/aspire-cli, then
  # expose $out/bin/aspire as the user-facing wrapper.
  installPhase = ''
    runHook preInstall

    mkdir -p "$out/lib/aspire-cli" "$out/bin"
    # Install from the archive rather than the build directory: sourceRoot "."
    # also contains stdenv's generated env-vars file, whose build-specific paths
    # would make the installed output non-reproducible.
    tar -xzf "$src" -C "$out/lib/aspire-cli"
    chmod 755 "$out/lib/aspire-cli/aspire"

    printf '%s\n' '{"source":"nix"}' > "$out/lib/aspire-cli/.aspire-install.json"

    makeWrapper "$out/lib/aspire-cli/aspire" "$out/bin/aspire" ${lib.optionalString isLinux ''--prefix LD_LIBRARY_PATH : "${linuxLibraryPath}"''}

    runHook postInstall
  '';

  preFixup = ''
    # Expand the payload before fixup. Patch the outer CLI first so setup can run
    # inside the Nix sandbox.
    ${lib.optionalString isLinux ''autoPatchelf "$out/lib/aspire-cli/aspire"''}

    # Bundle directory names include the CLI mtime; do not let patchelf's wall-clock
    # timestamp make the installed layout differ between identical Nix builds.
    touch -d "@$SOURCE_DATE_EPOCH" "$out/lib/aspire-cli/aspire"

    mkdir -p "$TMPDIR/aspire-home"
    HOME="$TMPDIR/aspire-home" \
      ASPIRE_CLI_TELEMETRY_OPTOUT=true \
      features__updateNotificationsEnabled=false \
      ASPIRE_LAYOUT_PATH="$out/lib/aspire-cli" \
      ${lib.optionalString isLinux ''LD_LIBRARY_PATH="${linuxLibraryPath}"''} \
      "$out/lib/aspire-cli/aspire" setup --install-path "$out/lib/aspire-cli" --non-interactive

    ${lib.optionalString isLinux ''cp "$out/lib/aspire-cli/bundle/dcp/dcptun_c" "$TMPDIR/dcptun_c.original"''}
  '';

  postFixup = lib.optionalString isLinux ''
    # DCP copies dcptun_c into its Azure Linux image, so retain its container loader.
    # https://github.com/microsoft/dcp/blob/main/internal/dcptun/image_builder.go
    mapfile -d "" -t hostFiles < <(find "$out" -type f ! -path '*/dcp/dcptun_c' -print0)
    autoPatchelf "''${hostFiles[@]}"

    tray="$out/lib/aspire-cli/bundle/tray/aspire-tray"
    if test -f "$tray"; then
      # NativeAOT loads these GUI libraries by name, not through DT_NEEDED.
      # Keep their search paths on the tray, not in every CLI child's environment.
      patchelf --add-rpath "${trayLibraryPath}" "$tray"
    fi
  '';

  doInstallCheck = isLinux;
  installCheckPhase = ''
    runHook preInstallCheck

    export HOME="$TMPDIR/aspire-home"
    export LD_LIBRARY_PATH="${linuxLibraryPath}"
    cmp "$TMPDIR/dcptun_c.original" "$out/lib/aspire-cli/bundle/dcp/dcptun_c"
    "$out/lib/aspire-cli/bundle/dcp/dcp" version

    # With no subcommand the managed host prints usage and exits 1. This also
    # exercises the self-contained runtime and its dynamically loaded libraries.
    status=0
    "$out/lib/aspire-cli/bundle/managed/aspire-managed" > managed-usage.log 2>&1 || status=$?
    cat managed-usage.log
    test "$status" -eq 1
    grep -F "Usage:" managed-usage.log

    # Older release bundles have no Linux tray. For bundles that do, preload its
    # dynamically imported libraries by soname using the executable's RUNPATH.
    # --help alone does not load GTK, and this check needs no graphical session.
    tray="$out/lib/aspire-cli/bundle/tray/aspire-tray"
    if test -f "$tray"; then
      interpreter="$(patchelf --print-interpreter "$tray")"
      env -u LD_LIBRARY_PATH "$interpreter" \
        --preload libgtk-layer-shell.so.0:libgtk-3.so.0:libgdk-3.so.0:libayatana-appindicator3.so.1:libatk-1.0.so.0:libcairo.so.2:libgio-2.0.so.0:libglib-2.0.so.0:libgobject-2.0.so.0:libpango-1.0.so.0:libpangocairo-1.0.so.0:libX11.so.6 \
        "$tray" --help > tray-usage.log 2> tray-loader.log
      cat tray-usage.log tray-loader.log
      # glibc can report an unresolved preload but still exit successfully.
      test ! -s tray-loader.log
    fi

    runHook postInstallCheck
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
