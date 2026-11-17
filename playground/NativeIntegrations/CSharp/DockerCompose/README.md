# Native Docker Compose hosting integration (C# comparison)

Use this integration to model, configure, and publish Docker Compose deployments
against AppHost Server v2. This is an independent C# implementation of
[`TypeScript/DockerCompose/docker-compose.mts`](../../TypeScript/DockerCompose/docker-compose.mts),
not a wrapper around Node.js or the managed `Aspire.Hosting.Docker` package.

It is an experimental existing-target deployer. Docker, registry credentials,
external networks/secrets, image cleanup and destructive teardown remain external.
The supported deployment model and limitations are the same as the TypeScript
prototype.

## Getting started

This is a nonshipping project reference, not an `aspire add` package.
The C# examples use the standalone native client; the product CLI's v2 AppHost
selection still supports TypeScript, not C# AppHosts.

From the repository root:

```bash
./.dotnet/dotnet build playground/NativeIntegrations/CSharp/DockerComposeAppHost/NativeIntegrations.DockerCompose.AppHost.Sample.csproj
```

The [AppHost](../DockerComposeAppHost/Program.cs) reuses the TypeScript sample's
actual [web application and Dockerfile](../../TypeScript/DockerCompose/AppHost/web/).
No duplicate application or integration-specific runtime provider is needed.

## Usage example

```csharp
using Aspire.Hosting.Native.Authoring;
using NativeIntegrations.DockerCompose;

var builder = DistributedApplication.CreateBuilder(["--operation=publish"]);
var compose = builder.AddDockerComposeEnvironment("compose")
    .WithDashboard(enabled: false)
    .ConfigureComposeFile(file =>
    {
        file.GetService("api").PullPolicy = "missing";
        file.AddNetwork("shared", new() { ["external"] = true });
    });

builder.AddContainer("api", "example/api:latest")
    .WithEndpoint("http", 8080, "http")
    .WithExternalHttpEndpoints()
    .WithComputeEnvironment(compose)
    .PublishAsDockerComposeService((target, service) =>
    {
        service.Restart = "unless-stopped";
        service.SetLabel("deployment.environment", target.Parent.Resource.Name);
    });

// client is a caller-owned, authenticated NativeClient.
await builder.PublishAsync(client, "aspire-output", "Production",
    "publish", cancellationToken);
```

The comparison AppHost includes Redis authentication, structured connection
references, a retained volume, an executable's explicit image/build metadata,
target healthchecks, a deployment policy and a custom `verify-publish` target.

### Running the standalone AppHost

Start a matching native server using a fresh `ASPIRE_NATIVE_RPC_TOKEN`, then
use its announced `127.0.0.1:<port>` endpoint. No DCP is required for publishing.
Use the same token in the AppHost process:

```bash
./.dotnet/dotnet run \
  --project playground/NativeIntegrations/CSharp/DockerComposeAppHost/NativeIntegrations.DockerCompose.AppHost.Sample.csproj -- \
  --native-endpoint=127.0.0.1:43210 \
  --native-application-root="$PWD/playground/NativeIntegrations/TypeScript/DockerCompose/AppHost" \
  --step=publish --environment=ComposeSmoke \
  --output-path="$PWD/artifacts/native-hosting/csharp-compose-output"
```

For deployment, start a fresh native server and change `--step=publish` to
`--step=deploy`. Supply `ASPIRE_PARAMETER_CACHE_PASSWORD` explicitly in that
AppHost process. `deploy` includes publishing; custom targets can depend on
`publish` or on individual environment steps.

`PublishAsync` owns and retires its native composition, not the supplied client
or server. It is the standalone entry point, not a new `aspire publish` C# CLI
adapter. The ordinary run-mode `Build().RunAsync()` path remains separate.

## Side-by-side implementation

| Responsibility | C# | TypeScript |
| --- | --- | --- |
| Native graph authoring | `DockerComposeBuilderExtensions.cs` | Fluent extensions in `docker-compose.mts` |
| Typed customization views | `ComposeModels.cs` | `ComposeServiceBuilder`, `ComposeFileBuilder` |
| Pure desired-state projection | `DockerComposeGenerator.cs` | `generateDockerCompose` |
| Publish/deploy and verification | `DockerComposeEnvironment.cs` | `DockerComposeEnvironment` |
| Bounded shell-free processes | `DockerCommand.cs` | `runDockerCommand` |
| AppHost | `../DockerComposeAppHost/Program.cs` | `AppHost/apphost.mts` |

