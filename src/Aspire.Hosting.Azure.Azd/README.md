# Azure Developer CLI (azd) import hosting integration

Use this integration to adopt an existing [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/) project in an Aspire AppHost without rewriting your assets. It reads an existing `azure.yaml`, the selected `.azure` environment, and the project's `infra` folder, and projects the azd services and resources onto equivalent Aspire resources.

`builder.AddAzdProject(...)` is a **native, end-to-end integration**: the imported model runs locally (`aspire run`) on emulators and containers without touching Azure, and it publishes/deploys (`aspire publish` / `aspire deploy`) to the **same** subscription, region, and resource group your azd environment already records — binding to resources you marked `existing` instead of recreating them.

> [!IMPORTANT]
> This integration is **experimental**. The APIs are decorated with `[Experimental("ASPIREAZUREAZD001")]` and may change in a future release. Suppress the diagnostic (for example with `#pragma warning disable ASPIREAZUREAZD001` or a project-level `<NoWarn>` entry) to use them.

## Why

Teams that already use azd have meaningful assets: a hand-tuned `azure.yaml`, Bicep under `infra/`, and per-environment configuration under `.azure/`. Moving to Aspire should not mean throwing those away. This integration lets you point Aspire at an existing azd project so you can start orchestrating and extending it from an AppHost while keeping your existing infrastructure-as-code intact.

The direction is **azd → Aspire** (import/adopt). This integration does not emit `azure.yaml`.

