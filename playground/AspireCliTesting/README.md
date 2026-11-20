# Aspire CLI testing playground

This file-based AppHost is the shared, in-repository exerciser for `aspire test`.
It uses repository hosting projects, not released packages or a private package
hive. The foundation, MSTest and pytest scenarios share the same HTTP service
and require no containers. The MSTest fixture registers the executable
Microsoft.Testing.Platform runner and TRX extension explicitly.
The pytest fixture uses Python 3.10 or later and `uv` on PATH. `WithUv()` installs
its declared pytest dependency into a local virtual environment before execution;
no global pytest installation is needed.

## Run from the repository root

Set up the repository SDK with `./restore.sh` if it has not already been restored.
Use that SDK for both the CLI and its child processes. The preview command is
disabled by default; opt in for each testing invocation without changing saved
configuration:

```bash
export DOTNET_ROOT="$PWD/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"

features__testCommandEnabled=true \
  ./run-aspire.sh test --apphost playground/AspireCliTesting/apphost.cs --isolated
```

The CLI builds the AppHost from the current sources. The `Aspire.Hosting.AppHost@*`
directive makes the file discoverable; `Directory.Build.targets` removes that
package reference and uses `AspireProjectOrPackageReference` to resolve the local
AppHost, .NET and Python hosting projects. It does not restore the wildcard package.

Like the other playgrounds, this AppHost adds the repository dashboard with
`AddDotnetProject` using the reserved `aspire-dashboard` resource name. Hosting
configures that resource instead of launching the CLI's bundled dashboard.
The source dashboard is built and launched without a manual environment export.
Avoid `--no-build` when refreshing the AppHost.

To test an explicitly selected dashboard binary instead, set
`DcpPublisher__DashboardPath` for that invocation; the AppHost preserves that
configuration and omits the source resource. To use the ordinary automatic
dashboard/bundle selection, build with `SkipDashboardProjectReference=true`
(for example, `features__testCommandEnabled=true SkipDashboardProjectReference=true ./run-aspire.sh test --apphost
playground/AspireCliTesting/apphost.cs --isolated`). This existing playground
opt-out also honors `ASPIRE_DASHBOARD_PATH` overrides, which otherwise do not
replace an explicitly modeled source dashboard.

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
features__testCommandEnabled=true \
dotnet run --project src/Aspire.Cli/Aspire.Cli.csproj --no-launch-profile -- \
  test --apphost playground/AspireCliTesting/apphost.cs --isolated --keep-alive
```

CI can bound the entire command, including build, startup and collectors:

```bash
features__testCommandEnabled=true \
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

`foundation-tests/tests.cs` probes the service and writes `probe.txt` into a
private temporary runner-report directory unique to the AppHost invocation.
Its `WithTesting(async context => { ... })` collector starts
alongside execution, awaits resource completion, checks the artifact, and
stages it into `context.OutputPath` before publishing the `shared-service-probe`
result. The runner report directory is removed at AppHost shutdown.
The callback runs at runtime, never during model construction; configure runner
arguments/environment independently rather than depending on its synchronous prefix.
It receives a `ResourceTestingContext`: `context.OutputPath` is the resource's
artifact directory, while `context.TestingContext.OutputPath` identifies the
containing invocation through its `TestingContext`.
Ordinary runs still execute the probe but do not invoke the collector.
Text output distinguishes `Resource`
state transitions from `Test` results, shows exit codes only for terminal states,
and reports each resource's passed/failed/other counts after its collectors finish.
`Failed` counts published `Failed`, `Inconclusive`, `Error`, `TimedOut`, and
`Cancelled` outcomes, all of which fail testing even if the process exits zero.
Individual result lines and JSON retain each specific outcome. Published test
timeouts/cancellations return normal test failure (`23`), not CLI deadline (`17`)
or user cancellation (`130`), and do not stop other resources.
`Other` counts published skipped results. Interrupted collectors instead show
the results published so far. Resource-only scenarios do not invent test counts.
The final output retains the run verdict, published totals and artifact link
without repeating a resource table. JSON retains both resource completion and
actual published results. On Unix the probe creates its artifact with mode `0600`;
collection preserves that mode when staging it into the run's private resource directory.