Both implementations consume the native server's sealed `ExportPublishModel`
JSON contract. Neither rebuilds the application graph or resolves native runtime
endpoints/secrets. The C# client adds generic compute-environment, publishing,
endpoint-intent and native-pipeline adapters, without Compose code in the kernel.

The same native graph is used to compare Compose documents, deployment input
contracts, planned service bindings and resolved nested expressions across eight
scenarios. Project/service/volume identity deliberately uses the existing
TypeScript FNV-1a/64 algorithm so the two implementations address the same objects.

### APIs and customization

`AddDockerComposeEnvironment` returns
`IResourceBuilder<DockerComposeEnvironmentResource>`. Use `WithProperties` for
`DefaultNetworkName` and `DashboardEnabled`, `ConfigureDashboard` for the
Dashboard host port/forwarded headers, `ConfigureComposeFile` for generated
services/networks/volumes/configs/external secrets, and `ConfigureEnvFile` for
captured input descriptions and permitted nonsecret defaults.

`PublishAsDockerComposeService` accepts typed options or synchronous/asynchronous
callbacks. Callbacks can configure image, build paths, healthcheck, restart/pull
policy, labels, container name and named volume attachments. Parameter placeholders
are created with `parameter.AsEnvironmentPlaceholder(target)`; arbitrary `$...`
strings are escaped, not treated as interpolation.

For configured parameters the C# recorder uses
`AddConfiguredParameter(name, secret: true)`, corresponding to TypeScript's
`addParameter(name, { secret: true })`. The older C# `AddParameter` overload
retains its existing explicit-value/generated-secret behavior.

The integration contributes the same `validate-docker-compose`, `validate-<name>`,
`prepare-<name>`, `publish-<name>` and `deploy-<name>` steps. Native code validates
the DAG and claims ready steps; C# owns only callbacks. Custom steps use
`builder.Pipeline.AddStep`, dependency/required-by metadata, configuration,
resource attribution, tags, cancellation, summaries and logging. Run-mode
`before-start` callbacks execute before native workload preparation.

## Publish assets and deployment

Publishing requires neither Docker nor credentials. It emits `compose.yaml`
(JSON is valid YAML), `deployment-inputs.json` and `prerequisites.txt`. Build
contexts/Dockerfiles must exist locally; artifacts remain machine-local.

Deployment requires Docker Compose 2.20+, a running daemon, necessary image/registry
access and explicit deployment inputs. It validates existing artifacts and inputs,
checks Compose and the daemon, runs bounded `up --build --detach --wait`, queries
actual health/bindings, then writes secret-free `deployment-state.json`.

Secrets are passed only in the child environment, never arguments, `.env` files,
manifests or state. They remain visible to Docker/container administrators.
Ambient Compose interpolation and redirection settings are not inherited.
Only explicitly external endpoints are published, on loopback/random ports by
default. Multiple environments have distinct projects and output directories;
cross-environment references/dependencies fail explicitly.

Repeated deployments converge existing services. There is no automatic rollback,
orphan removal or destructive cleanup. Use the exact project/file argument list in
`prerequisites.txt` for external `docker compose down`; volumes are retained unless
you explicitly request their destruction.

## Validation

```bash
./.dotnet/dotnet test \
  --project tests/Aspire.Hosting.Native.Core.Tests/Aspire.Hosting.Native.Core.Tests.csproj \
  --no-launch-profile -- \
  --filter-class '*.NativeComposeCSharpTests' \
  --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'
```

The native harness compiles the TypeScript comparison fixture, builds the C#
AppHost and runs cross-language comparisons plus real Docker acceptance:

```bash
bash eng/scripts/test-native-hosting.sh osx-arm64
```

Use the host RID (`linux-x64`, `linux-arm64`, `osx-x64`, or `osx-arm64`).
The real acceptance test exercises the separate C# AppHost process against
the native server, publishes without inputs, rejects missing deployment inputs,
deploys authenticated Redis and the shared web application, calls its HTTP
endpoint, redeploys, recreates Redis and verifies retained data. It removes only
its uniquely scoped containers, volumes and image.

## Additional documentation

- [Native integration packaging and product boundary](../../README.md)
- [TypeScript target contract and limitations](../../TypeScript/DockerCompose/README.md)

## Feedback & contributing

This comparison lives on the AppHost Server v2 prototype branch. Use the
repository's normal issue and contribution process for feedback.