`builder.AddAzdProject(...)` reads the azd assets when the AppHost starts and materializes equivalent Aspire resources in-memory. The result is a fully functional Aspire app that runs locally on emulators/containers and deploys to your existing Azure environment (see [Run and deploy natively](#run-and-deploy-natively)). `azure.yaml` stays the source of truth, and you can reference and customize any imported resource from your AppHost like any other Aspire resource.

## Getting started

### Prerequisites

- An existing azd project (a directory that contains an `azure.yaml` file).
- Azure subscription (only required when you eventually provision/deploy the imported resources).

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Azd` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Azd
```

## Run and deploy natively

Point the AppHost at the directory (or `azure.yaml` file) of your existing azd project. The call returns an `AzdImport` describing everything that was created, plus diagnostics for anything that needs follow-up:

```csharp
#pragma warning disable ASPIREAZUREAZD001

var builder = DistributedApplication.CreateBuilder(args);

// Resolve azure.yaml from the AppHost directory, or pass a path/directory explicitly.
var azd = builder.AddAzdProject("../../existing-azd-app");

// Surface anything that could not be imported automatically.
foreach (var diagnostic in azd.Diagnostics.Items)
{
    Console.WriteLine(diagnostic);
}

// Continue to customize imported resources as normal Aspire resources. GetService/GetResource
// return a reference immediately; if the name is not in azure.yaml the app fails when it runs or deploys.
var web = azd.GetService("web");
web.WithReplicas(2);

// Need the concrete resource type to call type-specific extension methods? Ask for it. The generic
// overloads re-wrap the loosely-typed import as a strongly-typed builder so it composes naturally.
azd.GetResource<AzureCosmosDBResource>("orders").AddCosmosDatabase("appdb");

builder.Build().Run();
```

That single call gives you a model that works in both directions:

- **`aspire run` (local development).** Resources that have a local development experience are switched to a container or emulator so nothing touches Azure: **Azure Cache for Redis** and **Azure Database for PostgreSQL** run as containers, and **Azure Storage**, **Cosmos DB**, **Service Bus**, and **Event Hubs** run as emulators. Resources without a local option (Key Vault, OpenAI, AI Search) continue to bind to Azure.
- **`aspire publish` / `aspire deploy` (the cloud).** Aspire generates the deployable Azure infrastructure (Bicep) for every mapped resource and targets the **same** subscription, region, and resource group recorded in your `.azure` environment — so a migrated app provisions into the place azd already used instead of standing up a parallel environment.
- **`existing: true` resources** are bound to the already-provisioned Azure resource (the annotation form of `AsExisting`) using the azd resource name and the environment's resource group, so neither run nor publish recreates something you already own.

These behaviors are on by default and can be tuned through `AzdImportOptions`:

```csharp
var azd = builder.AddAzdProject("../../existing-azd-app", options =>
{
    // Provision the real Azure resources locally instead of running emulators/containers.
    options.UseEmulatorsForLocalRun = false;

    // Provision a fresh copy instead of binding to resources marked `existing: true`.
    options.BindExistingResources = false;

    // Don't reuse the subscription/location/resource group from .azure for provisioning.
    options.ReuseAzureEnvironment = false;
});
```

## What gets imported

### Services (`services:`) → compute resources

| azd service shape | Aspire resource |
| --- | --- |
| `language: dotnet` (or a `project:` pointing at a `.csproj`/`.fsproj`) | `AddProject` |
| `host: function` with `language: dotnet` (a `.csproj`/`.fsproj`) | `AddAzureFunctionsProject` |
| `image:` (prebuilt container image) | `AddContainer` |
| `docker:` block, or any service with a `Dockerfile` | `AddDockerfile` |

Each service's `host:` selects a compute environment, created on demand:

| azd `host:` | Aspire compute environment |
| --- | --- |
| `containerapp` | `AddAzureContainerAppEnvironment` |
| `appservice` | `AddAzureAppServiceEnvironment` |
| `function` (with `language: dotnet`) | `AddAzureContainerAppEnvironment` — the service is imported as an `AddAzureFunctionsProject`. azd hosts Functions on `Microsoft.Web/sites`, but the Aspire Functions compute target is Azure Container Apps, so a diagnostic notes the substitution. |
| `function` (non-.NET), `aks`, `staticwebapp`, … | *Deferred* — reported as a diagnostic; the service is imported without a compute environment |

Service `env:` entries are applied with `WithEnvironment`, and `uses:` entries are wired with `WithReference`.

> [!NOTE]
> azd injects a fixed set of environment variables into a service for each `uses:` edge (for example `REDIS_HOST`/`REDIS_URL`, `AZURE_KEY_VAULT_ENDPOINT`, `POSTGRES_*`). Aspire's `WithReference` uses its own connection conventions, so the importer reports the azd variable names as a diagnostic. If your application code reads the azd names, add `WithEnvironment` to preserve them.

### Resources (`resources:`) → Azure integrations

The built-in mappers cover the resource types most commonly found in azd templates. The azd resource-type names below are taken from the [azd `azure.yaml` schema](https://github.com/Azure/azure-dev/blob/main/schemas/v1.0/azure.yaml.json).

| azd resource `type` | Aspire resource | Child resources |
| --- | --- | --- |
| `keyvault` | `AddAzureKeyVault` | — |
| `storage` | `AddAzureStorage` | `containers:` → blob containers |
| `db.redis` | `AddAzureManagedRedis` | — |
| `db.postgres` | `AddAzurePostgresFlexibleServer` | a database named after the resource |
| `db.cosmos` | `AddAzureCosmosDB` | a database named after the resource + `containers:` (with `partitionKeys`) |
| `messaging.servicebus` | `AddAzureServiceBus` | `queues:`, `topics:` |
| `messaging.eventhubs` | `AddAzureEventHubs` | `hubs:` |
| `ai.search` | `AddAzureSearch` | — |
| `ai.openai.model` | `AddAzureOpenAI` | `model:` (`name`/`version`) → a model deployment |
| `db.mysql`, `db.mongo`, `ai.project`, `host.*` | *Deferred* — reported as a diagnostic | — |

> [!NOTE]
> `db.redis` is a deliberate **SKU substitution**, not a 1:1 mapping. In azd, `db.redis` provisions [Azure Cache for Redis](https://learn.microsoft.com/azure/azure-cache-for-redis/) (`Microsoft.Cache/redis`), which Azure is [retiring](https://learn.microsoft.com/azure/azure-cache-for-redis/retirement-faq). Aspire maps it to **Azure Managed Redis** (`Microsoft.Cache/redisEnterprise`) — its supported successor — via `AddAzureManagedRedis`, and reports a warning. If you have an existing Azure Cache for Redis instance, confirm the successor SKU fits your migration, or register a custom `IAzdResourceMapper` to override the mapping.

### Preserved assets

- **`infra/` (Bicep)** is **referenced, not regenerated**. The path is exposed as `AzdImport.InfraPath`; continue to manage it with your existing deployment workflow. The provider is taken from `azure.yaml` or, when not pinned, detected from the folder's file extensions (as azd does). A **Terraform** `infra/` folder is preserved but flagged with a diagnostic, because Aspire provisions Bicep.
- **`.azure/<env>/.env` and `.azure/config.json`** are loaded into `AzdImport.Environment`, exposing the subscription, location, resource group, tenant, and principal recorded by azd. The default environment from `config.json` is used unless you set `AzdImportOptions.EnvironmentName`. These values also seed Azure provisioning: the subscription/location/resource group are applied to `AzureProvisionerOptions` (the `Azure:*` configuration) **and** to the `AzureEnvironmentResource` that Aspire adds for deployment (its `location`, `resourceGroupName`, and `principalId` parameters), so a grown-up app targets the same place on `aspire publish`/`aspire deploy` without re-prompting. Values you set yourself always win. Set `AzdImportOptions.ReuseAzureEnvironment = false` to opt out.
- A resource marked **`existing: true`** in `azure.yaml` is reference-only in azd. The importer binds it to the already-provisioned Azure resource automatically (the annotation form of `AsExisting`, using the azd resource name and the environment's resource group), so neither run nor publish creates a duplicate. Set `AzdImportOptions.BindExistingResources = false` to import it as a new resource instead.
- **azd expandable strings** (`${VAR}` / `$VAR`) in the project's `resourceGroup` and in a service's `env:` values are resolved against the selected `.azure` environment, exactly as azd does — so `resourceGroup: rg-${AZURE_ENV_NAME}` targets the same group azd would. A variable that isn't recorded in the environment is left as its literal `${VAR}` token rather than blanked, so an adopter's value is never silently emptied.

## Diagnostics

The import is **non-destructive and transparent**: anything that cannot be mapped (an unsupported host kind, an unknown resource type, a missing source path) is reported through `AzdImport.Diagnostics` instead of being silently dropped, so nothing is lost when you migrate.

```csharp
if (azd.Diagnostics.HasWarnings)
{
    foreach (var warning in azd.Diagnostics.Items.Where(d => d.Severity == AzdImportDiagnosticSeverity.Warning))
    {
        Console.WriteLine($"Needs attention: {warning}");
    }
}
```

## Known limitations

This integration is an early adoption aid, not a full azd replacement. In particular, a `uses:` edge is wired with Aspire's `WithReference` and the azd variable **names** are reported as a diagnostic, but the importer does **not** yet reproduce the exact azd variable **values** — azd assembles those from Key Vault secrets and Bicep outputs and adds managed-identity role assignments. Until that lands, assemble any azd-shaped connection values you still need with `WithEnvironment`. The following are also deferred (each surfaced as a diagnostic, never silently dropped): non-ACA host kinds, Terraform provisioning, hooks, pipelines/workflows/state/platform/cloud, `${AZURE_*}` parameter substitution, resource-level `uses`, and the `db.mysql`/`db.mongo`/`ai.project`/`host.*` resource types.

## Extending the mapping

Supply a custom `IAzdResourceMapper` to support a resource type the built-in mappers do not cover, or to override the default mapping for a type. Mappers are consulted in order, before the built-ins:

```csharp
public sealed class MyAccountMapper : IAzdResourceMapper
{
    public bool CanMap(AzdResourceMapContext context)
        => context.Resource.Type == "db.mysql";

    public IResourceBuilder<IResource>? Map(AzdResourceMapContext context)
        => context.Builder.AddMySql(context.ResourceName);
}

var azd = builder.AddAzdProject("../../existing-azd-app", options =>
{
    options.ResourceMappers.Add(new MyAccountMapper());

    // Bind imported services to a compute environment you already declared instead of creating new ones.
    options.CreateComputeEnvironments = false;

    // Import a specific azd environment rather than the default from .azure/config.json.
    options.EnvironmentName = "prod";
});
```

## Additional documentation

* https://aspire.dev/integrations/gallery/

## Feedback & contributing

https://github.com/microsoft/aspire
