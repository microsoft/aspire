# Native Docker Compose deployment integration

An experimental, bounded existing-target deployer implemented outside the native model kernel.
It registers native compute-environment graph resources only while publishing/inspecting and
leaves local container/executable/lifecycle behavior unchanged.
The normal CLI SDK overlay packages this module alongside generated `native-builder.mjs`.
This playground is not a production deployment recommendation.

An independent [C# implementation and side-by-side comparison](../../CSharp/DockerCompose/README.md)
uses the same native publish model, native pipeline scheduler and sample web application.

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();
const compose = builder.addDockerComposeEnvironment('compose')
    .withDashboard({ enabled: false });
const api = builder.addContainer('api', 'example/api:latest')
    .withHttpEndpoint({ name: 'http', targetPort: 8080 })
    .withExternalHttpEndpoints()
    .withComputeEnvironment(compose);
api.publishAsDockerComposeService({
    image: 'example/api:latest',
    build: { context: '.', dockerfile: 'Dockerfile' },
    healthcheck: { test: ['CMD', 'node', 'healthcheck.mjs'], interval: '5s' },
});
api.publishAsDockerComposeService(async (composeService, service) => {
    service.restart.set('unless-stopped');
    console.log(await composeService.parent().name());
});
compose.configureComposeFile(async file => {
    const service = await file.services.get('api');
    await service.pullPolicy.set('missing');
});

await builder.build().run();
```

## Descriptor packaging and sample

The [protocol-v2 descriptor](aspire.apphost-server.json) identifies
`example.ts.docker-compose`, contributes `docker-compose.mts` as an SDK file/export,
and uses [empty provider metadata](provider-contract.json): this target adds no
native RPC capabilities or runtime provider process. The CLI copies its source
beside `native-builder.mts` and re-exports its APIs from generated `aspire.mjs`.
The module statically imports the native builder's generic resource classes and the
pipeline's well-known step names. Neither generic module imports Compose, so this
does not introduce a runtime cycle.

See the [native provider packaging/opt-in instructions](../../README.md#product-opt-in)
and the [Redis + web sample](AppHost/apphost.mts). The source directory deliberately
does not contain an independently installable npm package manifest. Prepare a qualified
package dependency and target-project `"features": { "appHostServerV2": true }`
before running the AppHost through the matching v2 CLI/bundle. The reproducible harness
below prepares this artifact-only workspace automatically; do not manually inject SDK files.

The sample models a prebuilt Redis container with password authentication, AOF persistence
on a named volume, and a Compose healthcheck. An executable web app supplies an explicit
deployment image and [Dockerfile](AppHost/web/Dockerfile), receives the symbolic Redis URI,
and waits for Redis health. Its [HTTP server](AppHost/web/server.mts) authenticates to Redis
and increments a retained counter; `/health` uses Redis `PING` without incrementing it.
The web's `.withExternalHttpEndpoints()` marks its HTTP endpoint for automatic Compose
publication, on loopback with a dynamically assigned host port. Redis remains internal.

Both build paths are application-relative, **not** Dockerfile-relative-to-context:
for the sample's `context: './web'`, use `dockerfile: './web/Dockerfile'`.
`dockerfile: 'Dockerfile'` instead means the AppHost directory's Dockerfile and is
rejected when it is outside the `./web` context.

Once the sample workspace is prepared, from its AppHost directory:

```bash
# Set ASPIRE_PARAMETER_CACHE_PASSWORD explicitly for local run/start or deploy.
aspire run
# From another terminal, stop the same sample before restarting it detached.
aspire stop --apphost ./apphost.mts
aspire start --format Json
aspire stop --apphost ./apphost.mts
aspire publish --environment ComposeSmoke --output-path ./aspire-output
aspire deploy --environment ComposeSmoke --output-path ./aspire-output
node --input-type=module -e \
  'import { readFile } from "node:fs/promises"; const s = JSON.parse(await readFile("aspire-output/deployment-state.json", "utf8")); console.log(s.services.flatMap(x => x.endpoints.map(e => e.url)).join("\n"));'
```

Local `run` and detached `start` use native DCP orchestration, not Docker Compose.
The web executable listens on the DCP-assigned `PORT`; published containers use
the fixed target port 8080 and a dynamically assigned host port.
The sample registers deployment-policy pipeline steps only in publish mode, where
their Compose dependencies exist. A host stop request gives a running guest a
bounded interval to retire workloads and providers before its transport closes.

## Programmatic smoke-test contract

```typescript
const target = new DockerComposeEnvironment('compose', {
    runner, // Optional DockerCommandRunner; defaults to runDockerCommand.
    waitTimeoutSeconds: 120,
    parameterEnvironment: { ASPIRE_PARAMETER_CACHE_PASSWORD: suppliedSecret },
});
await target.publish(context);
await target.deploy(context);
```

`context` is the generated SDK's `PublishingContext`:
`{ model, outputPath, applicationDirectory, environmentName, signal }`.
`model` is the native `PublishModel`, not an integration-owned local resource graph.

```typescript
declare function generateDockerCompose(
    context: Pick<PublishingContext, 'model' | 'applicationDirectory' | 'environmentName'>,
    name: string,
): DockerComposePlan;
// Returns { compose, inputs, services }:
// compose: { name, services: Record<string, ComposeService>, volumes? }
// inputs: { schemaVersion: 1, projectName, parameters: [{ name, environmentVariable }],
//           variables: Record<string, PublishExpression> }
// services: [{ resource, service, healthcheck,
//              ports: [{ endpoint, target, protocol, scheme, hostIp, published? }] }]

declare function resolveDockerComposeInputs(
    plan: DockerComposePlan,
    model: PublishModel,
    environment: Readonly<Record<string, string | undefined>>,
): Record<string, string>;
// Returns only required parameter variables and computed ASPIRE_COMPOSE_EXPR_* values.

type DockerCommandRunner = (
    executable: string,
    arguments_: readonly string[],
    options: {
        cwd: string;
        env: Readonly<Record<string, string | undefined>>;
        signal: AbortSignal;
        timeoutMs: number;
    },
) => Promise<{ exitCode: number; stdout: string; stderr: string }>;
```

Use `dockerComposeProjectName(applicationDirectory, environmentName, targetName)`,
or read `plan.compose.name` / `plan.inputs.projectName`, instead of reimplementing
identity. The name is `aspire-<application-basename-slug:16>-<target-slug:12>-<environment-slug:12>-<16-hex-suffix>`.
The suffix is FNV-1a-64 over UTF-8 `JSON.stringify([resolve(applicationDirectory),
environmentName, targetName], null, 2) + '\n'`. It is a non-security ownership
identity, and services/volumes are scoped to this project.

`publishAsDockerComposeService` accepts `image`, `build: { context, dockerfile }`,
`healthcheck: { test: string[], interval?, timeout?, retries? }`,
`ports: [{ endpoint: 'http', published?, hostIp? }]`, `exclude`, and
`replaceRunLifecycle`. It returns the same resource builder.
By default, Compose derives published ports from the native endpoints marked external
with `.withExternalHttpEndpoints()`. An explicitly authored positive host port is honored;
otherwise Compose chooses a random host port. Bindings default to loopback.
The optional `ports` array replaces that native selection entirely: use it to select
specific endpoint names or override `published`/`hostIp`, or use `ports: []` to disable
all host publication for the resource. No callback-specific port API is needed.
`builder.addDockerComposeEnvironment(name)` registers and returns a fluent
`ResourceBuilder<DockerComposeEnvironmentResource>`. The standalone function with the
same name remains a convenience wrapper. This is an ordinary native graph resource
with type `docker-compose/environment`, not a singleton deployment-target slot.

## Fluent authoring and generated-model callbacks

Methods and property accessors are await-compatible. `withProperties(async environment => ...)`
exposes `name()`, `defaultNetworkName.get()/set()`, and `dashboardEnabled.get()/set()`.
A non-null default network name selects an **existing external network**; create it separately
before deployment.

`configureComposeFile(async file => ...)` runs after service customization. It supports
`name.get()/set()`, `services.get(name)`, `addService`, `addNetwork`, `addVolume`,
`addConfig`, and external-only `addSecret`. Network definitions use typed JSON records.
Volume configuration can set labels through `configure: async volume => volume.labels.set(...)`.
Services expose `containerName`, `restart`, and `pullPolicy` accessors, collection counts
for configs/secrets/ulimits, and `addVolume(source, target, { isReadOnly })`.
`image`, `build`, and `healthcheck` also have typed `get()/set()` accessors. For example:

```typescript
web.publishAsDockerComposeService(async (_, service) => {
    service.image.set('example/web:1');
    service.build.set({ context: './web', dockerfile: './web/Dockerfile' });
    service.healthcheck.set({ test: ['CMD', 'node', 'healthcheck.mjs'], interval: '5s' });
});
```

Executable workloads must have both a final image and explicit build metadata after
customization. Assigning a healthcheck does not silently discard native lifecycle hooks:
acknowledge replacement separately with
`.publishAsDockerComposeService({ replaceRunLifecycle: true })` when required.
Typed `model` properties expose the supported Compose fields without proxy-based dynamic APIs.
Additional services are included in verified deployment state.

Service callbacks receive `(composeService, service)`. The target resource exposes `name()`
and `parent()`. `parameter.asEnvironmentPlaceholder(composeService)` captures a declared
parameter as a required Compose interpolation expression, including when used only in
`service.containerName`; it never fetches the parameter's local/runtime value.
`compose.getHostAddressExpression(endpoint).getValue()` returns the deterministic Compose
service DNS name, not an allocated host address or port.

`configureEnvFile(async entries => ...)` customizes captured input metadata through
`entries.get(name).description` and `.defaultValue` accessors. Bind mounts become
`<NORMALIZED_RESOURCE>_BINDMOUNT_<INDEX>` inputs; their authored source is the nonsecret
default, and callbacks can replace it. This prototype deliberately writes **no `.env` file**:
descriptions and approved nonsecret defaults go into `deployment-inputs.json`. Deploy
evaluates those defaults in memory, with explicit environment values taking precedence.
Relative customized bind sources resolve as Compose paths relative to the output directory.
Secret defaults are rejected. Declared nonsecret parameter defaults are honored; missing
secret metadata is treated conservatively as secret.

Callbacks execute sequentially in registration order, once per target/model projection;
service callbacks precede Compose-file callbacks, then captured-input callbacks. Publish
never invokes Docker, reads secret values, or mutates the native workload definition.

## Multiple environments and pipeline graph

Create as many named environments as needed and assign each workload with
`.withComputeEnvironment(compose)`. A single environment is the implicit default.
With multiple environments every non-excluded workload must select one explicitly.
Cross-environment endpoint references and wait dependencies fail clearly rather than
silently dropping producers. Parameters and compute environments remain in the native
graph, but are not emitted as workload services.

Each environment contributes `validate-<name>`, `prepare-<name>`, `publish-<name>`,
and `deploy-<name>` to the ordinary application pipeline. Publish/deploy aggregators
and their prerequisites select these steps; deploy depends on publish. Applications
can add and configure other pipeline steps normally. Container builds still occur
in Compose's deploy-time `up --build`; the integration contributes no separate
Build action and does not claim to have built images during publish.
The shared `validate-docker-compose` step checks environment presence; per-environment
validation checks generated models and customizations. Neither invokes Docker or resolves
deployment inputs. Publish additionally checks local build paths. Docker version, daemon,
and resolved-input checks belong exclusively to deployment.

A single environment keeps the existing output root. Multiple environments write
under `<output>/<normalized-environment-name>/` with distinct project identities.
Explicit Compose-file project basenames also receive an environment-identity suffix in
multi-environment apps, preventing identical callbacks from sharing a Docker project.
Inspecting the pipeline only describes graph data and creates no output or Docker resources.

## Dashboard

Like today's Docker integration, environments enable a real Aspire Dashboard container
by default. This prototype pins `mcr.microsoft.com/dotnet/aspire-dashboard:13.0`.
It enables browser-token authentication and OTLP API-key authentication, never anonymous
access. Deploy requires explicit `ASPIRE_PARAMETER_<ENV>_DASHBOARD_TOKEN` and
`ASPIRE_PARAMETER_<ENV>_DASHBOARD_OTLP_KEY` values; publish writes only their placeholders.
The dashboard is internal-only unless explicitly configured:

```typescript
await compose.configureDashboard(async dashboard => {
    await dashboard.withHostPort({ port: 18888 });
    await dashboard.withForwardedHeaders({ enabled: false });
    const url = await (await dashboard.primaryEndpoint()).url();
});
```

Host publication is loopback-only. Forwarded headers map to the dashboard's actual
`ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED` setting; enable them only behind your
trusted proxy. `primaryEndpoint()` and `otlpGrpcEndpoint()` expose authored target
URLs/ports, not runtime allocations. Workload OTLP instrumentation is not inferred:
configure your instrumented workload's OTLP endpoint and API-key reference explicitly.
Disable Dashboard with `.withDashboard({ enabled: false })`; `.withDashboard()` re-enables it.
The bounded Redis/web end-to-end sample disables Dashboard; Dashboard container startup
is not covered by that scenario.

Verified output has this shape; read URLs from `services[].endpoints[].url`:

```json
{
  "schemaVersion": 1,
  "projectName": "aspire-application-compose-composesmoke-0123456789abcdef",
  "services": [
    {
      "resource": "web",
      "service": "web",
      "state": "running",
      "health": "healthy",
      "endpoints": [
        {
          "name": "http",
          "hostIp": "127.0.0.1",
          "port": 49152,
          "url": "http://127.0.0.1:49152"
        }
      ]
    }
  ]
}
```

`health` is `healthy` for observed healthy services, otherwise `not-configured`
when no healthcheck is configured. Unexposed services have an empty `endpoints` array.

## Publish and deploy

Publish produces deterministic `compose.yaml` (JSON is valid YAML), `deployment-inputs.json`
(required parameter names and complete symbolic URI-encoding expressions), and `prerequisites.txt`.
It never executes Docker or reads parameter values. Deploy requires matching publish artifacts,
Docker Compose 2.20+, an accessible Docker daemon, and any necessary registry credentials.
It checks Compose version and daemon availability, validates configuration, applies with
`up --build --detach --wait --wait-timeout 120`, then queries `ps --all --format json`.
Only verified service status and actual exposed URLs are written to `deployment-state.json`.
The wait timeout is bounded; builds also share an overall command timeout with two minutes of headroom.
An unsuccessful update does not automatically roll back or delete preexisting resources.
State describes the last successful verified deployment, not a live health monitor.

Supply every required secret parameter explicitly through the deploy process environment, for example
`ASPIRE_PARAMETER_DB_PASSWORD`. Names uppercase and replace non-ASCII-alphanumeric characters
other than `_` with `_`; normalization collisions fail. Generated native-run secrets and ambient
`.env` files are never reused. Approved nonsecret defaults may satisfy inputs; explicit environment
values override them. Compose placeholders require nonempty values. Literal `$` characters
are escaped; URI expressions evaluate their complete trees in memory into deterministic extra
interpolation variables. No plaintext values or `.env` files are written. Secrets are still
environment variables visible to Docker/container administrators, not Docker secret mounts.
Do not put literal credentials into the application model; use parameter expressions.

## Supported boundary

- Prebuilt image containers, entrypoint, arguments, environment expressions, named volumes and
  captured bind-mount inputs,
  TCP/UDP endpoints, service DNS/target-port references, and waits.
- Executables only with an explicit deployment image and build context/Dockerfile. The Dockerfile
  owns its entrypoint; the run executable path is not translated. Authored arguments become CMD.
- Build context and Dockerfile paths resolve against `applicationDirectory`. Artifacts retain
  **absolute local paths**, not portable context archives. The publisher copies no source tree.
  Dockerfile must be within its context. Review `.dockerignore` to prevent Docker itself from
  uploading credentials, `.git`, `node_modules`, `.aspire`, or publish output.
- Endpoints marked external by `.withExternalHttpEndpoints()` publish automatically; other
  endpoints remain internal. Bindings default to loopback and a random host port unless the
  native endpoint has an explicitly authored positive host port. A `ports` options array
  replaces the automatic selection, with `published` and `hostIp` overriding binding defaults;
  an empty array disables host publication. Run-mode allocated ports are never reused.
- Waits use `service_healthy` when the producer defines a target healthcheck, otherwise
  `service_started`. Host lifecycle hooks require explicit `replaceRunLifecycle: true` plus a
  target healthcheck, acknowledging that host setup/cleanup hooks will not run.
- Opaque resources must be explicitly excluded. Missing/excluded/cross-environment references,
  wait cycles, unknown options, and unsupported expression/protocol shapes are rejected.
- Names are deterministic and project-scoped using application directory, target/environment
  names, and a non-security identity suffix. Named volumes are not aliases for local run volumes.
  This prototype does not provision Docker, registries, cloud infrastructure, TLS, or identities;
  provide these externally. It does not support replicas, host networking, generated files,
  registry pushes, or resource limits. Generated Compose customization is bounded by the typed
  facade rather than an arbitrary dynamic proxy.

## Cleanup and testing

Cleanup is explicit and external:

```sh
docker compose -p <projectName-from-deployment-inputs.json> -f <output>/compose.yaml down
```

Use the same required deployment interpolation environment when running Compose manually.
URI-expression interpolation variables require evaluating `deployment-inputs.json` (the exported
`resolveDockerComposeInputs` function does this); the integration supplies them automatically
during deploy. Volumes are retained by default: do not add `--volumes` unless deleting data is intended.
Docker images are also retained and their cleanup is external.

The Node built-in tests exercise complete generated objects, secret and URI-expression semantics,
validation, filesystem artifacts, and injectable Docker command sequences without requiring Docker.
`DockerComposeEnvironment.withRunner` and its constructor's `runner` option inject the command runner;
the fluent environment builder's `.withRunner(runner)` injects it into registered pipeline actions.
`generateDockerCompose` and `resolveDockerComposeInputs` expose pure generation/resolution seams.

## Reproducing the live product-CLI smoke

From the repository root, with Node.js 24+, Docker Compose 2.20+, a running Docker
daemon, and the repository's native-AOT/build prerequisites:

```bash
# Apple Silicon macOS:
bash eng/scripts/test-native-hosting.sh osx-arm64

# Linux x64, including the CI lane:
bash eng/scripts/test-native-hosting.sh linux-x64
```

These commands run the broader [native-hosting validation lane](../../../../eng/scripts/test-native-hosting.sh),
not only Compose. The script restores/builds its prerequisites, publishes the actual native-AOT server,
prepares the matching real CLI bundle layout, compiles/runs the Node unit tests, and
invokes [NativeComposeSmoke.mts](../../../../tests/Aspire.Hosting.Native.Core.Tests/AotSmoke/NativeComposeSmoke.mts).
The smoke copies the sample AppHost/web files and the provider descriptor, metadata, and
module into an isolated artifact workspace. Only that workspace receives npm manifests
and `aspire.config.json`. SDK overlay generation and compilation go through the normal
product CLI; the AppHost's generated SDK is not manually injected.

The live scenario checks:

- CLI pipeline inspection lists `publish` and `deploy` without producing publish assets.
- Publish uses the real native server, terminates that server, and creates no Docker
  containers, networks, or volumes.
- Deploy without `ASPIRE_PARAMETER_CACHE_PASSWORD` fails before creating containers.
- A supplied password containing `$`, `@`, `:`, `/`, `?`, `#`, `%`, and whitespace works
  through required interpolation, URI encoding, container environment, and Redis `AUTH`.
- The deployed loopback HTTP URL comes from verified deployment state. Its first counter
  response is `1`; after removing both containers and deploying again with the retained
  Redis volume, it becomes `2`. Another deploy with the same explicit password yields `3`
  while retaining that same volume.
- Publish/deployment artifacts contain neither the raw password nor its URI-encoded form.
- Finally, only the smoke's labeled project resources and unique image are removed;
  container, network, and volume cleanup is asserted. This destructive test cleanup is
  separate from the integration's default retained-volume behavior.

The harness writes reviewable evidence under:

```text
artifacts/native-hosting/ci/logs/typescript-compose-provider-tests.log
artifacts/native-hosting/ci/logs/compose-publish-deploy-smoke.log
artifacts/native-hosting/ci/compose-apphosts/compose-*/proof.json
artifacts/native-hosting/ci/compose-apphosts/compose-*/*-aspire.log
artifacts/native-hosting/ci/compose-apphosts/compose-*/published/
```

`proof.json` records the project identity and observed counter values. Per-invocation
CLI logs redact the supplied password and its encoded representation. The script
recreates `artifacts/native-hosting/ci` on each run, so preserve needed evidence before
rerunning it.

This proves the bounded Redis/web publish/deploy scenario, not production readiness
or acceptance of every option on every platform. Registry authentication, remote Docker
contexts, broader network policy, and other production requirements remain outside this
smoke's evidence and must be evaluated separately.
