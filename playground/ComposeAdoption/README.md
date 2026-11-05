# Brownfield Compose adoption experiment

This is a **run-mode proof of concept**, not a complete Compose implementation.
It explores [discussion #18741](https://github.com/microsoft/aspire/discussions/18741)
and the runtime-import direction of [closed, unmerged PR #12384](https://github.com/microsoft/aspire/pull/12384).
The Compose file remains the infrastructure source of truth. No generated C#
or second `compose up` supervisor runs alongside DCP.

## Reproduce the complete journey

Requirements: Linux/macOS, Bash, curl, a running
Docker engine, Docker Compose v2 or later, and the repository's .NET SDK.
The verified environment used Linux x64, Docker 29.1.3, Compose 5.6.0, DCP 0.27.0,
and the SDK pinned by `global.json`. Redis is pulled from Docker Hub and the
.NET 10 ASP.NET runtime image from Microsoft's container registry.

Start from a fresh clone using GitHub CLI (or fetch the PR branch with git):

```bash
gh repo clone microsoft/aspire
cd aspire
gh pr checkout 21128
```

From the repository root:

```bash
# Installs the pinned SDK and restores the integration using repository feeds.
./restore.sh /p:Projects="$PWD/src/Aspire.Hosting.Docker/Aspire.Hosting.Docker.csproj"

# Executes original Compose, imported Aspire containers, then local replacement.
bash playground/ComposeAdoption/verify.sh
```

If the vulnerability-data endpoint is unreachable in your environment, the restore
can be run with `NuGetAudit=false` and `/p:NuGetAudit=false`; this is an environment
workaround, not a change to repository feed or security settings. `run.sh` disables
the network-dependent audit for its local sample build; it does not disable analyzers.

The script checks a real Redis-backed request through all three configurations,
including gateway `/health` forwarding an API Redis `PING` probe without
incrementing the counter; a successful response must be `{"redis":"ready"}`.
This is an application-level acceptance probe, not yet DCP Compose healthcheck
or `service_healthy` integration.
It also checks
dashboard HTTP availability, absence of the replaced API container, and cleanup.
It publishes the .NET API and gateway into ignored
`artifacts/compose-adoption/{api,gateway}` directories, then builds the AppHost
and dashboard synchronously before starting its readiness
timer, so a cold compilation is not mistaken for an unresponsive application.
The Compose file bind-mounts these published assemblies into the runtime image;
it does not use an unimplemented Compose `build` field. `run.sh --build-only`
prepares these artifacts without launching; `run.sh --no-build` runs the
previously built assembly. Both derive paths from the checkout, not a developer's
machine-specific output directory.
For an explicitly supplied DCP tools directory, set `ASPIRE_DCP_PATH` to its
absolute path. The scripts reject a missing/non-executable `dcp` rather than
falling back to packaged tools, and the launcher passes that binary through
`DcpPublisher:CliPath`. Verification prints the Aspire commit, worktree changes,
and the selected binary's `version` output. A custom controller build must include
its complete tools layout (including its matching tunnel client), not just a
copied `dcp` executable. This launcher support is not first-class Compose
integration; the sample at this revision still uses the native-resource importer.
`bootstrap-dcp.sh <reviewed-full-public-microsoft-dcp-commit>` obtains an exact
public source archive without Git credentials and runs the upstream codegen/build
inside a new ignored `artifacts/compose-dcp` directory. It requires Go (including
the toolchain specified by that source), Make, curl, tar and unzip; network access
to public Go modules and upstream build-tool downloads is also required. It does
not modify another checkout or install global tools. The retained directory
contains source, build log, source commit and the binary's version report; the
script prints the `ASPIRE_DCP_PATH` export. Use only a reviewed commit. An arbitrary
baseline build does not add Compose support: the agreed controller revision must
be pinned here before claiming first-class integration or cross-repository proof.
It stops its own AppHosts and Compose project on exit. Keep ports **18080** (gateway),
**18888** (dashboard) and **18889** (resource service) free. Do not run another copy
of this sample concurrently. Logs are written to ignored `imported.log` and
`replacement.log` in this directory.

To explore interactively:

```bash
# Original stack, without Aspire:
bash playground/ComposeAdoption/run.sh --build-only
docker compose -f playground/ComposeAdoption/compose.yaml up -d --wait
curl -fsS http://localhost:18080/
docker compose -f playground/ComposeAdoption/compose.yaml down

# Native Aspire containers; stop with Ctrl+C before continuing:
bash playground/ComposeAdoption/run.sh

# Same Compose file, replacing only the API with local source:
COMPOSE_REPLACE_API=true bash playground/ComposeAdoption/run.sh
```

Open **http://localhost:18888** for resources, dependencies, console logs and
stop/start commands; **http://localhost:18080/** is the unchanged caller URL.
These are local development endpoints, not remotely hosted demo URLs. The helper
deliberately uses anonymous HTTP dashboard/resource endpoints bound to loopback.
Do not expose or port-forward them to untrusted networks. Normal application
authentication/TLS settings should be used outside this local experiment.

Expected responses (whitespace varies):

```json
{"implementation":"compose-dotnet","visits":1}
{"implementation":"local-dotnet","visits":1}
{"implementation":"local-dotnet","visits":2}
```

The Redis counter demonstrates the real dependency, not a canned response.
Both containerized services use .NET; verification is Bash/curl only. The API
assembly is the same in container and local-source modes. Its container command
selects the `compose-dotnet` response marker; the local project defaults to
`local-dotnet`. Replacement deliberately does not inherit the container command.
Redis is ephemeral here: the original stack is stopped before Aspire starts,
and the counter resets between AppHost runs. This does **not** demonstrate adoption
of existing running containers or preservation/migration of database data.

## Application model and replacement

```csharp
var compose = await builder.AddDockerComposeFileAsync("legacy", "../compose.yaml",
    options =>
    {
        var api = builder.AddProject<Projects.ComposeAdoption_Api>(
                "local-api", launchProfileName: null)
            .WithHttpEndpoint(name: "port8080", env: "ASPNETCORE_HTTP_PORTS");
        options.ReplaceService("api", api);
    });

var gateway = compose.GetService("gateway"); // Native resource, not an opaque process.
```

Imported resource names are `<prefix>-<service>`. Every declared target port gets
an Aspire endpoint `port<target>`, including `expose`-only ports, so local code can
reach a dependency through DCP's allocated host proxy. Published ports are retained;
unpublished ports are dynamically allocated. For dashboard display, target ports
80/8080 use HTTP, 443 uses HTTPS and others use TCP. These are display conventions,
not protocol detection. Aspire's host proxies bind to localhost rather than
Compose's default all-interface host publishing; this development safety change
is intentional.

Whole absolute environment URLs such as `API_URL=http://api:8080` and
`REDIS_URL=redis://redis:6379` become `ReferenceExpression` values using endpoint
host/port properties. The original scheme, path, query and fragment are retained
without adding a trailing slash. Native Aspire network-aware resolution chooses
container addresses, host proxies or the container-to-host tunnel as appropriate.
No literal `localhost` or Docker host bridge address is hardcoded into imported
configuration. References and waits appear in the dashboard graph.

The replacement must already belong to the same AppHost and expose
`port<target>` for each declared original port. It receives the original environment
and required `service_started` dependency waits. Its image, command, mounts and
published bindings are deliberately replaced by its local definition. This is
explicit substitution, not a container with its image secretly removed.

## Bounded supported subset and diagnostics

Docker Compose performs YAML parsing, interpolation, `.env`/`env_file` evaluation,
path resolution and normalization via `docker compose --file ... config --format json`.
The AppHost does not duplicate Compose's YAML/interpolation parser. Resolved
environment values are ordinary environment values, **not** Aspire parameter
resources: do not assume secret classification, prompting or redaction is inferred.
Only import trusted files. Configuration runs with the AppHost's environment and
filesystem permissions. Normalized JSON is held in memory, not written to disk or
included in failure messages.

Supported normalized fields:

- `image` (including tags/digests), nonempty `command` argument arrays, string-valued
  `environment`, and a null/default `entrypoint`.
- Single TCP `ports` bindings and TCP `expose`; ports 1-65535, no ranges.
- Existing absolute bind mounts, read-only/read-write, without propagation/SELinux
  or other mount options. Compose resolves relative source paths.
- Required `depends_on` with `service_started`, mapped to `WaitForStart` (not readiness).
- The implicit default network only. It becomes Aspire's managed network; original
  project/network/container identities are not adopted. Original service DNS aliases
  are retained for imported containers, and conflicting existing aliases are rejected.

Everything outside this subset fails before imported resources are added, with a
configuration path, for example:

```text
Cannot import Compose configuration at 'services.api.healthcheck': this Compose field is not supported by the import proof of concept.
Cannot import Compose configuration at 'services.api.depends_on.redis': only required service_started dependencies without restart propagation are supported.
```

Examples of intentionally rejected fields: `build`, healthchecks, named/anonymous
volumes, custom networks/IPAM, secrets/configs, profiles, deploy/replicas, restart
policies, entrypoint overrides, privileged settings, `network_mode`, custom named
ports/app_protocol, UDP, and non-loopback explicit host bindings. Missing variables
or other Compose warnings fail normalization rather than silently substituting a
different stack. Future Compose versions adding new normalized fields may require
an explicit importer update.

Lowercase service names and valid Aspire resource names are required. Dependency
cycles, unknown replacements/dependencies, missing replacement endpoints, resource/
alias collisions and service URLs without a declared target port are diagnosed.
Publishing is rejected. C# only; no polyglot exports, source generation or CLI migration
command is included in this experiment.

**Connectivity boundary:** only whole environment URL values are retargeted.
Embedded connection-string fragments, raw hostname/port variables, commands,
files mounted into containers, hardcoded application URLs and external callers
are not rewritten. Service DNS remains available for container-to-container callers,
but replacing such a target with a local process requires explicit application/
configuration changes. This POC does not claim transparent replacement of arbitrary
black-box service-discovery mechanisms.

## Why not a first-class Go DCP Compose resource yet?

**No DCP PR has been created.** Public DCP source was inspected read-only; this
implementation normalizes Compose into existing Aspire/DCP resources, rather than
adding a first-class Compose resource. No DCP changes or additional repository
credentials are needed for this bounded experiment. Research revision:
[`cd36bfe59ac6d6c2a79600292f0b90affa294f89`](https://github.com/microsoft/dcp/commit/cd36bfe59ac6d6c2a79600292f0b90affa294f89).

- [`api/v1/groupversion_info.go`](https://github.com/microsoft/dcp/blob/cd36bfe59ac6d6c2a79600292f0b90affa294f89/api/v1/groupversion_info.go)
  registers the existing persistent workload types, including `Container`,
  `Executable`, `Service`, `ContainerNetwork` and `ContainerNetworkTunnelProxy`.
  There is no Compose type in this registry.
- [`api/v1/container_network_tunnel_proxy_types.go`](https://github.com/microsoft/dcp/blob/cd36bfe59ac6d6c2a79600292f0b90affa294f89/api/v1/container_network_tunnel_proxy_types.go)
  describes the existing service-to-service tunnel configuration used for container
  callers of local source. Aspire already models this in
  `src/Aspire.Hosting/Dcp/Model/ContainerTunnel.cs`.
- [`internal/dcp/bootstrap/extensions.go`](https://github.com/microsoft/dcp/blob/cd36bfe59ac6d6c2a79600292f0b90affa294f89/internal/dcp/bootstrap/extensions.go)
  discovers controller/monitor extensions. That extensibility is not by itself a
  registered Compose CRD, a Compose reconciler, or a dashboard resource integration.

A native Go controller using compose-go could preserve more Compose semantics than
translation and is worth a separate experiment for large, heterogeneous stacks.
But an opaque controller that merely invokes `compose up` leaves two owners for
lifecycle, ports, dependencies and cleanup. To be genuinely first-class it needs
a Compose CRD with per-service desired/observed state, logs and commands, a mapping
to DCP child Container/Service resources, partial replacement/exclusion and
endpoint-reference injection, plus coordinated Aspire API/model/watch support.
The critical interface would include **service identity -> DCP child resource and
endpoint IDs**, and **overrides for excluded services + externally provided endpoint
bindings**, with ownership/cleanup rules. That is cross-repository work and not
required to prove the bounded journey here.

The chosen Aspire-layer implementation reuses native image handling, endpoint
allocation, process debugging, logging, waits and DCP tunnels now. Unlike the prior
PR's broad hand-written YAML parser, Compose itself handles configuration syntax;
unsupported execution semantics are rejected rather than approximately translated.
The cost is reduced Compose fidelity. This result favors a strict native-resource
adapter for incremental adoption first, without ruling out a richer DCP controller.
A smaller intermediate option is a DCP normalization command using compose-go,
as suggested in the earlier PR discussion, replacing the Docker Compose CLI
dependency without introducing a new scheduling resource.

## Next design questions

### What is incomplete for brownfield adoption?

The sample is an acceptance probe for one supported graph, not proof that an
arbitrary existing Compose stack can be adopted. In particular, it prepares .NET
assemblies before Compose starts and uses only ephemeral Redis and whole-URL
environment references. It does not exercise Compose builds, retained data or
arbitrary existing service-discovery conventions.

| Missing capability | Consequence for an existing stack |
| --- | --- |
| Build contexts, Dockerfiles and build arguments | A stack using `build` cannot be imported without separately producing images. |
| Healthchecks and `service_healthy` / completion conditions | `service_started` cannot preserve readiness or migration-job semantics. |
| Named volumes and explicit storage ownership | Existing database state cannot be adopted, and cleanup/migration ownership is undefined. |
| Custom networks, aliases across networks and profiles | Multiple-network isolation and optional services are not preserved. |
| Secrets/configs and explicit binding overrides | The importer cannot infer secret classification or retarget arbitrary connection-string fragments/files. |
| Migration/override tooling and dashboard provenance | There is no guided preflight/report, generated service handles, grouping, or source mapping back to Compose fields. |

The smallest coherent completion path is to choose a representative existing
stack, record its required semantics and unchanged request path, and add an
explicit import plan with per-service/endpoint overrides and source provenance.
Then implement that stack's build, health and storage-ownership requirements
with positive/negative tests and a state-preserving replacement scenario.
Unsupported semantics must continue to fail explicitly. Calling the present
fixture "complete Compose adoption" would skip those acceptance criteria.

Existing DCP resources can still support that next slice; a Go Compose controller
is not a prerequisite established by this experiment. If fidelity requires a
first-class controller, its interface and joint Aspire changes are described
above. That separate cross-repository scope must be agreed before implementation.

Import currently normalizes at AppHost startup, not build time; Docker Compose must
be on PATH and a changed file requires an AppHost restart. Typed service access,
source-generator overrides and a one-time migration command could layer over a
shared validated model later, but are not necessary for this runtime experiment.
Health probes, persistent-volume ownership, multiple isolated networks/imports,
explicit per-port schemes and cross-language replacement UX need design before
this can be production-quality. Dashboard grouping/source provenance would make
the Compose boundary clearer than the current resource-name prefix.

## Focused validation

```bash
.dotnet/dotnet test tests/Aspire.Hosting.Docker.Tests/Aspire.Hosting.Docker.Tests.csproj \
  -- --filter-class '*DockerComposeImportTests'
bash -n playground/ComposeAdoption/run.sh playground/ComposeAdoption/verify.sh
```

The tests cover native resources, replacement identity/dependencies, URL suffix
preservation, unsupported semantics and invalid-graph diagnostics. The shell
scenario is the end-to-end runtime check; model/build tests alone are insufficient.

### Observed results (2026-10-19)

Verified against Aspire base `5bcd4dfc95d5000b36a14c5680c4ec73c63e9986`:

| Check | Result |
| --- | --- |
| Sample, hosting integration and real dashboard build | Passed, 0 warnings / 0 errors |
| Focused importer tests | 29 passed, 0 failed/skipped |
| Original Compose .NET gateway -> .NET API -> Redis | `compose-dotnet`, visits 1 |
| Imported native DCP containers, same request | `compose-dotnet`, visits 1 |
| Gateway container -> local .NET API -> Redis | `local-dotnet`, visits 1 then 2 |
| Dashboard HTTP in imported/replaced modes | 200 |
| Earlier browser inspection, before the .NET-only fixture revision | Imported: three Running containers; replaced: gateway/Redis + Project `local-api`, Running |
| Earlier replaced gateway environment, observed allocation | `API_URL=http://aspire.dev.internal:40067` |
| API container after replacement | Absent; existing DCP tunnel proxy present |
| AppHost shutdown | Imported containers removed by DCP |
| Importer whitespace formatting and shell syntax | Passed |

Ports are dynamically allocated; `40067` is observed evidence, not a required port.
Startup retries are expected: `service_started` does not mean application readiness.
The end-to-end script completed successfully after the real responses and cleanup
checks, not merely after container creation. Full repository suites, Windows/macOS,
alternate Compose versions, IDE breakpoints and production publishing were not
validated.
