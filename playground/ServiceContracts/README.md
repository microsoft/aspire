# Producer-owned binding contracts: multi-repository experiment

This is a runnable design probe, **not a proposed general module framework or a
claim that Aspire cannot compose repositories today**. It adds an experimental
`AddServiceContract` hosting API and exercises it across two real Git repositories.
The producer owns its Dockerfile, source layout, HTTP binding, readiness probe, and
required environment variables. The consumer owns its AppHost, parameter values,
supported contract identity/major version, and choice of implementation.

## Prior art and the deliberately smaller question

* [Multi-repo discussion #1137](https://github.com/microsoft/aspire/discussions/1137)
  already describes sibling checkouts, container images, remote endpoints,
  producer packages, and the
  [Stack/NuGet switching pattern](https://github.com/microsoft/aspire/discussions/1137#discussioncomment-12913138).
  [PR #1071](https://github.com/microsoft/aspire/pull/1071) added path-based
  `AddProject`; this experiment uses that existing capability, not a replacement
  repository manager.
* [Enterprise constraints #10276](https://github.com/microsoft/aspire/discussions/10276)
  motivate a producer Dockerfile that works independently of the consumer
  AppHost and centrally controlled pipelines. Runtime configuration and secrets
  must still be supplied at runtime; putting secrets into a Dockerfile is not a
  solution to fixed pipeline templates.
* [White Label](https://github.com/bUKaneer/white-label.starthere) already explores
  independent source-control boundaries, packages, local registries, and
  container test hosts. This prototype does not recreate its scaffolding or
  registries.
* [Shirubasoft.Aspire.ModularAppHosts](https://github.com/Shirubasoft/aspire-modular-apphosts)
  is much closer prior art: its
  [module guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/modules.md)
  already documents producer-owned typed C# contracts, identity/version checks,
  local project/container switching, graph composition, and explicit repository
  initialization. Its
  [image workflow](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/module-images.md)
  also addresses publishing. Those are existing ideas, not novelty claims here.
* The current
  [2026-2027 roadmap #18581](https://github.com/microsoft/aspire/discussions/18581)
  explicitly plans one app across repositories and multiple AppHosts. The older
  [roadmap #10644](https://github.com/microsoft/aspire/discussions/10644) likewise
  lists multi-repo work. A
  [Windows 365 integration reference](https://github.com/microsoft/aspire/discussions/1137#discussioncomment-14997188)
  and the later sibling-AppHost startup script in the same discussion further
  illustrate current orchestration approaches. The linked Windows 365 article
  was not retrievable in the implementation environment; no implementation
  details from that article are claimed.

**The learning question:** can a consumer bind to a small *data-only* producer
artifact, without loading a producer assembly, importing its whole resource graph,
or knowing its internal project path, and still use normal Aspire discovery and
health-gated startup when switching between source and an image?

The JSON format is intentionally much less capable than the modular-AppHost POC.
It could eventually be a binding boundary for such modules; it is not a competing
graph DSL or a substitute for the roadmap's multi-AppHost coordination.

## What runs

```text
<workspace>/producer/.git
  aspire-service.json
  Dockerfile
  src/Catalog.Api/...
<workspace>/consumer/.git
  contracts/catalog.json        # producer release artifact, not producer internals
  AppHost/...
  Storefront/...

HTTP client -> storefront /shop -> catalog /products
```

The standalone .NET `Verify` runner materializes these as independently initialized and committed Git
repositories **outside the Aspire checkout**. They are not solution folders or
submodules. The only producer content shipped to the consumer is its contract
artifact; an image is the other release deliverable. The consumer has no
`ProjectReference` to producer code. It references this Aspire checkout solely
because the experimental hosting API has not been published.
These standalone fixtures are excluded from the repository's default playground
build glob; the runner supplies `AspireRepoRoot` and builds their independent
projects explicitly.

```csharp
var catalog = builder.AddServiceContract(
    "catalog", "../contracts/catalog.json", "contoso.catalog", 1,
    new Dictionary<string, IResourceBuilder<ParameterResource>>
    {
        ["region"] = builder.AddParameter("catalog-region"),
        ["api-key"] = builder.AddParameter("catalog-api-key", secret: true)
    },
    builder.Configuration["CatalogSource"]);

builder.AddProject("storefront", "../Storefront/Storefront.csproj", launchProfileName: null)
    .WithReference(catalog.GetEndpoint("http"))
    .WaitFor(catalog);
```

Without `CatalogSource`, the contract selects the versioned container image.
With `CatalogSource`, it reads `aspire-service.json` from the already-cloned
producer repository and uses that producer-owned project path. Paths supplied by
the consumer are relative to its AppHost, not the shell's current directory.
The source project path is relative to the producer root. There is no automatic
clone, fetch, branch selection, fallback to another mode, or nested host.
Source mode retains normal `AddProject` build/launch behavior; the runner also
builds the selected source explicitly before launching.

The result is the **actual** `ProjectResource` or `ContainerResource`, exposed as
`IResourceBuilder<IResourceWithEndpoints>`. Existing `GetEndpoint`, `WithReference`
and `WaitFor` handle discovery and readiness; no wrapper copies endpoints and no
new orchestration engine runs. A named endpoint is used because projects and
containers have different service-discovery marker interfaces.

## Contract and independent evolution

See [producer/aspire-service.json](producer/aspire-service.json) for the entire
format. All fields are required; unknown fields fail rather than being silently
ignored.

| Field | Ownership and behavior |
| --- | --- |
| `schemaVersion` | Interchange format version, currently exactly `1`. |
| `id`, `version` | Producer identity and three-part numeric service contract version. Consumer explicitly pins identity and supported major. No prerelease/range negotiation. |
| `image`, `imageTag` | Producer image/release selection; an explicit non-`latest` tag is required. |
| `project` | Producer-root-relative .NET project path, resolved only in source mode. Lexical traversal outside the root and missing projects fail. |
| `http` | One named `http` endpoint, container target port, port environment variable, and HTTP readiness path. Source uses a dynamically allocated port. |
| `inputs` | Required named bindings to consumer-owned `ParameterResource`s, mapped to producer environment variable names. No values/defaults/secrets in the artifact. |

The published artifact is checked even in source mode, and then the selected
checkout is checked against the same identity/major. The checkout's metadata
is authoritative for its own layout and binding details. The probe moves
`src/Catalog.Api` to `src/Catalog.Renamed`, updates only the producer contract to
`1.1.0`, and repeats the request without changing the consumer artifact or AppHost.
A checkout declaring `2.0.0` fails before resource registration.

This checks **declared binding compatibility**, not HTTP semantics or the
truthfulness of a version. A producer must bump its major for incompatible HTTP
changes. Adding a new required input is rejected until the consumer supplies it,
even if a producer incorrectly calls that change a minor release. Inputs must
match exactly, cannot share environment-variable destinations (including the
port variable), and must refer to parameters in the consumer's own model.
Parameter values stay deferred references so normal secret handling applies.

## Run the complete experiment

Prerequisites: Linux x64, Git, the .NET 10 SDK (including ASP.NET Core runtime)
on `PATH`, access to the
repository's approved package feeds and Microsoft's container registry, and an
already-running Docker daemon. The repository's `dotnet.sh` bootstraps its
separate pinned .NET SDK; do not edit `global.json`.

From this Aspire branch, with a **new, nonexistent workspace path**:

```bash
./dotnet.sh --info
docker info
./dotnet.sh run --project playground/ServiceContracts/Verify -- --workspace /tmp/aspire-contract-demo
```

The dependency-free .NET console runner uses the repository's pinned SDK and
runtime; no additional scripting runtime is required. It builds the real hosting library and dashboard dependency, both
applications, and the producer's standalone multi-stage Dockerfile. It uses DCP
`0.27.0` as pinned by this checkout (resolved from MSBuild, not a second version
constant). It never starts/stops the shared Docker daemon. It stops its own
AppHosts, retains logs and both repositories, and leaves tracked fixture files
unchanged after all probes succeed. Temporary contract edits and directory moves
are restored in `finally` blocks even if a probe fails. Each command and HTTP
probe has a deadline; AppHosts receive SIGTERM and a bounded graceful shutdown
before a process-tree kill fallback. The versioned local image is retained.
Because the repository SDK host may only contain .NET 11, the runner searches
`PATH` for a host containing both .NET 10 and ASP.NET Core 10, then supplies that
host/root to the application and its DCP-launched children. Builds still use
the repository-pinned SDK.

The consumer host is intentionally headless and enables normal resource-log
forwarding. Current core option validation still requires a real dashboard
artifact even when dashboard launch is disabled; the runner builds and supplies
that dependency, rather than supplying a dummy path. This exercises the hosting
library directly, not CLI setup or IDE/debugger integration.

Options:

```bash
# Source-only run does not need Docker.
./dotnet.sh run --project playground/ServiceContracts/Verify -- --workspace /tmp/contract-source --mode source
# Container-only run never builds producer source on the host.
./dotnet.sh run --project playground/ServiceContracts/Verify -- --workspace /tmp/contract-image --mode container
# Materialize independent repositories without building or running anything.
./dotnet.sh run --project playground/ServiceContracts/Verify -- --workspace /tmp/contract-manual --prepare-only
```

An unmodified producer can independently be built with
`docker build -t aspire-contract-catalog:1.0.0 /tmp/contract-manual/producer`.
No consumer checkout is in its build context. For actual teams, publish the
image and contract together through their existing artifact distribution
process; obtaining/reviewing that artifact is deliberately outside the loader.

After a complete run, interactive manual reproduction uses the retained
repositories and binaries:

```bash
export ASPIRE_CHECKOUT=/absolute/path/to/this/aspire
export CONTRACT_WORKSPACE=/tmp/aspire-contract-demo
export DCP_TOOLS="$HOME/.nuget/packages/microsoft.developercontrolplane.linux-amd64/0.27.0/tools"
cd "$CONTRACT_WORKSPACE/consumer/AppHost"
dotnet bin/Debug/net10.0/Consumer.AppHost.dll \
  --DcpPublisher:CliPath "$DCP_TOOLS/dcp" \
  --DcpPublisher:DashboardPath "$ASPIRE_CHECKOUT/artifacts/bin/Aspire.Dashboard/Debug/net11.0/Aspire.Dashboard.dll" \
  --StorefrontPort 18080 \
  --Parameters:catalog-region north \
  --Parameters:catalog-api-key fixture-key-not-a-secret
# In a second terminal:
curl --fail http://localhost:18080/shop
```

Restart the same command with
`--CatalogSource "$CONTRACT_WORKSPACE/producer"` for local-source mode. Remove
that argument for image mode. The key above is public fixture data; real secrets
should use normal AppHost configuration providers, not shell history or images.

## Verification and failure UX

The recorded run is in [verification.txt](verification.txt): 167 hosting tests
(including 34 contract cases and the existing image parser/archive suites) passed
on .NET 10, and all 14 runtime probes passed.
This is local runtime evidence, not a claim about CI or production deployment.

The runner requires exact response JSON in source mode, after the producer-only
refactor, and in container mode **while the producer checkout is renamed away**:

```json
{"service":"catalog","contractVersion":1,"region":"north","products":["tea","coffee"]}
```

It also checks incompatible source major, wrong artifact identity/schema,
missing/unexpected input bindings, malformed JSON, missing artifact/checkout,
missing runtime parameter values in both implementations, and a source service
with explicit Kestrel listeners in `appsettings.json`. The contract's port
variable is bound separately from endpoint environment shortcuts so native
allocated Kestrel URL overrides remain intact. Contract errors
are raised before adding service resources to the model. Missing runtime values
use Aspire's existing parameter error state and log forwarding; they do not
silently run with empty defaults. Authentication of the producer request uses
the separately bound fixture API key.

Normal resource logs show the consumer waiting for catalog health, catalog
`/health` returning HTTP 200, then the storefront process launching. The targeted
hosting tests additionally check endpoint/port equivalence, health and wait
annotations, preserved parameter object references, malformed contract shapes,
source path errors, relative path resolution, foreign-model bindings, and
Kestrel URL allocation. The recorded neighboring suites also include
`ProjectResourceTests`, `WithEnvironmentTests`, and `KestrelConfigTests`:

```bash
./dotnet.sh restore tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj --disable-parallel
./dotnet.sh test --project tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj \
  --no-restore -- --filter-class Aspire.Hosting.Tests.ServiceContractTests
```

On SDK-only development installations without a matching runtime, the repository
test host may need `DOTNET_ROLL_FORWARD=Major`. Run tests and the integration
runner **sequentially**: both build the same hosting output directories.
The runner disables package vulnerability auditing for its isolated experiment
restores because the audit endpoint was unavailable in the implementation
environment; it does not change repository feeds or claim an audit passed.

## Deliberate limits and implications

* One .NET service and one HTTP endpoint, not arbitrary resource graphs,
  dependency import, multiple producer AppHosts, non-.NET source runners, or
  shared infrastructure ownership. This is the binding edge, not the roadmap's
  entire multi-repo feature.
* Experimental C# API (`ASPIRESERVICECONTRACTS001`), no ATS/polyglot export yet.
  The data artifact is language-neutral; that does not make the source runner
  or AppHost API polyglot.
* Contract identity/major checks are not OpenAPI or behavioral compatibility
  proofs, runtime attestation, image signature verification, or package resolution.
  Explicit tags avoid `latest` but remain mutable; digest pinning and atomic
  artifact/image provenance need design before a production format.
* Contracts select executable code/images and must be reviewed as trusted
  execution inputs. Root containment is lexical, not a symlink sandbox.
  Environment-variable validation is not a security boundary against a malicious
  producer. No arbitrary executable commands are added to this format.
* The consumer has a prototype checkout/build dependency. No NuGet package,
  public fixture repositories, or registry image is published by this experiment;
  the checked-in runner makes the independent repositories locally.
* HTTP is local-development transport. The sample API-key check demonstrates
  binding, not production authentication. TLS, cloud identity, Key Vault,
  Terraform/Helm ownership, deployment and locked enterprise pipelines remain
  separate concerns; the producer Dockerfile does not embed secrets.
* No claim about debugger attach, hot reload, remote endpoints, transitive
  graph cycles, simultaneous independent DCP instances, rollout compatibility,
  or deployed readiness is made.

The useful next design boundary is separating *public bindings* from
*implementation materialization*. A producer can independently refactor its
source while preserving the binding edge, but consumers still need an explicit,
reviewable artifact upgrade policy. Typed module composition can offer richer
tooling; this experiment asks whether a smaller data artifact is valuable for
teams that do not want a producer package/assembly loaded into their AppHost.
