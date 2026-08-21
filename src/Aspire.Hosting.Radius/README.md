# Radius hosting integration

Use this integration to publish and deploy an Aspire AppHost's applications to a [Radius](https://radapp.io) compute environment.

`AddRadiusEnvironment` is an Aspire **compute environment**, the same kind of building block as
`AddKubernetesEnvironment`, `AddDockerComposeEnvironment`, and `AddAzureContainerAppEnvironment`.
Add it to your AppHost, keep your existing resource graph unchanged, and target it with the standard
`aspire publish` / `aspire deploy` lifecycle — Radius becomes just another target you
deploy to, with no changes to how you declare `AddContainer`, `AddProject`, `AddRedis`, and friends.
Radius participates only at publish/deploy time; `aspire run` continues to run your app locally as usual.

> **Preview / prototype.** This integration is an early prototype. The public API surface and the generated Bicep contract may change in future versions. Pin the integration version in `AppHost.csproj` and avoid taking dependencies on any internal types.

This README is layered by intent:

* **Getting started** — the happy path: add the environment, run, publish, deploy.
* **Deploying to a cloud** — Azure/AWS providers and credentials.
* **Production & platform features** — secret stores, multiple resource groups, resource/recipe customization.
* **Reference** — supported resources, diagnostics, and known limitations.

## Getting started

### Prerequisites

