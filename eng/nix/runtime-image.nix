{
  aspireCli,
  uid,
  gid,
  workspace,
  dockerTools,
  buildEnv,
  runCommand,
  bashInteractive,
  cacert,
  coreutils,
  curl,
  diffutils,
  docker-client,
  dotnetCorePackages,
  findutils,
  gnugrep,
  jq,
  patchelf,
}:
let
  # This control has the original interpreter from the embedded release payload.
  # It must not become runnable just because the test image contains a .NET SDK.
  unpatchedDcp = runCommand "aspire-unpatched-dcp" { dontFixup = true; } ''
    export HOME="$TMPDIR/home"
    export ASPIRE_CLI_TELEMETRY_OPTOUT=true
    export features__updateNotificationsEnabled=false
    mkdir -p "$HOME" "$out/bin"
    ASPIRE_LAYOUT_PATH="$TMPDIR/payload" \
      ${aspireCli}/bin/aspire setup --install-path "$TMPDIR/payload" --non-interactive
    cp "$TMPDIR/payload/bundle/dcp/dcp" "$out/bin/aspire-unpatched-dcp"
  '';
  tools = buildEnv {
    name = "aspire-nix-runtime-tools";
    paths = [
      aspireCli
      unpatchedDcp
      bashInteractive
      coreutils
      curl
      diffutils
      docker-client
      dotnetCorePackages.sdk_10_0
      findutils
      gnugrep
      jq
      patchelf
    ];
    pathsToLink = [ "/bin" ];
  };
in
dockerTools.buildLayeredImage {
  name = "aspire-nix-runtime-smoke";
  tag = "test";
  contents = [ tools ];
  extraCommands = ''
    mkdir -p etc tmp
    chmod 1777 tmp
    printf '%s\n' 'root:x:0:0:root:/root:/bin/bash' \
      'aspire:x:${toString uid}:${toString gid}:Aspire:${workspace}/home:/bin/bash' > etc/passwd
    printf '%s\n' 'root:x:0:' 'aspire:x:${toString gid}:' > etc/group
  '';
  config = {
    User = "${toString uid}:${toString gid}";
    WorkingDir = workspace;
    Cmd = [ "/bin/bash" ];
    Env = [
      "PATH=/bin"
      "HOME=${workspace}/home"
      "TMPDIR=${workspace}/tmp"
      "ASPIRE_HOME=${workspace}/state"
      "NIX_ASPIRE_PACKAGE=${aspireCli}"
      "SSL_CERT_FILE=${cacert}/etc/ssl/certs/ca-bundle.crt"
      "DOTNET_ROOT=${dotnetCorePackages.sdk_10_0}/share/dotnet"
      "DOTNET_CLI_TELEMETRY_OPTOUT=1"
      "ASPIRE_CLI_TELEMETRY_OPTOUT=true"
      # The disposable smoke fixture uses HTTP rather than testing certificate trust.
      "ASPIRE_ALLOW_UNSECURED_TRANSPORT=true"
      "ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true"
      "features__updateNotificationsEnabled=false"
      "ASPIRE_CLI_NUGET_SERVICE_INDEX=https://packagefeedproxy.microsoft.io/nuget/v3/index.json"
    ];
  };
}
