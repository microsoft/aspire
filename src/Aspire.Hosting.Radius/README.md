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

* **Radius v0.59.0 or later.** This integration is developed and verified against Radius **v0.59.0** and up; the generated Bicep (resource types, `secretStores`, and `recipeConfig`) targets the schemas shipped in that release. Older Radius versions are not supported.
* A Kubernetes cluster (for example `kind`, `minikube`, AKS) with [Radius](https://docs.radapp.io/installation/) installed.
* The `rad` CLI on PATH. Version must match the pinned Radius Bicep extension this integration emits (currently `0.59`). Run `rad version` to check.
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
emits `properties.providers.azure.scope` / `providers.aws.scope` on the
`Radius.Core/environments` resource and the deploy pipeline registers
credentials via `rad credential register` before `rad deploy` runs.

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
deploy and redacts them from any logged command line. Recipe-parameter secrets bound to
Aspire parameters are emitted as valueless Bicep parameters today, not delivered by this
credential-registration path.

> **Security note:** `rad credential register` accepts credential secrets only as command-line
> arguments, so during registration those resolved values are briefly visible to other users on the
> same host via the process table (`ps` / `/proc/<pid>/cmdline`). Log redaction does not mitigate
> this local, transient exposure. Deploy-time recipe parameters do not share this concern — they are
> written to an owner-only temporary parameters file rather than the command line.

See the [Radius cloud providers documentation](https://docs.radapp.io/guides/deploy/environments/cloud-providers/)
for an end-to-end walkthrough.

## Production & platform features

The features below are power/enterprise capabilities for platform teams. They are opt-in and not
needed for a standard single-app deploy — reach for them when your topology demands it.

### Multiple resource groups

> **Experimental** — `WithRadiusResourceGroup` is gated by `ASPIRERADIUS005`. Suppress the
> diagnostic (`#pragma warning disable ASPIRERADIUS005`) to opt in.

Route each resource into a named [Radius resource group](https://docs.radapp.io/guides/resources/) to
partition a single app graph into independently published and deployed units. When any resource is
routed, `aspire publish` emits one `groups/<group>/app.bicep` per group (each carrying only that
group's resources plus the single logical application), and `aspire deploy` creates every group
idempotently and deploys them in dependency order with one `rad deploy` per group.

```csharp
#pragma warning disable ASPIRERADIUS005

// Shared infrastructure in its own group, with its own environment and configuration.
builder.AddRadiusEnvironment("data-env")
    .WithRadiusResourceGroup("shared-data")
    .WithNamespace("shared-data-ns");
var db = builder.AddPostgres("db").WithRadiusResourceGroup("shared-data");

// A per-service group; a cross-group WithReference emits the target's full UCP ID.
builder.AddRadiusEnvironment("orders-env").WithRadiusResourceGroup("orders");
builder.AddProject<Projects.Orders>("orders")
    .WithRadiusResourceGroup("orders")
    .WithReference(db);

#pragma warning restore ASPIRERADIUS005
```

Key behaviours:

- **Exactly one group per resource** — every routable resource and environment must resolve to a
  single group (`ASPIRERADIUS031`/`ASPIRERADIUS032`).
- **Cross-group references and environment targets** are emitted as fully-qualified UCP IDs; the
  union of these edges must be acyclic (`ASPIRERADIUS035`).
- **Cross-group environment targets** — a group without its own environment can deploy against
  another group's environment via `WithRadiusResourceGroup(group, environmentGroup)`
  (`ASPIRERADIUS034` if the target group owns no environment).
- **Per-group configuration** — providers, namespace, recipe packs, and recipe parameters compose
  per group into that group's environment.
- **No-group default is unchanged** — when no resource is routed to a group, publish/deploy behave
  byte-for-byte as before.

See the [Radius resource groups documentation](https://docs.radapp.io/guides/resources/)
for an end-to-end walkthrough.

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

// Reference an existing cluster Secret (external operator / hand-applied).
radius.WithSecretStore("tls-cert", RadiusSecretStoreType.Certificate, s =>
    s.FromExistingSecret("app/tls-cert", "tls.crt", "tls.key"));

// GitOps sealed secrets — the encrypted manifest is applied before rad deploy and awaited.
radius.WithSecretStore("db-creds", RadiusSecretStoreType.BasicAuthentication, s =>
    s.FromSealedSecret("./secrets/db-creds.sealed.yaml", "username", "password"));
```

- **Scope is implied by the API form**: `builder.AddRadiusSecretStore(...)` is application-scoped
  (`properties.application`); `radius.WithSecretStore(...)` is environment-scoped
  (`properties.environment`).
- **Encoding** defaults to `base64` for `certificate` stores and `raw` otherwise.
- **Sealed secrets** require `kubectl` on `PATH` and the Bitnami Sealed Secrets controller in the
  target cluster; the integration applies the already-encrypted manifest (it never runs
  `kubeseal`) and polls for the materialized `Secret` (default 120s, overridable via
  `WithMaterializationTimeout`).
- **Consume** a store from `recipeConfig` auth / `envSecrets` via
  `WithBicepRegistryAuthentication` / `WithTerraformGitAuthentication` /
  `WithTerraformProviderSecret` / `WithRecipeEnvironmentSecret`, referenced by the store's
  fully-qualified UCP secret-store ID.

## Reference

### Supported resources

* `AddContainer(...)` — published as a Radius container workload (`Radius.Compute/containers`).
* `AddProject<T>(...)` — published as a Radius container workload only when the project has a pre-built image attached with `WithContainerImage("<registry>/<image>:<tag>")`. Without one, `aspire publish` fails with a remediation message to build and push an image the cluster can pull.
* Selected resources with a Radius mapping (e.g. Redis, MongoDB, RabbitMQ, Dapr building blocks) emit Radius "legacy" types via the resource type mapper. Child database resources (for example `AddSqlServer("sql").AddDatabase("appdb")`) are collapsed onto the parent today.

Other Aspire resource types are not emitted; only the resources listed above appear in the generated Bicep.

### Diagnostics

The package uses the `ASPIRERADIUS` diagnostic prefix for two distinct mechanisms, with
disjoint numeric ranges reserved so the IDs never collide:

| Range | Mechanism | Surfaced as |
|-------|-----------|-------------|
| `ASPIRERADIUS001`–`ASPIRERADIUS009`, `ASPIRERADIUS057` | Compile-time analyzer diagnostics for experimental APIs (incl. `ASPIRERADIUS003` for the cloud-provider / cloud-managed surface `WithAzureProvider`/`WithAwsProvider`/`WithManagedResource`, `ASPIRERADIUS005` for `WithRadiusResourceGroup`, `ASPIRERADIUS006` for the secret-store APIs, and `ASPIRERADIUS057` for `WithContainerImage`) | `[Experimental]` warnings (suppressible), documented at `https://aka.ms/aspire/diagnostics/<id>` |
| `ASPIRERADIUS010`–`ASPIRERADIUS019` | Cloud-provider configuration errors | Thrown `InvalidOperationException` (message includes the ID) |
| `ASPIRERADIUS020`–`ASPIRERADIUS029` | Cloud-managed resource (`WithManagedResource`) and recipe/recipe-parameter validation | Thrown `ArgumentException` (config time) / `InvalidOperationException` (publish time) |
| `ASPIRERADIUS030`–`ASPIRERADIUS039` | Multi-resource-group routing (`WithRadiusResourceGroup`) validation | Thrown `ArgumentException` (call site, e.g. empty name) / `InvalidOperationException` (fail-fast gate before publish/deploy) |
| `ASPIRERADIUS040`–`ASPIRERADIUS055` | Secret-store (`AddRadiusSecretStore` / `WithSecretStore`) validation, publish, and deploy | Thrown `ArgumentException` (call site, e.g. empty/invalid name or key) / `InvalidOperationException` (fail-fast gate, publish, or deploy) |
| `ASPIRERADIUS056` | Bicep generation | Thrown `InvalidOperationException` (publish) |

Runtime validation codes:

| Code | When | Meaning |
|------|------|---------|
| `ASPIRERADIUS010` | Provider config | A cloud-provider credential callback did not select a credential. |
| `ASPIRERADIUS011` | Provider config | Conflicting cloud-provider credentials across environments sharing a Radius installation. |
| `ASPIRERADIUS020` | Publish | A resource is marked cloud-managed for a cloud whose provider is not configured on the environment. Validated at publish time, so provider/selection call order does not matter. |
| `ASPIRERADIUS022` | Config | A compute workload (project/container, or a resource overridden to the compute container type) cannot be marked cloud-managed. |
| `ASPIRERADIUS023` | Config | A cloud-managed recipe is missing its `RecipeLocation`. |
| `ASPIRERADIUS024` | Config | A child resource cannot be marked cloud-managed directly; mark its parent instead. |
| `ASPIRERADIUS025` | Config | The resource does not map to a supported Radius backing resource type. |
| `ASPIRERADIUS026` | Publish | Multiple instances of one user-defined (`Radius.*`) type resolve to different recipes or recipe parameters; Radius binds one recipe (with one parameter set) per type per environment. |
| `ASPIRERADIUS027` | Publish | A per-instance recipe name or parameters were set (via `PublishAsRadiusResource(c => c.Recipe...)`) on a native (`Radius.*`) type or native container; these are unsupported. Use `WithRecipeParameters(resourceType, ...)` on the environment instead. |
| `ASPIRERADIUS028` | Publish | Two recipe parameters bound to different Aspire parameters sanitize to the same Bicep identifier. Rename one so they produce distinct identifiers. |
| `ASPIRERADIUS031` | Publish/Deploy gate | A compute/backing resource or environment is not assigned to any Radius resource group (orphaned). Route it with `WithRadiusResourceGroup(...)`. |
| `ASPIRERADIUS032` | Publish/Deploy gate | A resource is ambiguously assigned: to more than one Radius resource group, or to a single group but more than one environment group; it must resolve to exactly one of each. Keep a single `WithRadiusResourceGroup(...)` call per resource (the last call wins if repeated). A route declared on a child resource (e.g. a database on its server) applies to its parent, so a parent/child (or child/child) group or environment-group disagreement trips this too. |
| `ASPIRERADIUS033` | Config (call site) / Publish/Deploy gate | A Radius resource-group name is not a valid single UCP/ARM segment. It must be 1-90 characters of ASCII letters, digits, `-`, `_`, or `.`, may not start or end with `.`, may not contain `..`, and may not be a reserved device name (e.g. `CON`, `NUL`). Names passed to `WithRadiusResourceGroup` are rejected at the call site; names set through internal annotations are rejected at the publish/deploy gate. The name is used verbatim as both the `groups/<group>/` artifact directory and a UCP-ID segment. |
| `ASPIRERADIUS034` | Publish/Deploy gate | A cross-group environment target names a group that has no Radius environment routed to it. Route a `RadiusEnvironmentResource` into that group, or correct the `environmentGroup` argument. |
| `ASPIRERADIUS035` | Publish/Deploy gate | The group dependency graph (cross-group references ∪ cross-group environment targets) contains a cycle. Break the cycle by removing a cross-group `WithReference` or environment target so the groups form a DAG. |
| `ASPIRERADIUS036` | Publish/Deploy gate | Resources in one Radius resource group resolve to more than one environment (a group that owns an environment also has a member targeting a different environment group, or a non-owning group's members disagree). All resources in a group must deploy against a single environment. |
| `ASPIRERADIUS037` | Publish/Deploy gate | A Radius resource group carries resources but resolves to no environment (neither the group nor its environment group owns one). Route a `RadiusEnvironmentResource` into the group, or target a group that owns one. |
| `ASPIRERADIUS038` | Publish/Deploy gate | Two Radius resource-group (or environment-group) names differ only by case. Radius resource-group names are case-insensitive, so they would collide server-side. Use a single consistent spelling. |
| `ASPIRERADIUS039` | Publish/Deploy gate | A legacy resource that declares a custom recipe (a custom recipe location, explicit recipe name, per-instance override, or managed recipe) lives in a group that deploys against an environment owned by another group. Legacy recipes are registered on the environment, so a local custom recipe would be lost. Register the recipe on the environment-owning group, or drop the cross-group environment target. |
| `ASPIRERADIUS040` | Publish/Deploy gate | A secret store is missing a key its `type` requires (`certificate` needs `tls.crt`/`tls.key`; `basicAuthentication` needs `username`/`password`; `azureWorkloadIdentity` needs `clientId`/`tenantId`; `awsIRSA` needs `roleARN`). |
| `ASPIRERADIUS041` | Publish/Deploy gate | A secret store declares more than one population mode (`WithData` / `FromExistingSecret` / `FromSealedSecret`) or none. Declare exactly one. |
| `ASPIRERADIUS042` | Publish/Deploy gate | An inline (`WithData`) secret key is bound to a non-secret `ParameterResource`. Use `builder.AddParameter(name, secret: true)`. |
| `ASPIRERADIUS043` | Publish/Deploy gate | A secret store declares a duplicate `data` key. (An empty/whitespace key is rejected at the call site with `ArgumentException`.) |
| `ASPIRERADIUS044` | Publish | A `FromSealedSecret(...)` manifest path does not exist or is unreadable. |
| `ASPIRERADIUS045` | Deploy | The `kubectl` client is not on `PATH`. Applying a `SealedSecret` manifest requires the kubectl client; install it and ensure it is on `PATH`. (A missing Sealed Secrets controller is not detected here — it surfaces as a materialization timeout, `ASPIRERADIUS046`.) |
| `ASPIRERADIUS046` | Deploy | The `Secret` a sealed store references never materialized within the timeout (controller not installed, wrong namespace from a `strict`-scoped seal, or decryption failure). Surfaced before `rad deploy`. |
| `ASPIRERADIUS047` | Publish/Deploy gate | An invalid `encoding` was set for the store type (e.g. `raw` on a `certificate` store, which Radius requires to be `base64`). |
| `ASPIRERADIUS048` | Publish/Deploy gate | Two secret stores map to the same Bicep identifier within the same scope (e.g. `db-creds` and `db.creds` both sanitize to `db_creds`). Rename one so they produce distinct identifiers. |
| `ASPIRERADIUS049` | Config (call site) | A secret-store name is not a valid resource-name segment. Use a name that starts with a letter and contains only letters, digits, `-`, `.`, or `_`. |
| `ASPIRERADIUS050` | Publish | A secret-store consumer (recipe environment secret, gateway TLS, etc.) references a store that is not emitted or resolvable from the consuming environment. Ensure the store is declared and routed into a reachable scope/group. |
| `ASPIRERADIUS051` | Publish/Deploy gate | A consumer references a store whose `type` is incompatible with the consumer kind (Bicep-registry and Terraform-Git-PAT auth require a `basicAuthentication` store; gateway TLS requires a `certificate` store). |
| `ASPIRERADIUS052` | Publish/Deploy gate | A recipe environment secret (`WithRecipeEnvironmentSecret`) references a key the store does not declare (enforced only when the store declares an explicit key set). |
| `ASPIRERADIUS053` | Publish/Deploy gate | A store is referenced as a Terraform provider secret (`WithTerraformProviderSecret`), which is not yet supported. Remove the call until provider-secret emission is modeled. |
| `ASPIRERADIUS054` | Config (call site) | An application-scoped store was used for gateway TLS (`WithTlsCertificate`), which requires an environment-scoped `certificate` store. Declare the store with `WithSecretStore` on an environment. |
| `ASPIRERADIUS055` | Publish/Deploy gate | An application-scoped `FromExistingSecret` store uses a bare `<name>` reference. Application-scoped stores have no owning environment to default the namespace from; use a fully-qualified `<namespace>/<name>` reference. |
| `ASPIRERADIUS056` | Publish | Two emitted constructs map to the same Bicep identifier (e.g. a resource named `app` or `recipepack` colliding with a synthesized construct, or two resource names that sanitize to the same identifier such as `my-x` and `my.x`). Bicep symbols share one flat namespace; rename the conflicting resource. |

> `ASPIRERADIUS021` was retired: the cloud is taken from the explicit `RadiusCloud` argument
> rather than inferred from the recipe location, so there is no cloud/recipe conflict to flag.

For native `Radius.*` types, recipe parameters are configured at the environment/type scope; per-instance recipe parameters are rejected with `ASPIRERADIUS027`, and environment/type-scoped values override the recipe's intrinsic parameters. The per-resource-over-type-over-environment precedence applies only to legacy `Applications.*` types.

### Known limitations

* For `ASPIRERADIUS011`, AWS access-key credential conflicts are compared by the Aspire parameter name that supplies the access-key ID, not by the resolved access-key value. Two environments that use different parameter names for the same key can be flagged as a false conflict, while the same parameter name with different values is not flagged.
* `WithTerraformProviderSecret` is not yet supported: the Radius `recipeConfig.terraform.providers.<name>` schema is an array of objects that the Bicep post-processor cannot currently emit, and the API does not capture the provider secret's name/key. Calls fail with `ASPIRERADIUS053` until provider-secret emission is modeled.
* `WithTlsCertificate` (gateway TLS) validates the store (must be an environment-scoped `certificate` store, `ASPIRERADIUS051`/`ASPIRERADIUS054`) and records the reference deterministically on the owning environment, but does not yet emit `tls.certificateFrom` wiring because no gateway resource type is modeled today. The recorded reference is a placeholder until gateways are modeled.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://docs.radapp.io/
* https://aspire.dev/

## Feedback & contributing

https://github.com/microsoft/aspire