* **Radius v0.60.0 or later.** This integration is developed and verified against Radius **v0.60.0** and up; the generated Bicep (resource types, `secretStores`, and `recipeConfig`) targets the schemas shipped in that release. **Radius v0.59 and earlier are not supported**, and the incompatibility is silent: v0.60 renamed the `Radius.Core/recipePacks` fields `recipeKind`/`recipeLocation` to `kind`/`source`, and the Radius API server drops fields it does not recognize instead of rejecting them. Deploying these artifacts to a v0.59 cluster therefore produces an *empty* recipe pack with no error, and every backing resource then fails to resolve a recipe.
* If you are upgrading an existing cluster, run `rad upgrade kubernetes` before deploying, so the control plane and the registered resource types match the version this integration targets.
* A Kubernetes cluster (for example `kind`, `minikube`, AKS) with [Radius](https://docs.radapp.io/installation/) installed.
* The `rad` CLI on PATH. Version must match the pinned Radius Bicep extension this integration emits (currently `0.60`). Run `rad version` to check.
* `rad init` has been run against the target cluster so the workspace and environment exist.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Radius` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Radius
```

## Quick start

In the _AppHost.cs_ file of `AppHost`, add the environment:

**C#**

```csharp
builder.AddRadiusEnvironment("radius");
```

**TypeScript**

```typescript
await builder.addRadiusEnvironment("radius");
```

That single line is all you add — your existing resource declarations stay the same. The standard
Aspire lifecycle still works; Radius participates only in publish and deploy:

| Command | What happens |
|---------|--------------|
| `aspire run` | Runs your app locally as usual. Radius does no run-mode wiring — the Radius environment is inert during local development and takes effect only at publish/deploy. |
| `aspire publish` | Generates `app.bicep` plus a `bicepconfig.json` pinned to the Radius extension version. |
| `aspire deploy` | Invokes `rad deploy` against the generated Bicep — no direct `rad` knowledge needed for the happy path. |

Publish and deploy:

```shell
aspire publish -o radius-artifacts
aspire deploy
```

### Local development with `aspire run`

Radius is a publish/deploy-only target: `aspire run` builds and runs your app locally exactly as it
would without Radius, using the normal Aspire dashboard for your resources. The Radius environment
does not attach annotations or alter your resources during local development — Radius wiring happens
when you `aspire publish` / `aspire deploy`. You iterate locally as usual, then publish/deploy the
same application resources to a cluster.

### Multiple compute environments

When the model contains more than one compute environment (for example a Radius environment alongside a Kubernetes one), explicitly assign each resource to the environment that should publish it:

```csharp
var radius = builder.AddRadiusEnvironment("radius");
var k8s    = builder.AddKubernetesEnvironment("k8s");

builder.AddContainer("api", "myorg/api", "1.0")
       .WithComputeEnvironment(radius);
```

Untargeted resources surface a clear error from the core pipeline instead of being silently claimed by one environment.

## Deploying to a cloud

Everything above works against a plain Kubernetes cluster with Radius installed. To target Azure
and/or AWS resources, configure the providers in the AppHost.

### Cloud providers

Configure Azure and/or AWS cloud providers directly in the AppHost. The publisher
emits the provider configuration on the `Radius.Core/environments` resource using the
native schema's discrete fields — `properties.providers.azure.subscriptionId` /
`properties.providers.azure.resourceGroupName` for Azure and
`properties.providers.aws.accountId` / `properties.providers.aws.region` for AWS
(the legacy `Applications.Core/environments` schema instead used a single `scope`
path) — and the deploy pipeline registers credentials via `rad credential register`
before `rad deploy` runs.

```csharp
var clientSecret = builder.AddParameter("azure-sp-secret", secret: true);

builder.AddRadiusEnvironment("radius")
       .WithAzureProvider(
           subscriptionId: "00000000-0000-0000-0000-000000000000",
           resourceGroup:  "rg-radius",
           azure => azure.WithServicePrincipal(
               tenantId:     "11111111-1111-1111-1111-111111111111",
               clientId:     "22222222-2222-2222-2222-222222222222",
               clientSecret: clientSecret))
       .WithAwsProvider(
           accountId: "123456789012",
           region:    "us-west-2",
           aws => aws.WithIrsa("arn:aws:iam::123456789012:role/radius-irsa"));
```

Supported credential modes:

| Provider | Mode | Method |
|----------|------|--------|
| Azure | Service Principal | `azure.WithServicePrincipal(tenantId, clientId, clientSecret)` |
| Azure | Workload Identity | `azure.WithWorkloadIdentity(tenantId, clientId)` |
| AWS   | Access Key        | `aws.WithAccessKey(accessKeyId, secretAccessKey)` |
| AWS   | IRSA              | `aws.WithIrsa(iamRoleArn)` |

Cloud-provider credential secret material (Azure SP client secret, AWS access-key pair) must be supplied
via `builder.AddParameter(..., secret: true)`. The integration never inlines
those credential values into Bicep or manifests; `rad credential register` resolves them during
deploy and redacts them from any logged command line. Secret Aspire parameters used in container
environment variables are emitted as `@secure()` Bicep parameters (never literals) and their values
are supplied to `rad deploy` separately, not by this credential-registration path.

> **Security note:** `rad credential register` accepts credential secrets only as command-line
> arguments, so during registration those resolved values are briefly visible to other users on the
> same host via the process table (`ps` / `/proc/<pid>/cmdline`). Log redaction does not mitigate
> this local, transient exposure. Deploy-time secret parameters do not share this concern — they are
> written to an owner-only temporary parameters file rather than the command line.

See the [Radius cloud providers documentation](https://docs.radapp.io/guides/deploy/environments/cloud-providers/)
for an end-to-end walkthrough.

## Production & platform features

The features below are power/enterprise capabilities for platform teams. They are opt-in and not
needed for a standard single-app deploy — reach for them when your topology demands it.

### Recipe parameters

Flow platform values into the [Radius recipes](https://docs.radapp.io/guides/recipes/) that
provision your backing resources with `WithRecipeParameters`. Parameters can be set environment-wide
(applied to every recipe in the environment) or scoped to a specific Radius resource type
(resource-type-scoped values win on key collision):

```csharp
var radius = builder.AddRadiusEnvironment("radius")
    // Environment-wide: applied to every recipe.
    .WithRecipeParameters(p => p["region"] = "eastus")
    // Scoped to one resource type; overrides the environment-wide value on collision.
    .WithRecipeParameters("Radius.Data/redisCaches", p => p["sku"] = "Premium");
```

- **Type fidelity** — numbers, booleans, arrays, and objects are emitted with their Bicep types
  (a `6379` stays a number, not `'6379'`).
- **Aspire parameters flow as `@secure()` params, never literals** — binding an
  `AddParameter(..., secret: true)` value emits a valueless secure Bicep `param` and passes the
  resolved value at deploy time (`rad deploy --parameters`), so no secret lands in the artifact.
- **Provider references** — reuse a configured cloud provider's scope without re-declaring it, e.g.
  `p["region"] = RadiusProviderReference.AwsRegion`. Referencing a provider that is not configured
  fails at publish with a message naming the missing provider.
- A parameter scoped to a resource type with no emitted recipe is ignored with a warning; the
  publish still succeeds.

### Secret management

> **Experimental** — the secret-store APIs are gated by `ASPIRERADIUS006`. Suppress the
> diagnostic (`#pragma warning disable ASPIRERADIUS006`) to opt in.

Declare a Radius secret store (`Applications.Core/secretStores`) and populate it in exactly one
of three ways:

```csharp
#pragma warning disable ASPIRERADIUS006

// Inline — Radius-created from Aspire secret parameters (@secure() params, redacted at deploy).
var user = builder.AddParameter("db-user", secret: true);
var pass = builder.AddParameter("db-pass", secret: true);
builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.BasicAuthentication)
       .WithData(d => { d.Add("username", user); d.Add("password", pass); });

// For a single key there is a convenience overload:
//   builder.AddRadiusSecretStore("api", RadiusSecretStoreType.Generic).WithData("api-key", apiKey);

// Reference an existing cluster Secret (external operator / hand-applied).
radius.WithSecretStore("tls-cert", RadiusSecretStoreType.Certificate, s =>
    s.WithExistingSecret("app/tls-cert", "tls.crt", "tls.key"));

// GitOps sealed secrets — the encrypted manifest is applied before rad deploy and awaited.
radius.WithSecretStore("db-creds", RadiusSecretStoreType.BasicAuthentication, s =>
    s.WithSealedSecret("./secrets/db-creds.sealed.yaml", "username", "password"));
```

- **Scope is implied by the API form**: `builder.AddRadiusSecretStore(...)` is application-scoped
  (`properties.application`); `radius.WithSecretStore(...)` is environment-scoped
  (`properties.environment`).
- **Encoding** defaults to `base64` for `certificate` stores and `raw` otherwise.
- **Sealed secrets** require `kubectl` on `PATH` and the Bitnami Sealed Secrets controller in the
  target cluster; the integration applies the already-encrypted manifest (it never runs
  `kubeseal`) and polls for the materialized `Secret` (default 120s, overridable via
  `WithMaterializationTimeout`, which applies to sealed stores only — `ASPIRERADIUS062`).
- **Consume** a store from `recipeConfig` auth / `envSecrets` via
  `WithBicepRegistryAuthentication` / `WithTerraformGitAuthentication` /
  `WithRecipeEnvironmentSecret`, referenced by the store's `.id`.

## Reference

### Supported resources

* `AddContainer(...)` — published as a Radius container workload (`Radius.Compute/containers`).
* `AddProject<T>(...)` — published as a Radius container workload only when the project has a pre-built image attached with `WithContainerImage("<registry>/<image>:<tag>")`. Without one, `aspire publish` fails with a remediation message to build and push an image the cluster can pull.
* Selected resources with a Radius mapping emit a Radius resource type via the resource type mapper. Redis, PostgreSQL, and RabbitMQ emit the current `Radius.*` user-defined types; MongoDB, SQL Server, and the Dapr building blocks still emit the older `Applications.*` portable types, because their UDT equivalents have no published Kubernetes recipe. Child database resources (for example `AddSqlServer("sql").AddDatabase("appdb")`) are collapsed onto the parent today.

Other Aspire resource types are not emitted; only the resources listed above appear in the generated Bicep.

### Backing resources and connection information

A *backing* resource (Redis, PostgreSQL, MongoDB, SQL Server, RabbitMQ) is provisioned by a Radius **recipe**, not as a `Radius.Compute/containers` workload. The recipe — not Aspire — decides the Kubernetes `Service` name and the credentials, so nothing about a backing resource's address can be derived from its Aspire endpoint.

Every value a consumer sees for a backing resource is therefore projected from that resource's own Radius resource:

| Value | Projected from |
|-------|----------------|
| Host / port (and anything composed from them: `ConnectionStrings__*`, `*_HOST`, `*_PORT`, `*_URI`, service discovery) | `<resource>.properties.host` / `<resource>.properties.port` (`properties.server` for SQL Server, whose legacy type names it that way) |
| Password, for resources emitted as legacy `Applications.*` types (MongoDB, SQL Server) | `<resource>.listSecrets().password` — the recipe generates the credential |
| Password, for Redis | Nothing. The pinned `kube-recipes/rediscaches` recipe deploys Redis **without authentication** and publishes no password secret, so consumers receive an empty password. A password Aspire generated is dropped with a warning naming the discarded value (`ASPIRERADIUS075`); a password supplied explicitly fails the publish (`ASPIRERADIUS085`) |
| User name, for resources emitted as legacy `Applications.*` types (MongoDB, SQL Server) | `<resource>.properties.username` — but only when the AppHost supplied one as a parameter (see the known limitations) |
| Password and user name, for PostgreSQL | Written onto the resource's own required `username` / `password` schema properties (which the recipe reads as `context.resource.properties.<name>`), using the same secure Bicep parameter Aspire composes into the connection string, so both sides agree by construction |
| Password, for RabbitMQ | Written into a dedicated `Radius.Security/secrets` resource, whose **resource ID** is assigned to the broker's `password` property — that property takes a secret reference, not a password string. The secret carries the same secure Bicep parameter Aspire composes into the connection string, so both sides agree by construction |
| User name, for RabbitMQ | Written onto the resource's `username` property, so the broker is provisioned with the same user the connection string names. An explicit user name is **required** — see `ASPIRERADIUS082` |

Because the substitution happens on the underlying values rather than on formatted strings, connection strings, URIs, and the individual connection properties emitted by `WithReference` all resolve consistently. Values that appear inside a URI are wrapped in `uriComponent(...)` so a recipe-generated credential containing `@`, `/`, or `:` cannot corrupt the URI.

For resources emitted as legacy `Applications.*` types (MongoDB, SQL Server), a password or user name the AppHost supplies as a parameter is *not* used when deploying to Radius — the recipe generates its own. Aspire replaces it with the recipe's value and logs a warning, since the parameter's value never reaches the cluster. Passwords Aspire generated itself are replaced silently. Resources emitted as `Radius.*` types (PostgreSQL, RabbitMQ) behave the opposite way: the parameter *is* handed to the recipe — directly on the resource's properties for PostgreSQL, through a `Radius.Security/secrets` resource for RabbitMQ — so the value the AppHost supplied is the deployed credential and nothing is replaced. Redis is neither: its recipe deploys an unauthenticated server, so a password Aspire generated is discarded with the `ASPIRERADIUS075` warning, while a password supplied explicitly (`AddRedis("cache", password: ...)`) fails the publish with `ASPIRERADIUS085` rather than being silently discarded.

Radius additionally injects its own `CONNECTION_<NAME>_<PROPERTY>` environment variables for every entry in a container's `connections` block. Those are separate from — and not a replacement for — the `ConnectionStrings__*` variables Aspire's client integrations read.

#### Where recipe-generated credentials end up

A recipe-generated credential is emitted as a `listSecrets()` call in the generated Bicep, so no secret value is written into the published artifacts — an improvement over emitting a resolved credential, and the reason `aspire publish` output for a backing resource contains no password.

That is a trade rather than a pure reduction, and it is worth understanding before deploying:

* A `@secure()` deployment parameter is excluded from ARM/Radius deployment history. A `list*()` result assigned to a resource property that is not itself `@secure()` generally is not, so the resolved credential can appear in deployment records.
* Radius's own `connections` credentials are still emitted as clear-text container environment variables. Aspire does not control that path.

#### How credential-bearing environment variables are published

A container environment variable is emitted in one of two forms:

* **`value`** — for a value that carries nothing sensitive. It appears verbatim in the Kubernetes `Deployment` spec, which keeps the deployed app readable.
* **`valueFrom.secretKeyRef`** — for a value that carries a credential: any value built from a secret parameter (`AddParameter(name, secret: true)`, and the generated passwords Aspire creates for backing resources) or from a recipe's `listSecrets()` output. The credential is written to a `Radius.Security/secrets` resource named `<container>-env-secret` and referenced by key, so it reaches the pod as a Kubernetes `Secret` and never appears in the `Deployment` spec or its rollout history.

The whole composed value moves into the secret, not just its sensitive fragment. That is deliberate: percent-encoding for a value destined for a URI is applied per-fragment by Bicep's `uriComponent()` at deploy time, whereas kubelet's `$(VAR)` expansion — the alternative way to compose a secret-backed fragment into a larger string — substitutes at pod start, long after Bicep could have escaped it. A password containing `@`, `:`, or `/` would silently corrupt the connection string. Keeping composition in Bicep preserves the escaping exactly.

One secret is emitted per container, holding every credential-bearing variable keyed by the variable's own name. Kubernetes restricts `Secret` data keys to letters, digits, `-`, `_`, and `.`; a credential-bearing variable whose name falls outside that set fails the publish with `ASPIRERADIUS083` rather than being rejected by the API server at deploy time.

This secret is only ever read by its own container, so it cannot create the `secret → resource → secret` cycle that a backing resource's credential secret is kept separate to avoid.

Referencing a backing resource that is deployed to a *different* Radius environment fails the publish with `ASPIRERADIUS069`: another environment's recipe outputs are not reachable from the generated Bicep. Deploy the consumer and the backing resource to the same environment. The same failure surfaces from the Kubernetes, Azure Container Apps, and Azure App Service publishers when *they* reference a Radius-owned backing resource, as the public `RadiusBackingResourceProjectionException`. Only *address* properties fail this way (`Url`, `Host`, `IPV4Host`, `Port`, `TargetPort`, `HostAndPort`); `Scheme` and `TlsEnabled` are read from the endpoint declaration itself and are still answered, unless the endpoint is TLS-enabled — see `ASPIRERADIUS081`.

### Diagnostics

The package uses the `ASPIRERADIUS` diagnostic prefix for two mechanisms: compile-time
`[Experimental]` gates on preview APIs, and runtime configuration/publish validation errors.

| Code | Mechanism | Surfaced as |
|------|-----------|-------------|
| `ASPIRERADIUS003` | Experimental gate on the cloud-provider surface (`WithAzureProvider` / `WithAwsProvider` and their credential callbacks) | `[Experimental]` warning (suppressible), documented at `https://aka.ms/aspire/diagnostics/<id>` |
| `ASPIRERADIUS004` | Experimental gate on the `ConfigureRadiusInfrastructure` escape hatch and its construct types | `[Experimental]` warning (suppressible) |
| `ASPIRERADIUS006` | Experimental gate on the secret-store surface (`AddRadiusSecretStore` / `WithSecretStore` and their population/consumer callbacks) | `[Experimental]` warning (suppressible) |
| `ASPIRERADIUS057` | Experimental gate on `WithContainerImage` | `[Experimental]` warning (suppressible) |

Runtime validation codes:

| Code | When | Meaning |
|------|------|---------|
| `ASPIRERADIUS010` | Provider config | A cloud-provider credential callback did not select a credential. |
| `ASPIRERADIUS011` | Provider config | Conflicting cloud-provider credentials across environments sharing a Radius installation. |
| `ASPIRERADIUS028` | Publish | Two recipe parameters bound to distinct Aspire parameters whose names sanitize to the same Bicep identifier. Rename one so they produce distinct identifiers. |
| `ASPIRERADIUS040`–`ASPIRERADIUS052`, `ASPIRERADIUS055`, `ASPIRERADIUS058`–`ASPIRERADIUS059`, `ASPIRERADIUS061`–`ASPIRERADIUS068` | Secret-store (`AddRadiusSecretStore` / `WithSecretStore`) validation, publish, and deploy | Thrown `ArgumentException` (call site, e.g. empty/invalid name or key) / `InvalidOperationException` (fail-fast validation gate, publish, or deploy). Key codes: `041` (declare exactly one population mode — the zero-mode case), `044` (`WithSealedSecret` manifest missing/unreadable, malformed, ambiguous/duplicate-key, plaintext-capable, missing a non-empty `spec.encryptedData` mapping of standard-base64 sealed values, or not a single encrypted Bitnami `SealedSecret`), `045` (`kubectl` not on `PATH`), `046` (`WithExistingSecret` reference is not a valid `[namespace/]name` — each segment must be a DNS-1123 label/subdomain), `051` (a secret-store consumer is incompatible with the referenced store — e.g. Bicep-registry auth requires a `basicAuthentication` (username/password) store, and Terraform Git PAT auth requires a store that declares a `pat` key), `052` (a key-specific `envSecrets` consumer references a key a non-empty declared key set does not contain), `058` (the sealed `Secret` did not materialize before deploy: the Sealed Secrets controller never reported `Synced` for the applied `SealedSecret` generation — the controller must have status updates enabled, i.e. not `--update-status=false` / Helm `updateStatus: false`), `059` (the active `rad` workspace's Kubernetes context could not be resolved; publish/deploy fails closed rather than applying the `SealedSecret` to `kubectl`'s ambient context — configure the active `rad` workspace or set the `ASPIRE_RADIUS_KUBE_CONTEXT` override), `061` (the materialized sealed `Secret` is missing a declared key), `062` (`WithMaterializationTimeout` was set on a store that is not populated with `WithSealedSecret`), `063` (a `WithSealedSecret` manifest embeds a plaintext `kind: Secret` inside a `last-applied-configuration` annotation), `064` (a key-specific `envSecrets` consumer references a store that declares no keys), `065` (a secret store's population was declared more than once — repeated or cross-mode `WithData`/`WithExistingSecret`/`WithSealedSecret`), `066` (a bounded `kubectl` apply/verify operation exceeded the store's materialization budget and was cancelled), `067` (a secret data key is not a valid Kubernetes Secret key — only alphanumeric characters, `-`, `_`, or `.`, at most 253 characters, and not `.`/`..`), `068` (an application-scoped store was declared but the model contains no Radius environment to emit and deploy it — add one with `AddRadiusEnvironment`). Note: `056` (Bicep identifier collision) and `057` (`WithContainerImage` experimental) are separate diagnostics documented in their own rows, and codes `053`/`054`/`060` are retired. |
| `ASPIRERADIUS056` | Publish | Two emitted constructs map to the same Bicep identifier (e.g. a resource named `app` or `recipepack` colliding with a synthesized construct, or two resource names that sanitize to the same identifier such as `my-x` and `my.x`). Bicep symbols share one flat namespace; rename the conflicting resource. |
| `ASPIRERADIUS069` | Publish | A Radius-owned backing resource has no derivable address, so a reference to one of its address properties (`Url`, `Host`, `IPV4Host`, `Port`, `TargetPort`, `HostAndPort`) cannot be resolved. `Scheme` and `TlsEnabled` come from the endpoint declaration and are unaffected by *this* diagnostic (but see `ASPIRERADIUS081`). Raised as the public `RadiusBackingResourceProjectionException`, including from other publishers. |
| `ASPIRERADIUS070` | Publish | A resource references a parameter that is also a backing resource's credential. The recipe generates that credential, so the referencing resource receives the recipe's value rather than the parameter's. |
| `ASPIRERADIUS071` | Publish | An emitted Radius type has no connection schema entry, so neither its address nor its credentials can be projected. This is a gap in the resource-type mapping rather than something an AppHost can fix — a type was added to the mapper without a matching schema entry. |
| `ASPIRERADIUS072` | Publish | More than one database on a single server resource is referenced by consumers, but the recipe provisions one. |
| `ASPIRERADIUS073` | Publish | A `ReferenceExpression` requested a string format the publisher cannot express in Bicep. |
| `ASPIRERADIUS074` | Publish | A projected connection value targets a construct a `ConfigureRadiusInfrastructure` callback removed. |
| `ASPIRERADIUS075` | Publish (warning) | The recipe a backing resource maps to provisions no credential (today: Redis, whose `kube-recipes/rediscaches` recipe deploys an unauthenticated server). Consumers receive an empty password, and the password Aspire generated for the resource is not applied to the deployed workload. An *explicitly supplied* password fails the publish instead — see `ASPIRERADIUS085`. |
| `ASPIRERADIUS076` | Publish | A `Radius.*` type requires `username`/`password` as schema properties, but the Aspire resource exposes no such connection property for Aspire to supply, so the deployment would be rejected by schema validation. |
| `ASPIRERADIUS077` | Publish | A reference asks a backing resource for something its recipe does not publish: a *secondary* endpoint (for example RabbitMQ's `management` endpoint, which the mapped recipe does not deploy), or an endpoint property that has no projection. Fixed by referencing the primary endpoint or removing the reference. |
| `ASPIRERADIUS078` | Publish | A conditional `ReferenceExpression`'s condition is only known at deploy time, so the branch to emit cannot be selected while publishing. |
| `ASPIRERADIUS079` | Publish | An emitted Radius type has a connection schema, but that schema declares no host or port output, so consumers cannot be given its address. |
| `ASPIRERADIUS080` | Publish (warning) | A referenced `AddDatabase(...)` child names a database the mapped recipe does not create (the legacy SQL Server recipe). |
| `ASPIRERADIUS081` | Publish | A TLS-enabled backing endpoint was asked for `Scheme`, `TlsEnabled`, or `Url`. The mapped Radius type publishes no transport-security output, so the value would describe how the resource runs locally rather than how the recipe deploys it. Raised as `RadiusBackingResourceProjectionException`, including from the Kubernetes, Azure Container Apps, and Azure App Service publishers when they reference a Radius-owned backing resource. |
| `ASPIRERADIUS082` | Publish | A RabbitMQ resource would be deployed with the user name `guest`, which RabbitMQ restricts to loopback connections — the broker would reject every workload that connects to it. Supply an explicit user name with `AddRabbitMQ(name, userName: ...)`. |
| `ASPIRERADIUS083` | Publish | A credential-bearing environment variable's name contains characters that a Kubernetes `Secret` data key may not contain (allowed: letters, digits, `-`, `_`, `.`). Rename the variable. |
| `ASPIRERADIUS084` | Publish | A `ConfigureRadiusInfrastructure` callback removed the `Radius.Security/secrets` resource a container reads a credential-bearing environment variable from. Keep the resource, or set the variable explicitly in the callback. |
| `ASPIRERADIUS085` | Publish | A password was supplied explicitly to a backing resource whose recipe provisions no credential (today: Redis). The deployed server would accept unauthenticated connections, so the publish fails rather than silently dropping the credential. Remove the password, or provision the resource yourself if the workload must authenticate. A password Aspire *generated* is dropped with the `ASPIRERADIUS075` warning instead. |
| `ASPIRERADIUS086` | Publish | A backing resource's connection property could not be resolved to a Bicep expression — it is composed from a value that only exists at run time. Raised as `RadiusBackingResourceProjectionException`. |
| `ASPIRERADIUS087` | Publish | A `ConfigureRadiusInfrastructure` callback left a container environment variable carrying both a `value` and a `valueFrom.secretKeyRef`, or an incomplete secret reference (only one of `SecretName`/`SecretKey`). The two forms are mutually exclusive and Kubernetes rejects an environment variable that sets both. Assign either `Value` or the `SecretName`/`SecretKey` pair. |

A variable that `WithReference` itself injected is exempt from `ASPIRERADIUS070` — receiving the recipe's credential is the whole point of the reference. That exemption is decided from the variable name the connection-property splat produces for a reference the resource actually declares (`CACHE_PASSWORD`, or `ADMIN_PASSWORD` for `WithReference(cache, "admin")`), not from the value: an AppHost author can construct a value identical to an injected connection property, and such a variable *is* silently replaced by the recipe credential, so it is reported rather than exempted.

### Known limitations

* For `ASPIRERADIUS011`, AWS access-key credential conflicts are compared by the Aspire parameter name that supplies the access-key ID, not by the resolved access-key value. Two environments that use different parameter names for the same key can be flagged as a false conflict, while the same parameter name with different values is not flagged.
* Application-scoped sealed secret stores are applied by every Radius environment. A benign concurrent re-apply of the same manifest (for example, two environments sharing one store) is tolerated: the wait syncs against the latest live `SealedSecret` generation and verifies the declared keys materialize. It does not, however, compare the live `SealedSecret`'s encrypted values against the manifest this deployment applied, so a concurrent writer that replaces the encrypted values while preserving the same key names (only possible if a distinct manifest collides on the same namespace/name) would not be detected.
* Recipe customization (per-instance recipes via `PublishAsRadiusResource`), multiple Radius resource groups, and cloud-managed resources are not part of this release; they are planned for follow-up releases.
* A backing resource's recipe provisions a single database. Referencing more than one database from a single server resource (for example `WithReference` on two `AddDatabase(...)` children of one `AddPostgres(...)`) fails the publish with `ASPIRERADIUS072`. Declaring extra databases nobody references is allowed, but only one is created; a warning names the one that was. Declaring several databases and referencing none of them through `WithReference` also fails with `ASPIRERADIUS072`: a database consumed only from inside a `WithEnvironment` callback records no reference annotation, so Aspire cannot tell which one the recipe should create. A server with no `AddDatabase(...)` child publishes with a warning, and the recipe is asked to create a database named after the user: the server-level connection string carries no database name, and clients default it to the user name.
* A parameter cannot serve as both the user name and the password of one backing resource, and a credential parameter cannot be shared across backing resources when either side's credential is generated by its recipe. Both fail the publish with `ASPIRERADIUS070`, because the substitution that projects recipe-generated credentials is keyed by parameter and could only be resolved to one of the two values.
* MongoDB and SQL Server are emitted as the legacy `Applications.Datastores/mongoDatabases` and `Applications.Datastores/sqlDatabases` types rather than `Radius.*` UDTs. The contrib UDTs (`Radius.Data/mongoDatabases`, `Radius.Data/sqlServerDatabases`) do ship in the `0.60` Bicep extension, so the *types* exist — what is missing is a published Kubernetes recipe for either of them. Without a recipe there is nothing to deploy, so the legacy types — which have both a published recipe and a `listSecrets()` action — remain the only deployable option. Each mapping moves to its UDT once the corresponding recipe ships.
* **The legacy SQL recipe does not create databases.** It takes a `database` parameter but only echoes it back in its outputs: the workload it deploys is a SQL Server with the `sa` login and nothing that runs `CREATE DATABASE`. `AddDatabase(...)` creates the database in run mode only, so a referenced `AddSqlServer("sql").AddDatabase("appdb")` hands the consumer a connection string naming a database the deployment does not contain. Publishing warns when this happens. Have the application create the database on startup (for example EF Core's `Migrate()`/`EnsureCreated()`), or deploy SQL Server outside the Radius environment. This resolves when `Radius.Data/sqlServerDatabases` gains a published recipe — its schema declares `database` as required and created.
* **Redis is deployed without authentication.** The recipe the environment pins for Redis (`ghcr.io/radius-project/kube-recipes/rediscaches`) starts a bare `redis` image with no `requirepass` and publishes only `host`/`port` — it records no password secret. Consumers therefore receive an empty password. A password Aspire generated for `AddRedis(...)` is dropped with the `ASPIRERADIUS075` warning; a password supplied explicitly (`AddRedis("cache", password: ...)`) fails the publish with `ASPIRERADIUS085`, because dropping a credential the AppHost author asked for would deploy an unauthenticated cache without saying so. Deploy Redis as a container, or pair the type with a recipe that configures and publishes a credential, if authentication is required.
* Only a backing resource's *primary* endpoint can be referenced. A Radius recipe publishes a single address, so a reference to a secondary endpoint — `AddRabbitMQ(...).WithManagementPlugin()`'s `management` endpoint, for example, which the mapped `Radius.Messaging/rabbitMQ` recipe does not deploy at all — fails the publish with `ASPIRERADIUS077` rather than projecting the primary address for it.
* TLS on a backing resource cannot be projected, because no mapped Radius type publishes a transport-security output. The recipe decides whether the provisioned server terminates TLS, while `EndpointProperty.Scheme` and `EndpointProperty.TlsEnabled` describe the container Aspire would have run locally — so a TLS-enabled backing endpoint would tell consumers `rediss://` and `,ssl=true` about a server the recipe starts in plaintext. Rather than emit that, publishing a TLS-enabled backing endpoint fails with `ASPIRERADIUS081` for the properties that carry the decision (`Scheme`, `TlsEnabled`, and `Url`, which embeds the scheme). `Host`, `Port`, and `HostAndPort` are still projected: the address is right regardless of transport. When a mapped type publishes a TLS output this becomes a projection off that output instead of a failure.
* MongoDB's default user name is not projected. `admin` is composed into the connection string as literal text with no value to substitute, so it keeps its default value even though the recipe may have created a different user. Supply a user name parameter (`AddMongoDB(name, userName: ...)`) to get the recipe's user name projected. RabbitMQ is not affected: its user name is an *input* Aspire writes onto the resource, so the deployed broker is created with whatever the connection string says.
* **RabbitMQ requires an explicit user name.** `AddRabbitMQ("queue")` defaults the user name to `guest`, which RabbitMQ restricts to loopback connections, so a broker provisioned with it rejects every workload pod. Aspire cannot substitute a different name on your behalf either: a default user name reaches the publisher as literal text inside the connection string's format string, with no value to rewrite, so the broker and the connection string would disagree. Publishing therefore fails with `ASPIRERADIUS082`. Pass a user name — `AddRabbitMQ("queue", userName: builder.AddParameter("queueuser"))` — and both sides use it.
* `Radius.Messaging/rabbitMQ` carries a `queue` property that Aspire does not emit, because `AddRabbitMQ(...)` declares a *broker* and the Aspire RabbitMQ resource has no queue concept — any value Aspire chose would be invented rather than derived from the AppHost. The property is optional and the recipe defaults it, so the emitted resource still deploys; the queue is simply named by the recipe rather than after anything in the AppHost. There is no way to set it from the AppHost today, and no diagnostic is reported.
* URI escaping is emitted as Bicep's `uriComponent(...)` and evaluated by the Radius deployment engine, which may leave `!'()*` unescaped where Aspire's in-process escaping would not. Those characters are legal unescaped in the parts of a URI where credentials appear, so this is a cosmetic difference.
* **The Redis and RabbitMQ recipes are pinned by commit SHA, not by `:latest`.** `resource-types-contrib` moves the `:latest` tag only when a recipe is part of a stable release, and `kube-recipes/rediscaches` and `kube-recipes/rabbitmq` post-date the last one — `:latest` does not exist for either, and referencing it fails at `rad deploy` with `RecipeDownloadFailed` rather than at publish time. The generated artifacts therefore reference an immutable commit-SHA tag, which is the pin Radius itself uses. This is why a `recipePacks` entry for Redis or RabbitMQ shows a SHA where the other types show `:latest`. Both move to `:latest` once a stable recipe release includes them.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://docs.radapp.io/
* https://aspire.dev/

## Feedback & contributing

https://github.com/microsoft/aspire
