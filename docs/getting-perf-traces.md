# Wall-clock time investigations

Aspire startup profiling is collected with the CLI self-profile capture flow. The hidden `--capture-profile` option starts a private dashboard collector, enables profiling-only OpenTelemetry spans for the CLI and AppHost, exports a dashboard trace archive, and exits with the wrapped command's exit code.

## Collection with Aspire CLI

Capture startup for an AppHost and exit automatically after startup:

```sh
aspire run \
  --project path/to/AppHost.csproj \
  --capture-profile \
  --capture-profile-output artifacts/tmp/startup-profile/profile.zip \
  --non-interactive
```

From a repository checkout, use the built CLI directly:

```sh
./dotnet.sh exec artifacts/bin/Aspire.Cli/Debug/net10.0/aspire.dll run \
  --project tests/TestingAppHost1/TestingAppHost1.AppHost/TestingAppHost1.AppHost.csproj \
  --capture-profile \
  --capture-profile-output artifacts/tmp/startup-profile/profile.zip \
  --non-interactive
```

The export zip contains `traces/profile.json`, which can be inspected with tools such as `jq`:

```sh
tmpdir="$(mktemp -d)"
unzip -q artifacts/tmp/startup-profile/profile.zip -d "$tmpdir"
jq -r '.resourceSpans[]?.scopeSpans[]?.scope.name' "$tmpdir/traces/profile.json" | sort | uniq -c
jq -r '.resourceSpans[]?.scopeSpans[]?.spans[]?.name' "$tmpdir/traces/profile.json" | sort | uniq -c
```

Expected captures include `Aspire.Cli.Profiling` spans for CLI work and `Aspire.Hosting.Profiling` spans for AppHost orchestration, resource startup, backchannel startup, dashboard readiness, and outbound DCP/Kubernetes calls.
