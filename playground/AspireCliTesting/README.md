# Aspire CLI testing playground

This file-based AppHost is the shared, in-repository exerciser for `aspire test`.
It uses repository hosting projects, not released packages or a private package
hive. No containers or platform-specific test frameworks are required for the
foundation scenario.

## Run from the repository root

Set up the repository SDK with `./restore.sh` if it has not already been restored.
Use that SDK for both the CLI and its child processes:

```bash
export DOTNET_ROOT="$PWD/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"

dotnet build src/Aspire.Dashboard/Aspire.Dashboard.csproj
export ASPIRE_DASHBOARD_PATH="$PWD/artifacts/bin/Aspire.Dashboard/Debug/net11.0/Aspire.Dashboard.dll"

dotnet run --project src/Aspire.Cli/Aspire.Cli.csproj --no-launch-profile -- \
  test --apphost playground/AspireCliTesting/apphost.cs --isolated
```

The CLI builds the AppHost from the current sources. The `Aspire.Hosting.AppHost@*`
directive makes the file discoverable; `Directory.Build.targets` removes that
package reference and uses `AspireProjectOrPackageReference` to resolve the local
AppHost and .NET hosting projects. It does not restore the wildcard package.

The explicit dashboard path keeps this playground on the repository build.
The AppHost still opts into the CLI bundle; without an override, the CLI can
inject its bundled dashboard path ahead of the AppHost's local assembly metadata.
Rebuild the dashboard and CLI after updating the branch; avoid `--no-build` when
refreshing the AppHost.

The modern dashboard is the default in current source, not a separate preview
binary or an `aspire test` switch. `Dashboard:UI:DisableModernDashboard=true`
(environment alias `DASHBOARD__UI__DISABLEMODERNDASHBOARD`) forces the previous
experience. A saved `Aspire_Dashboard_Experience=legacy` browser cookie can also
select it. If the current binary still opens the previous experience, check that
configuration and select **New dashboard** under **Settings > Dashboard
experience**, or clear that experience cookie. Updating Git alone does not change
an already running dashboard or its saved browser preference.

For a machine-readable summary, add `--format json`. For dashboard inspection
after collection, add `--keep-alive` and stop with Ctrl+C when finished:

```bash
dotnet run --project src/Aspire.Cli/Aspire.Cli.csproj --no-launch-profile -- \
  test --apphost playground/AspireCliTesting/apphost.cs --isolated --keep-alive
```

CI can bound the entire command, including build, startup and collectors:

```bash
dotnet run --project src/Aspire.Cli/Aspire.Cli.csproj --no-launch-profile -- \
  test --apphost playground/AspireCliTesting/apphost.cs --isolated \
  --timeout-seconds 120 --format json --non-interactive
```

The deadline returns exit code `17` and retains a partial summary if testing is
unfinished. There is no whole-command limit unless the switch is supplied.
Timeout starts when the test command executes, not during compilation of the CLI
by the outer `dotnet run`. An additional bounded five-second cleanup window
allows the AppHost and its child processes to shut down.
With `--keep-alive`, the same deadline also ends inspection. Already completed
results remain completed; `summary.json` records the timeout exit code and
diagnostic. JSON stdout is still emitted only once, at testing completion.

To exercise ordinary development lifetime instead:

```bash
dotnet run --project src/Aspire.Cli/Aspire.Cli.csproj --no-launch-profile -- \
  run --apphost playground/AspireCliTesting/apphost.cs --isolated
```

The service remains running after the probe finishes under `aspire run`. Testing
output directories and collectors are only activated by `aspire test`.

## Shared scenario and extension points

`apphost.cs` defines `service`, a small HTTP service with `/health` and a `/`
response of `Aspire CLI testing`. `serviceEndpoint` is its dynamically allocated
HTTP endpoint; no scenario should hard-code a port.

`foundation-tests/tests.cs` probes the service and writes `probe.txt` into its
resource-specific testing output directory. Its `WithTesting` collector starts
alongside execution, awaits resource completion, checks the artifact, and
publishes the `shared-service-probe` result. The console/JSON summary includes
both resource completion and that actual published result. On Unix the probe
creates its artifact with mode `0600` inside the run's private resource directory.

Subsequent framework layers should append their resource declarations to the
same `apphost.cs`, before `builder.Build().Run()`, and put their fixtures in
separate subdirectories. Reuse `service` for `.WaitFor(service)` and
`serviceEndpoint` for endpoint injection. Add each required hosting package to
`AspireProjectOrPackageReference` in `Directory.Build.targets` so it resolves to
the current repository source. Do not add released testing packages or
machine-specific paths. Keep experimental API suppressions scoped to this
playground; the foundation collector APIs use `ASPIRETESTING001`.