Artifacts are laid out directly as
`~/.aspire/testruns/<run-id>/foundation-tests/probe.txt`, alongside
`<run-id>/summary.json`. There is no `resources` intermediary or generated
resource-directory suffix. The directory uses the logical resource name;
replicas share it, so replica-aware runners must avoid overwriting each other's
artifacts. Repeated `WithTesting` calls keep that same directory while replacing
collectors. Invalid portable directory names and pre-existing output paths fail
explicitly rather than being sanitized or reused.

The test-run ID is currently independent of the dashboard's timestamp run ID
under `~/.aspire/dashboard/runs`. The dashboard generates its ID after AppHost
model construction, whereas the CLI creates the test-run directory before
launching the AppHost. Shared run-ID plumbing is deferred.

`mstest-tests/Playground.MSTest.csproj` is an executable MSTest 4.4.0 project with
Microsoft.Testing.Extensions.TrxReport 2.4.0. Its local build files isolate it from
the repository's xUnit runner setup, matching a consumer MSTest project.
`AddMsTest("mstest-tests", ...)` waits for the same `service` and receives
`serviceEndpoint` through `MSTEST_SERVICE_URL`. Tests check `/`, `/health`, an
opt-in failure, and a skipped example.

`pytest-tests/pyproject.toml` declares pytest 9.0.2. `AddPytest("pytest-tests", ...)`
waits for the same service, receives `serviceEndpoint` through `PYTEST_SERVICE_URL`,
and launches the virtual environment's pytest executable directly. Its tests
check `/`, `/health`, an independent opt-in failure, and a skipped example.

The default combined summary contains **7 passed, 0 failed, 2 skipped**:
one foundation result plus three passed and one skipped result from each framework.
Each collection has its own output directory. The foundation writes `probe.txt`;
MSTest and pytest write into fresh temporary runner directories configured before
launch. Their runtime `WithTesting` callbacks wait for completion and stage
`results.trx` (and its report tree) into `<run>/mstest-tests`, and the built-in
JUnit report `pytest.xml` into `<run>/pytest-tests`. The AppHost file-system
service cleans up runner temporary files on disposal.
JSON exposes `outputPath`, `summaryPath`, and each `collections[].outputPath`.
Reports and `summary.json` remain after teardown.
Staged files use the copy writer's permissions; no per-file `0600` guarantee is made.

Exercise the controlled MSTest failure without changing the foundation scenario:

```bash
features__testCommandEnabled=true MSTEST_PLAYGROUND_FAIL=true \
dotnet run --project src/Aspire.Cli/Aspire.Cli.csproj --no-launch-profile -- \
  test --apphost playground/AspireCliTesting/apphost.cs --isolated \
  --timeout-seconds 120 --format json --non-interactive
```

This returns CLI exit code `23` with **6 passed, 1 failed, 2 skipped**. The MSTest
process exits nonzero, but its collector still harvests the finalized TRX report
and publishes all results. Foundation and pytest results remain passing.

Exercise the independent pytest failure without changing MSTest:

```bash
features__testCommandEnabled=true PYTEST_PLAYGROUND_FAIL=true \
dotnet run --project src/Aspire.Cli/Aspire.Cli.csproj --no-launch-profile -- \
  test --apphost playground/AspireCliTesting/apphost.cs --isolated \
  --timeout-seconds 120 --format json --non-interactive
```

This also returns exit code `23` with **6 passed, 1 failed, 2 skipped**. The pytest
collector harvests finalized JUnit even after its process exits nonzero.
There is no `--output` option; use the paths assigned
by the CLI rather than passing reporting flags to the runner.

Subsequent framework layers should append their resource declarations to the
same `apphost.cs`, before `builder.Build().Run()`, and put their fixtures in
separate subdirectories. Reuse `service` for `.WaitFor(service)` and
`serviceEndpoint` for endpoint injection. Add each required hosting package to
`AspireProjectOrPackageReference` in `Directory.Build.targets` so it resolves to
the current repository source. Do not add released Aspire hosting packages or
machine-specific paths. Keep experimental API suppressions scoped to this
playground; the foundation collector APIs use `ASPIRETESTING001`.
