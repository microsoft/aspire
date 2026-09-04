# Azure Container Apps Sandboxes hosting integration

Use this integration to model, configure, and deploy container-backed Aspire compute resources to Azure Container Apps Sandboxes.

## Getting started

### Prerequisites

* An Azure subscription and region with Azure Container Apps Sandboxes preview access.
* Permission to create sandbox groups, Azure Container Registry resources, and scoped role assignments.
* Docker or Podman for building and inspecting Linux/amd64 OCI images.

The integration grants the deployment identity the **Container Apps SandboxGroup Data Owner** role on a sandbox group that it provisions. When using an existing sandbox group, grant that role to the deployment identity before deploying.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Sandboxes` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Sandboxes
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add an Azure sandbox group and publish a compute resource to it using the following methods:

```csharp
#pragma warning disable ASPIREAZURE001 // Azure Container Apps Sandboxes APIs are experimental.

var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");

builder.AddProject<Projects.ApiService>("api")
    .WithHttpEndpoint(name: "http", targetPort: 8080)
    .WithExternalHttpEndpoints()
    .PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
    {
        Tier = AzureSandboxTier.Medium,
        AutoSuspendEnabled = true,
        AutoSuspendInterval = TimeSpan.FromMinutes(15),
        AutoSuspendMode = AzureSandboxAutoSuspendMode.Disk,
        Endpoints =
        [
            new AzureSandboxEndpointOptions
            {
                Name = "http",
                Anonymous = true
            }
        ]
    });
```

The same APIs are available to TypeScript AppHosts:

```typescript
import {
    AzureSandboxAutoSuspendMode,
    AzureSandboxTier,
    createBuilder
} from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const sandboxGroup = await builder.addAzureSandboxGroup("sandboxes");

const api = await builder
    .addContainer("api", "nginx", "alpine")
    .withHttpEndpoint({ name: "http", targetPort: 80 })
    .withExternalHttpEndpoints();

await api.publishAsAzureSandbox(sandboxGroup, {
    tier: AzureSandboxTier.Medium,
    autoSuspendEnabled: true,
    autoSuspendInterval: 900_000,
    autoSuspendMode: AzureSandboxAutoSuspendMode.Disk,
    endpoints: [{ name: "http", anonymous: true }]
});

await builder.build().run();
```

Endpoints are not exposed unless they are marked external. External endpoints require an explicit `Anonymous = true` opt-in for anonymous access. Sandbox egress is configured with full inspection and deny-by-default behavior.

Images are resolved to immutable Linux/amd64 digests before import. Images hosted by the configured Azure Container Registry are imported with a dedicated user-assigned identity that has `AcrPull`; public registry images are imported without that ACR identity. Deployment state stores sandbox, disk-image, endpoint, and endpoint-security metadata, but does not persist registry credentials. Stable ownership labels are derived from the AppHost and Azure deployment scope so a later deploy or destroy can find resources after `--clear-cache`; the scope and application identity remain part of the label to prevent resource-name-only sweeping across apps.

Duration options use `TimeSpan` in C#. Generated TypeScript SDKs represent `TimeSpan` values as milliseconds, where one second is `1_000`.

## Deployment architecture

Sandbox groups are ARM resources, but sandbox instances, disk images, ports, and lifecycle settings are currently exposed only through the regional Azure Dev Compute preview data plane. Aspire therefore performs sandbox deployment in-process rather than through an ARM deployment script. This lets the deployment pipeline inspect local container images, resolve and validate immutable Linux/amd64 digests, report polling progress, persist deployment state, retain a previous endpoint generation during updates, and clean up stale or failed data-plane resources.

This design means Aspire owns retry, polling, state recovery, and cleanup behavior while the preview data-plane contract evolves. The implementation is intentionally isolated in the Sandboxes integration and should be reevaluated when Azure provides a stable ARM resource or deployment primitive for these operations.

To keep endpoint references usable during an ordinary redeploy of the same immutable image and endpoint policy, Aspire can retain the immediately previous sandbox generation until the next successful deployment. If the image digest, endpoint exposure, protocol, or anonymous-access configuration changes, the previous generation is pruned immediately instead so an older workload or security posture does not remain reachable. Ordinary stale-generation pruning is best-effort after the new deployment state is safely persisted. A failure to prune after a security-relevant change fails the deployment visibly while preserving the new deployment and its state for recovery.

## Publish, deploy, and destroy behavior

* `aspire publish` emits reviewable Bicep for the sandbox group, registry, managed identities, and role assignments. Sandbox instances, disk images, ports, and data-plane URLs are deploy-time resources and are not created by publish.
* `aspire deploy` provisions the ARM resources, builds or resolves the workload image to an immutable Linux/amd64 digest, creates the ADC disk image and sandbox, configures lifecycle and ports, and records IDs, URLs, ownership, scope, and security metadata in deployment state. Public URLs are shown in the deployment summary.
* `aspire destroy` removes the current and labeled retained sandbox generations and disk images before Azure resource-group cleanup. Stable ownership labels allow cleanup after deployment state is cleared when the same AppHost and Azure sandbox group scope are still configured.
* Existing sandbox groups use the subscription, resource group, location, and name from the group's actual Azure outputs rather than the ambient deployment resource group.

## Preview limitations

The package and service are preview features. The current integration does not support:

* Volumes, snapshots, shell/file APIs, or interactive lifecycle commands.
* TCP ports, private service discovery, or cross-group endpoint references.
* Windows, ARM64, or arbitrary registry credentials.
* Runtime sandbox URLs as first-pass ARM/Bicep inputs.

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings to be available via .NET configuration. The Aspire dashboard will prompt you to set these values if they are not already configured. See [Local Azure Provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/) for more details.

> NOTE: Developers must have Owner access to the target subscription so that role assignments can be configured for the provisioned resources.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://sandboxes.azure.com/docs/sandboxes/quickstart/setup-portal

## Feedback & contributing

https://github.com/microsoft/aspire
