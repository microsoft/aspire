# Azure Monitor health models hosting integration

Use this integration to model, configure, and orchestrate Azure Monitor health-model publishing
and the managed Prometheus ingestion resources that supply its signals.

## Getting started

### Prerequisites

This is an experimental, in-repository integration. Reference its project from an AppHost while
developing; the new package is not yet available from public package feeds.

Publishing requires a version 1 `aspire-healthmodel.json` exported from Dashboard > Health,
the .NET SDK and Aspire CLI. It does not require Azure credentials or create Azure resources.
Deployment additionally requires an Azure subscription, a region supporting the current
CloudHealth API, Azure Container Apps, and permission to create resources and role assignments.

After the package is released, add it from the AppHost directory with:

```bash
aspire add Aspire.Hosting.Azure.HealthModels
```

## Usage example

Then, in the AppHost, add a health-model publishing resource and an explicitly selected metrics producer:

```csharp
#pragma warning disable ASPIREAZUREHEALTH001

var health = builder.AddAzureHealthModel("health", "aspire-healthmodel.json");

if (builder.ExecutionContext.IsPublishMode)
{
    builder.AddAzureContainerAppEnvironment("azure");

    var metrics = builder.AddProject<Projects.HealthMetrics>("health-metrics")
        .WithHttpEndpoint(name: "http", targetPort: 8080)
        .PublishAsAzureContainerApp((_, app) =>
        {
            app.Template.Scale.MinReplicas = 1;
            app.Template.Scale.MaxReplicas = 1;
        });

    builder.AddAzureContainerAppsHealthModelCollector(
        "health-collector", health, metrics.GetEndpoint("http"));
}
```

For a TypeScript AppHost with an existing metrics producer exposing an `http` endpoint, the exported
methods have the same resource/endpoint contract:

```typescript
const health = await builder.addAzureHealthModel("health", "aspire-healthmodel.json");
await builder.addAzureContainerAppsHealthModelCollector(
    "health-collector", health, metrics.getEndpoint("http"));
```

Configure the Container Apps environment and keep the metrics producer at one replica, as in the
C# example. The collector's endpoint references and identity values remain deferred expressions in the publish
artifacts. Its ingress is private, and it runs continuously at one replica. Do not expose the
metrics producer publicly merely to make it scrapeable.

The API is publish-only: ordinary local startup neither reads the definition nor provisions Azure.
Invalid files, stale AppHost resource bindings, and changed relationships fail publishing explicitly.
The application name and complete set of health-check bindings must match the definition.

## Published infrastructure

The health-model module generates:

- `Microsoft.CloudHealth/healthmodels` with a system-assigned query identity.
- The saved entities, directed relationships, canvas positions, impact and dependency policies.
- A reusable `PrometheusMetricsQuery` signal definition for every local signal and its assignment
  to the matching entity's Azure Monitor workspace signal group.
- An Azure Monitor workspace (`Microsoft.Monitor/accounts`), data collection endpoint, and
  data collection rule routing `Microsoft-PrometheusMetrics` to that workspace.
- A user-assigned collector identity with **Monitoring Metrics Publisher** scoped to the DCR.
- **Monitoring Reader** for the health model's identity, scoped to the workspace.

CloudHealth resources target `2026-09-01-preview`. Other Azure resource API versions are pinned
independently. Fractional saved coordinates and thresholds are preserved through Bicep `json()`;
they are not rounded to integer literals.

The model is a separate Azure resource, not a child of the workspace. Authentication uses managed
identity; no API keys or credential values are embedded in generated artifacts. Public-network
ingestion endpoints remain authenticated. Private-link configuration is outside this first version.

## Signal contract

The collector scrapes `/metrics` every 30 seconds. Producers report this Prometheus gauge:

```text
aspire_health_status{resource_name="api",health_check="ready",replica_index="1"} 2
```

`resource_name`, `health_check`, and `replica_index` must match the saved entity binding.
Values use .NET `HealthStatus`: **2 = Healthy, 1 = Degraded, 0 = Unhealthy**. A signal called
`resource-state` represents lifecycle health. It must not stand in for a readiness check.

The generated AHM rules use `< 1` for Unhealthy and `< 2` for Degraded, with Unhealthy taking
precedence. Queries select only values in the supported 0–2 range and require a sample newer
than three minutes. Missing, invalid and stale measurements produce no query result rather
than a fabricated healthy value. Signal evaluation refreshes once per minute.
Empty-result/Unknown transitions and portal coordinate anchoring still need service-level validation;
preserving the definition does not yet prove identical Azure rendering or every evaluation edge case.

An AppHost delegate is **not automatically executable in a deployed application**. The producer
must run the same underlying checks, or an explicitly equivalent validation, in the deployed
environment. The dedicated HealthModel playground demonstrates shared validation code between
its local AppHost and deployable metrics producer; its resources remain a simulation.

## Publish, deploy and cleanup

```powershell
aspire publish --apphost .\MyApp.AppHost.csproj --output-path .\publish-output --non-interactive
```

Review the Bicep, parameters and collector configuration before deployment. The integration uses
Aspire's existing Azure resource and Container Apps pipelines rather than executing Azure CLI
commands while constructing the app model.

`aspire deploy` applies those resources and may incur charges. Identity/RBAC propagation can delay
initial samples. Verify the workspace contains the expected gauge series and the AHM entity
states match the intended validation after deployment. Compilation of Bicep alone does not prove
that a tenant has the required provider availability or ingestion permissions.

The model, workspace, DCE/DCR and identities are owned by the generated Azure deployment.
Use the application's normal Azure teardown/resource-group cleanup workflow. Existing-workspace
reuse, sovereign-cloud endpoint selection and private-link provisioning are not yet exposed by this API.
Incremental deployments do not automatically delete child resources removed from the definition.
Use a fresh model or explicitly remove obsolete entities, relationships and signal definitions when
testing topology removals; deletion reconciliation is outside this first version.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/azure-monitor/health-models/overview
* https://learn.microsoft.com/azure/azure-monitor/health-models/tutorial-bicep
* https://learn.microsoft.com/azure/azure-monitor/metrics/prometheus-remote-write

## Feedback & contributing

https://github.com/microsoft/aspire
