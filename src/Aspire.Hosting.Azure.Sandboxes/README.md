# Azure Container Apps Sandboxes hosting integration

Use this integration to deploy container-backed Aspire compute resources to Azure Container Apps Sandboxes.

## Getting started

### Prerequisites

* An Azure subscription and region with Azure Container Apps Sandboxes preview access.
* Permission to create sandbox groups, Azure Container Registry resources, and scoped role assignments.
* Docker or Podman for building and inspecting Linux/amd64 OCI images.

The integration grants the deployment identity the **Container Apps SandboxGroup Data Owner** role on a sandbox group that it provisions. When using an existing sandbox group, grant that role to the deployment identity before deploying.

### Install the package

In your AppHost project, install the Azure Container Apps Sandboxes hosting integration:

```bash
aspire add Aspire.Hosting.Azure.Sandboxes
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add an Azure sandbox group and publish a compute resource to it using the following methods:

```csharp
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

Endpoints are not exposed unless they are marked external. External endpoints require an explicit `Anonymous = true` opt-in for anonymous access. Sandbox egress is configured with full inspection and deny-by-default behavior.

Images are resolved to immutable Linux/amd64 digests before import. Deployment state stores sandbox, disk-image, and endpoint identifiers, but does not persist registry credentials.

Duration options use `TimeSpan` in C#. Generated TypeScript SDKs represent `TimeSpan` values as .NET ticks, where one second is `10_000_000`.

## Deployment architecture

Sandbox groups are ARM resources, but sandbox instances, disk images, ports, and lifecycle settings are currently exposed only through the regional Azure Dev Compute preview data plane. Aspire therefore performs sandbox deployment in-process rather than through an ARM deployment script. This lets the deployment pipeline inspect local container images, resolve and validate immutable Linux/amd64 digests, report polling progress, persist deployment state, retain a previous endpoint generation during updates, and clean up stale or failed data-plane resources.

This design means Aspire owns retry, polling, state recovery, and cleanup behavior while the preview data-plane contract evolves. The implementation is intentionally isolated in the Sandboxes integration and should be reevaluated when Azure provides a stable ARM resource or deployment primitive for these operations.

## Preview limitations

The package and service are preview features. The current integration does not support:

* Connector Gateway, MCP, triggers, or OAuth flows.
* Volumes, snapshots, shell/file APIs, or interactive lifecycle commands.
* TCP ports, private service discovery, or cross-group endpoint references.
* Windows, ARM64, or arbitrary registry credentials.
* Runtime sandbox URLs as first-pass ARM/Bicep inputs.

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings to be available via .NET configuration. The Aspire dashboard will prompt you to set these values if they are not already configured. See [Local Azure Provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/) for more details.

> NOTE: Developers must have Owner access to the target subscription so that role assignments can be configured for the provisioned resources.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/container-apps/sandboxes-overview

## Feedback & contributing

https://github.com/microsoft/aspire
