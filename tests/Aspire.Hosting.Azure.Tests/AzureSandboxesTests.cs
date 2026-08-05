// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREAZURE003

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Utils;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSandboxesTests
{
    [Fact]
    public async Task AddAzureConnectorGatewayResourcesGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorGateway("gateway");
        var connection = gateway.AddConnection(
            "office365",
            "office365",
            new AzureConnectorGatewayConnectionOptions
            {
                ConnectionName = "office365-outlook",
                DisplayName = "Office 365 Outlook"
            });
        connection.WithAccessPolicy(
            "worker-access",
            new AzureConnectorGatewayAccessPolicyOptions
            {
                PolicyName = "worker-acl",
                ObjectId = "11111111-1111-1111-1111-111111111111",
                TenantId = "22222222-2222-2222-2222-222222222222"
            });
        connection.WithIdentityAccessPolicy(
            "worker-identity-access",
            builder.AddAzureUserAssignedIdentity("worker-identity"),
            policyName: "worker-identity-acl");
        var mcp = gateway.AddMcpServerConfig(
            "outlook-mcp",
            new AzureConnectorGatewayMcpServerConfigOptions
            {
                ConfigName = "outlook-tools",
                Description = "Allow-listed Outlook tools."
            });
        mcp.WithConnector(
            "office365",
            connection,
            new AzureConnectorGatewayMcpConnectorOptions
            {
                DisplayName = "Office 365 Outlook",
                Description = "Read-only Outlook operations.",
                Operations =
                [
                    new AzureConnectorGatewayMcpOperationOptions
                    {
                        Name = "GetEmailsV3",
                        DisplayName = "Get emails",
                        Description = "Reads recent emails."
                    }
                ]
            });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Same(gateway.Resource, connection.Resource.Parent);
        Assert.Same(gateway.Resource, mcp.Resource.Parent);
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, gateway.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ExistingConnectorGatewayChildrenGenerateExistingBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorGateway("gateway")
            .PublishAsExisting("existing-gateway", "existing-rg");
        gateway.AddConnection("office365", "office365", new AzureConnectorGatewayConnectionOptions
        {
            ConnectionName = "existing-connection"
        }).AsExisting();
        gateway.AddMcpServerConfig("mcp", new AzureConnectorGatewayMcpServerConfigOptions
        {
            ConfigName = "existing-mcp"
        }).AsExisting();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, gateway.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddConnectorTriggerConfigSecuresSandboxCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorGateway("gateway");
        var connection = gateway.AddConnection("sharepoint", "sharepointonline");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var listener = builder.AddContainer("listener", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup);
        var trigger = connection.AddTriggerConfig(
            "new-file",
            "GetOnNewFileItems",
            listener.GetEndpoint("http"),
            new AzureConnectorGatewayTriggerOptions
            {
                TriggerName = "sharepoint-new-file",
                CallbackPath = "/webhook",
                Description = "Posts new SharePoint files to the sandbox.",
                Parameters =
                [
                    new AzureConnectorGatewayTriggerParameter
                    {
                        Name = "dataset",
                        Value = "https://contoso.sharepoint.com/sites/demo"
                    },
                    new AzureConnectorGatewayTriggerParameter
                    {
                        Name = "table",
                        Value = "Documents"
                    }
                ]
            });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var accessPolicy = Assert.Single(connection.Resource.AccessPolicies);
        Assert.True(accessPolicy.UsesGatewayManagedIdentity);
        Assert.Equal("gateway-acl", accessPolicy.PolicyName);
        Assert.Equal("webhook", trigger.Resource.CallbackPath);

        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(
            listener.Resource.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        var endpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.False(endpoint.Anonymous);
        Assert.Equal(gateway.Resource, Assert.Single(endpoint.AuthorizedConnectorGateways));

        var triggerSteps = await CreateStepsAsync(app, trigger.Resource);
        var triggerStep = Assert.Single(triggerSteps);
        Assert.Equal("provision-new-file", triggerStep.Name);
        Assert.Contains("deploy-listener-sandbox-container", triggerStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, triggerStep.RequiredBySteps);

        var (gatewayManifest, gatewayBicep) = await AzureManifestUtils.GetManifestWithBicep(model, gateway.Resource);
        var triggerBicep = trigger.Resource.GetBicepTemplateString();

        await Verify(gatewayManifest.ToString(), "json")
            .AppendContentAsFile(gatewayBicep, "bicep")
            .AppendContentAsFile(triggerBicep, "bicep");
    }

    [Fact]
    public async Task ConnectorTriggerDoesNotCreateDeployStepInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var gateway = builder.AddAzureConnectorGateway("gateway");
        var connection = gateway.AddConnection("office365", "office365");
        var listener = builder.AddContainer("listener", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints();
        var trigger = connection.AddTriggerConfig("new-email", "OnNewEmailV3", listener.GetEndpoint("http"));

        using var app = builder.Build();

        Assert.Empty(await CreateStepsAsync(app, trigger.Resource, DistributedApplicationOperation.Run));
    }

    [Fact]
    public void ConnectorTriggerRejectsAnonymousSandboxEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorGateway("gateway");
        var connection = gateway.AddConnection("office365", "office365");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var listener = builder.AddContainer("listener", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
            {
                Endpoints =
                [
                    new AzureSandboxEndpointOptions
                    {
                        Name = "http",
                        Anonymous = true
                    }
                ]
            });
        connection.AddTriggerConfig("new-email", "OnNewEmailV3", listener.GetEndpoint("http"));

        using var app = builder.Build();
        var sandboxContainer = new AzureSandboxContainerResource(
            "listener-sandbox-container",
            listener.Resource,
            sandboxGroup.Resource,
            autoSuspend: false);

        var exception = Assert.Throws<InvalidOperationException>(
            () => AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal(
            "Endpoint 'http' on resource 'listener' is a Connector Namespace trigger callback and cannot allow anonymous access.",
            exception.Message);
    }

    [Fact]
    public void ManagedMcpServerRequiresExplicitOperationAllowList()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorGateway("gateway");
        var connection = gateway.AddConnection("office365", "office365");
        var mcp = gateway.AddMcpServerConfig("outlook-mcp");

        var exception = Assert.Throws<ArgumentException>(() => mcp.WithConnector(
            "office365",
            connection,
            new AzureConnectorGatewayMcpConnectorOptions()));

        Assert.Equal("At least one connector operation must be explicitly allow-listed. (Parameter 'options')", exception.Message);
    }

    [Fact]
    public void ConnectorTriggerRequiresExternalEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorGateway("gateway")
            .AddConnection("office365", "office365");
        var listener = builder.AddContainer("listener", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080);

        var exception = Assert.Throws<InvalidOperationException>(
            () => connection.AddTriggerConfig("new-email", "OnNewEmailV3", listener.GetEndpoint("http")));

        Assert.Equal(
            "Connector trigger callback endpoint 'http' on resource 'listener' must be external.",
            exception.Message);
    }

    [Fact]
    public void ConnectorTriggerRejectsDuplicateParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorGateway("gateway")
            .AddConnection("office365", "office365");
        var listener = builder.AddContainer("listener", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints();

        var exception = Assert.Throws<ArgumentException>(() => connection.AddTriggerConfig(
            "new-email",
            "OnNewEmailV3",
            listener.GetEndpoint("http"),
            new AzureConnectorGatewayTriggerOptions
            {
                Parameters =
                [
                    new AzureConnectorGatewayTriggerParameter { Name = "folderPath", Value = "Inbox" },
                    new AzureConnectorGatewayTriggerParameter { Name = "folderPath", Value = "Archive" }
                ]
            }));

        Assert.Equal(
            "Trigger parameter 'folderPath' is configured more than once. (Parameter 'parameters')",
            exception.Message);
    }

    [Fact]
    public void ConnectorTriggerRequiresUniquePhysicalNameWithinNamespace()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorGateway("gateway");
        var outlook = gateway.AddConnection("outlook", "office365");
        var sharepoint = gateway.AddConnection("sharepoint", "sharepointonline");
        var listener = builder.AddContainer("listener", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints();
        outlook.AddTriggerConfig(
            "new-email",
            "OnNewEmailV3",
            listener.GetEndpoint("http"),
            new AzureConnectorGatewayTriggerOptions { TriggerName = "shared-trigger" });

        var exception = Assert.Throws<InvalidOperationException>(() => sharepoint.AddTriggerConfig(
            "new-file",
            "GetOnNewFileItems",
            listener.GetEndpoint("http"),
            new AzureConnectorGatewayTriggerOptions { TriggerName = "shared-trigger" }));

        Assert.Equal(
            "Trigger configuration 'shared-trigger' is already registered on Connector Namespace 'gateway'.",
            exception.Message);
        Assert.Equal("new-email", Assert.Single(gateway.Resource.TriggerConfigs).Name);
        Assert.Empty(sharepoint.Resource.AccessPolicies);
    }

    [Fact]
    public void ExistingConnectorConnectionRejectsImplicitTriggerAccessPolicy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorGateway("gateway")
            .AddConnection("office365", "office365")
            .AsExisting();
        var listener = builder.AddContainer("listener", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints();

        var exception = Assert.Throws<InvalidOperationException>(
            () => connection.AddTriggerConfig("new-email", "OnNewEmailV3", listener.GetEndpoint("http")));

        Assert.Equal(
            "Existing connector connection 'office365' is read-only and cannot create a trigger because trigger provisioning requires a new connection access policy.",
            exception.Message);
        Assert.Empty(connection.Resource.AccessPolicies);
        Assert.Empty(listener.Resource.Annotations.OfType<AzureConnectorGatewayEndpointAuthorizationAnnotation>());
        Assert.Empty(connection.Resource.Parent.TriggerConfigs);
    }

    [Fact]
    public async Task AddAzureSandboxResourcesGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var hostIdentity = builder.AddAzureUserAssignedIdentity("hostmi");
        var hostGroup = builder.AddAzureSandboxGroup("hostgroup")
            .WithUserAssignedIdentity(hostIdentity);
        var workerGroup = builder.AddAzureSandboxGroup("workergroup");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var (hostGroupManifest, hostGroupBicep) = await AzureManifestUtils.GetManifestWithBicep(model, hostGroup.Resource);
        var (workerGroupManifest, workerGroupBicep) = await AzureManifestUtils.GetManifestWithBicep(model, workerGroup.Resource);

        await Verify(hostGroupManifest.ToString(), "json")
            .AppendContentAsFile(hostGroupBicep, "bicep")
            .AppendContentAsFile(workerGroupManifest.ToString(), "json")
            .AppendContentAsFile(workerGroupBicep, "bicep");
    }

    [Fact]
    public async Task AddAzureSandboxGroupSupportsExplicitManagedIdentities()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var identity = builder.AddAzureUserAssignedIdentity("nodeidentity");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .WithUserAssignedIdentity(identity);

        builder.AddContainer("node", "node", "22-alpine")
            .WithAzureUserAssignedIdentity(identity)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
    }

    [Fact]
    public async Task ExistingAzureSandboxGroupDoesNotAddDeploymentPrincipalRoleAssignment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .PublishAsExisting("existing-sandboxes", "existing-rg");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (_, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, sandboxGroup.Resource);

        Assert.DoesNotContain("roleAssignments", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("Container Apps SandboxGroup Data Owner", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishAsAzureSandboxDoesNotAddDeploymentTargetInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var container = builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0");
        var configureCalled = false;
        var buildOptionsCallbackCount = container.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count();

        container.PublishAsAzureSandbox(sandboxGroup, options => configureCalled = true);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");

        Assert.Null(computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource));
        Assert.False(configureCalled);
        Assert.Equal(buildOptionsCallbackCount, container.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count());
    }

    [Fact]
    public void ContainerImageMetadataBuildsSandboxEntrypointFromImageConfig()
    {
        var metadata = AzureSandboxContainerDeployment.ParseContainerImageMetadata(
            """
            {
              "Entrypoint": ["dotnet", "/app/yarp.dll"],
              "Cmd": null,
              "WorkingDir": "/app",
              "Env": [
                "PATH=/usr/local/bin:/usr/bin:/bin",
                "ASPNETCORE_URLS=http://+:5000",
                "EMPTY="
              ]
            }
            """,
            "example.azurecr.io/site:tag");

        Assert.Equal(["dotnet", "/app/yarp.dll"], metadata.Entrypoint);
        Assert.Empty(metadata.Command);
        Assert.Equal("/usr/local/bin:/usr/bin:/bin", metadata.EnvironmentVariables["PATH"]);
        Assert.Equal("http://+:5000", metadata.EnvironmentVariables["ASPNETCORE_URLS"]);
        Assert.Equal(string.Empty, metadata.EnvironmentVariables["EMPTY"]);
        Assert.Equal("/app", metadata.WorkingDirectory);
    }

    [Fact]
    public async Task AzureDevComputeClientCreatesDiskImageWithRegistryCredentials()
    {
        var credential = new RecordingTokenCredential();
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("management.westus3.azuredevcompute.io", request.RequestUri?.Host);
            Assert.Equal("/subscriptions/sub/resourceGroups/rg/sandboxGroups/sg/diskimages", request.RequestUri?.AbsolutePath);
            Assert.Equal("?api-version=2026-02-01-preview", request.RequestUri?.Query);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);

            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("site-1234", root.GetProperty("name").GetString());
            Assert.Equal("site-container", root.GetProperty("labels").GetProperty("aspire-resource").GetString());
            Assert.Equal("example.azurecr.io/site:tag", root.GetProperty("image").GetProperty("base").GetString());
            Assert.Equal("00000000-0000-0000-0000-000000000000", root.GetProperty("registryCredentials").GetProperty("username").GetString());
            Assert.Equal("refresh-token", root.GetProperty("registryCredentials").GetProperty("token").GetString());

            return JsonResponse(
                """
                {
                  "id": "disk-1",
                  "labels": {},
                  "image": { "base": "example.azurecr.io/site:tag" },
                  "status": { "state": "Ready", "createdAt": "2026-06-03T00:00:00Z", "updatedAt": "2026-06-03T00:00:00Z" }
                }
                """);
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), credential, NullLogger.Instance);

        var diskImage = await client.CreateDiskImageAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            new AzureDevComputeCreateDiskImageRequest
            {
                Name = "site-1234",
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["aspire-resource"] = "site-container"
                },
                Image = new AzureDevComputeDiskImageSpec
                {
                    Base = "example.azurecr.io/site:tag"
                },
                RegistryCredentials = new AzureDevComputeRegistryCredentials
                {
                    Username = "00000000-0000-0000-0000-000000000000",
                    Token = "refresh-token"
                }
            },
            CancellationToken.None);

        Assert.Equal("disk-1", diskImage.Id);
        Assert.Equal([AzureDevComputeClient.AuthorizationScope], credential.Scopes);
    }

    [Fact]
    public async Task AzureDevComputeClientListsSandboxResourcesWithLabelSelector()
    {
        var requestCount = 0;
        var handler = new RecordingHandler(request =>
        {
            requestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("management.westus3.azuredevcompute.io", request.RequestUri?.Host);
            Assert.Contains("Page=1", request.RequestUri?.Query, StringComparison.Ordinal);
            Assert.Contains("PageSize=100", request.RequestUri?.Query, StringComparison.Ordinal);
            Assert.Contains("labels=aspire-resource%3Dsite-container", request.RequestUri?.Query, StringComparison.Ordinal);
            Assert.Contains("api-version=2026-02-01-preview", request.RequestUri?.Query, StringComparison.Ordinal);

            if (request.RequestUri?.AbsolutePath.EndsWith("/sandboxes", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(JsonResponse(
                    """
                    [
                      {
                        "id": "sandbox-1",
                        "labels": { "aspire-resource": "site-container" },
                        "ports": []
                      }
                    ]
                    """));
            }

            Assert.EndsWith("/diskimages", request.RequestUri?.AbsolutePath);
            return Task.FromResult(JsonResponse(
                """
                [
                  {
                    "id": "disk-1",
                    "labels": { "aspire-resource": "site-container" },
                    "status": { "state": "Ready" }
                  }
                ]
                """));
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);
        var scope = new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3");

        var sandboxes = await client.ListSandboxesAsync(scope, "aspire-resource=site-container", CancellationToken.None);
        var diskImages = await client.ListDiskImagesAsync(scope, "aspire-resource=site-container", CancellationToken.None);

        Assert.Equal("sandbox-1", Assert.Single(sandboxes).Id);
        Assert.Equal("disk-1", Assert.Single(diskImages).Id);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task AzureDevComputeClientPaginatesSandboxResources()
    {
        var requestedPages = new List<int>();
        var handler = new RecordingHandler(request =>
        {
            var page = request.RequestUri!.Query.Contains("Page=1", StringComparison.Ordinal) ? 1 : 2;
            requestedPages.Add(page);

            var count = page == 1 ? 100 : 1;
            var response = Enumerable.Range(0, count)
                .Select(index => new
                {
                    id = $"sandbox-{page}-{index}",
                    labels = new Dictionary<string, string>(),
                    ports = Array.Empty<object>()
                });
            return Task.FromResult(JsonResponse(JsonSerializer.Serialize(response)));
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);

        var sandboxes = await client.ListSandboxesAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            labels: null,
            CancellationToken.None);

        Assert.Equal(101, sandboxes.Count);
        Assert.Equal([1, 2], requestedPages);
    }

    [Fact]
    public void LabeledDeploymentCleanupKeepsCurrentAndPreviousGenerations()
    {
        var excludedDeployIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "current-deploy",
            "previous-deploy"
        };
        var excludedResourceIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "current-id",
            "previous-id"
        };

        Assert.False(AzureSandboxContainerDeployment.ShouldDeleteLabeledDeployment(
            "current-id",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aspire-owner"] = "owner-1",
                ["aspire-resource"] = "frontend-sandbox-container",
                ["aspire-deploy"] = "current-deploy"
            },
            "owner-1",
            "frontend-sandbox-container",
            excludedDeployIds,
            excludedResourceIds));
        Assert.False(AzureSandboxContainerDeployment.ShouldDeleteLabeledDeployment(
            "previous-id",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aspire-owner"] = "owner-1",
                ["aspire-resource"] = "frontend-sandbox-container",
                ["aspire-deploy"] = "previous-deploy"
            },
            "owner-1",
            "frontend-sandbox-container",
            excludedDeployIds,
            excludedResourceIds));
        Assert.False(AzureSandboxContainerDeployment.ShouldDeleteLabeledDeployment(
            "unrelated-id",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aspire-owner"] = "owner-1",
                ["aspire-resource"] = "backend-sandbox-container",
                ["aspire-deploy"] = "old-deploy"
            },
            "owner-1",
            "frontend-sandbox-container",
            excludedDeployIds,
            excludedResourceIds));
        Assert.False(AzureSandboxContainerDeployment.ShouldDeleteLabeledDeployment(
            "other-owner-id",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aspire-owner"] = "owner-2",
                ["aspire-resource"] = "frontend-sandbox-container",
                ["aspire-deploy"] = "old-deploy"
            },
            "owner-1",
            "frontend-sandbox-container",
            excludedDeployIds,
            excludedResourceIds));
        Assert.True(AzureSandboxContainerDeployment.ShouldDeleteLabeledDeployment(
            "old-id",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aspire-owner"] = "owner-1",
                ["aspire-resource"] = "frontend-sandbox-container",
                ["aspire-deploy"] = "old-deploy"
            },
            "owner-1",
            "frontend-sandbox-container",
            excludedDeployIds,
            excludedResourceIds));

        Assert.Equal(
            "aspire-owner=owner-1,aspire-resource=frontend-sandbox-container",
            AzureSandboxContainerDeployment.CreateLabelSelector("owner-1", "frontend-sandbox-container"));
    }

    [Fact]
    public void SandboxUrlSummaryIncludesRetainedUrlWhenDifferent()
    {
        var currentUrl = "https://current--8080.westus3.adcproxy.io/";
        var retainedUrl = "https://previous--8080.westus3.adcproxy.io/";

        Assert.Equal(
            $"Current: [{currentUrl}]({currentUrl}); retained for references configured before sandbox deployment: [{retainedUrl}]({retainedUrl})",
            AzureSandboxContainerDeployment.CreateSandboxUrlSummary(currentUrl, retainedUrl));
        Assert.Equal(
            $"[{currentUrl}]({currentUrl})",
            AzureSandboxContainerDeployment.CreateSandboxUrlSummary(currentUrl, currentUrl));
        Assert.Equal(
            $"[{currentUrl}]({currentUrl})",
            AzureSandboxContainerDeployment.CreateSandboxUrlSummary(currentUrl, retainedUrl: null));
    }

    [Fact]
    public void SandboxDeploymentStateTracksOwnerOnlyRecoveryState()
    {
        var ownerOnlyState = new DeploymentStateSection(
            "AzureSandboxes:frontend",
            new JsonObject { ["OwnerId"] = "owner-1" },
            version: 0);
        var emptyState = new DeploymentStateSection(
            "AzureSandboxes:backend",
            new JsonObject(),
            version: 0);

        Assert.True(AzureSandboxContainerDeployment.HasRemoteDeploymentState(ownerOnlyState));
        Assert.False(AzureSandboxContainerDeployment.HasRemoteDeploymentState(emptyState));
    }

    [Fact]
    public void SandboxDeploymentRejectsScopeChangesWhileStateExists()
    {
        var state = new DeploymentStateSection(
            "AzureSandboxes:frontend",
            new JsonObject
            {
                ["OwnerId"] = "owner-1",
                ["SubscriptionId"] = "sub-1",
                ["ResourceGroup"] = "rg-1",
                ["Location"] = "westus3",
                ["SandboxGroup"] = "sandboxes-1"
            },
            version: 0);

        AzureSandboxContainerDeployment.ValidateDeploymentScope(
            state,
            new AzureDevComputeResourceScope("SUB-1", "RG-1", "SANDBOXES-1", "WESTUS3"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AzureSandboxContainerDeployment.ValidateDeploymentScope(
                state,
                new AzureDevComputeResourceScope("sub-1", "rg-1", "sandboxes-2", "westus3")));

        Assert.Contains("aspire destroy", exception.Message);
    }

    [Fact]
    public async Task AzureDevComputeClientRetriesForbiddenResponses()
    {
        var attempts = 0;
        var handler = new RecordingHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            return Task.FromResult(JsonResponse(
                """
                {
                  "id": "disk-1",
                  "labels": {},
                  "status": { "state": "Ready" }
                }
                """));
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance, TimeSpan.Zero);

        var diskImage = await client.GetDiskImageAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            "disk-1",
            CancellationToken.None);

        Assert.Equal("disk-1", diskImage.Id);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AzureDevComputeClientRetriesThrottledAndServerResponses()
    {
        var statuses = new Queue<HttpStatusCode>(
        [
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK
        ]);
        var handler = new RecordingHandler(_ =>
        {
            var status = statuses.Dequeue();
            if (status == HttpStatusCode.OK)
            {
                return Task.FromResult(JsonResponse("""{ "id": "disk-1", "labels": {}, "status": { "state": "Ready" } }"""));
            }

            var response = new HttpResponseMessage(status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance, TimeSpan.Zero);

        var diskImage = await client.GetDiskImageAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            "disk-1",
            CancellationToken.None);

        Assert.Equal("disk-1", diskImage.Id);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task AzureDevComputeClientRetriesTransientNetworkErrors()
    {
        var attempts = 0;
        var handler = new RecordingHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"))
                : Task.FromResult(JsonResponse("""{ "id": "disk-1", "labels": {}, "status": { "state": "Ready" } }"""));
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance, TimeSpan.Zero);

        var diskImage = await client.GetDiskImageAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            "disk-1",
            CancellationToken.None);

        Assert.Equal("disk-1", diskImage.Id);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AzureDevComputeClientDoesNotRetryAmbiguousCreateNetworkErrors()
    {
        var attempts = 0;
        var handler = new RecordingHandler(_ =>
        {
            attempts++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"));
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance, TimeSpan.Zero);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateDiskImageAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            new AzureDevComputeCreateDiskImageRequest
            {
                Name = "disk-image",
                Image = new AzureDevComputeDiskImageSpec { Base = "example.azurecr.io/site@sha256:abc123" }
            },
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AzureDevComputeClientDoesNotRetryAmbiguousCreateServerErrors()
    {
        var attempts = 0;
        var handler = new RecordingHandler(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance, TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateDiskImageAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            new AzureDevComputeCreateDiskImageRequest
            {
                Name = "disk-image",
                Image = new AzureDevComputeDiskImageSpec { Base = "example.azurecr.io/site@sha256:abc123" }
            },
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AzureDevComputeClientTreatsMissingDeletedResourcesAsSuccess()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);
        var scope = new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3");

        await client.DeleteSandboxAsync(scope, "sandbox-1", CancellationToken.None);
        await client.DeleteDiskImageAsync(scope, "disk-1", CancellationToken.None);
        var ports = await client.RemovePortAsync(
            scope,
            "sandbox-1",
            new AzureDevComputeRemovePortRequest { Port = 8080 },
            CancellationToken.None);

        Assert.Empty(ports);
    }

    [Fact]
    public async Task AzureDevComputeClientDoesNotExposeUnrecognizedErrorBodies()
    {
        const string secret = "registry-refresh-token-secret";
        var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(secret, Encoding.UTF8, "text/plain")
        }));
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDiskImageAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            "disk-1",
            CancellationToken.None));

        Assert.DoesNotContain(secret, exception.Message);
        Assert.Contains("unrecognized error response", exception.Message);
    }

    [Fact]
    public void SandboxImageReferencesResolveToImmutableDigests()
    {
        var reference = AzureSandboxContainerDeployment.ResolveLinuxAmd64ManifestReference(
            """{ "Descriptor": { "digest": "sha256:abc123" } }""",
            "example.azurecr.io/site:latest");

        Assert.Equal("example.azurecr.io/site@sha256:abc123", reference);

        var podmanReference = AzureSandboxContainerDeployment.ResolveLinuxAmd64ManifestReference(
            """{ "digest": "sha256:podman123" }""",
            "example.azurecr.io/site:latest");

        Assert.Equal("example.azurecr.io/site@sha256:podman123", podmanReference);

        var verboseReference = AzureSandboxContainerDeployment.ResolveLinuxAmd64ManifestReference(
            """
            [
              {
                "Descriptor": {
                  "digest": "sha256:amd64",
                  "platform": { "os": "linux", "architecture": "amd64" }
                }
              },
              {
                "Descriptor": {
                  "digest": "sha256:arm64",
                  "platform": { "os": "linux", "architecture": "arm64" }
                }
              }
            ]
            """,
            "example.azurecr.io/site:latest");

        Assert.Equal("example.azurecr.io/site@sha256:amd64", verboseReference);

        var pinnedIndexReference = AzureSandboxContainerDeployment.ResolveLinuxAmd64ManifestReference(
            """
            {
              "manifests": [
                {
                  "digest": "sha256:linux-amd64",
                  "platform": { "os": "linux", "architecture": "amd64" }
                },
                {
                  "digest": "sha256:linux-arm64",
                  "platform": { "os": "linux", "architecture": "arm64" }
                }
              ]
            }
            """,
            "example.azurecr.io/site@sha256:index");

        Assert.Equal("example.azurecr.io/site@sha256:linux-amd64", pinnedIndexReference);

        var pinnedManifestReference = AzureSandboxContainerDeployment.ResolveLinuxAmd64ManifestReference(
            """
            {
              "Descriptor": {
                "digest": "sha256:linux-amd64",
                "platform": { "os": "linux", "architecture": "amd64" }
              }
            }
            """,
            "example.azurecr.io/site@sha256:linux-amd64");

        Assert.Equal("example.azurecr.io/site@sha256:linux-amd64", pinnedManifestReference);

        var incompatiblePlatformException = Assert.Throws<InvalidOperationException>(() =>
            AzureSandboxContainerDeployment.ResolveLinuxAmd64ManifestReference(
                """
                {
                  "Descriptor": {
                    "digest": "sha256:windows-amd64",
                    "platform": { "os": "windows", "architecture": "amd64" }
                  }
                }
                """,
                "example.azurecr.io/site@sha256:windows-amd64"));
        Assert.Contains("require linux/amd64", incompatiblePlatformException.Message);

        var unverifiablePlatformException = Assert.Throws<InvalidOperationException>(() =>
            AzureSandboxContainerDeployment.ResolveLinuxAmd64ManifestReference(
                """{ "Descriptor": { "digest": "sha256:unknown" } }""",
                "example.azurecr.io/site@sha256:unknown"));
        Assert.Contains("platform could not be verified", unverifiablePlatformException.Message);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AzureSandboxContainerDeployment.ResolveLinuxAmd64ManifestReference(
                """{ "schemaVersion": 2 }""",
                "example.azurecr.io/site:latest"));
        Assert.Contains("mutable", exception.Message);
    }

    [Fact]
    public async Task DigestPinnedSandboxImageReferencesAreInspected()
    {
        var runtime = new FakeContainerRuntime
        {
            InspectImageManifestAsyncCallback = static (_, _) => Task.FromResult(
                """
                {
                  "manifests": [
                    {
                      "digest": "sha256:linux-amd64",
                      "platform": { "os": "linux", "architecture": "amd64" }
                    }
                  ]
                }
                """)
        };

        var reference = await AzureSandboxContainerDeployment.ResolveContainerImageReferenceForDiskImageAsync(
            runtime,
            "example.azurecr.io/site@sha256:index",
            CancellationToken.None);

        Assert.True(runtime.WasInspectImageManifestCalled);
        Assert.Equal(["example.azurecr.io/site@sha256:index"], runtime.InspectImageManifestCalls);
        Assert.Equal("example.azurecr.io/site@sha256:linux-amd64", reference);
    }

    [Fact]
    public async Task AzureDevComputeClientCreatesSandboxWithContainerMetadata()
    {
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("management.westus3.azuredevcompute.io", request.RequestUri?.Host);
            Assert.Equal("/subscriptions/sub/resourceGroups/rg/sandboxGroups/sg/sandboxes", request.RequestUri?.AbsolutePath);
            Assert.Equal("?api-version=2026-02-01-preview", request.RequestUri?.Query);

            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("disk-1", root.GetProperty("sourcesRef").GetProperty("diskImage").GetProperty("id").GetString());
            Assert.False(root.GetProperty("sourcesRef").GetProperty("diskImage").GetProperty("isPublic").GetBoolean());
            Assert.Equal("2000m", root.GetProperty("resources").GetProperty("cpu").GetString());
            Assert.Equal("4096Mi", root.GetProperty("resources").GetProperty("memory").GetString());
            Assert.Equal("32768Mi", root.GetProperty("resources").GetProperty("disk").GetString());
            Assert.Equal("dotnet", root.GetProperty("entrypoint")[0].GetString());
            Assert.Equal("/app/app.dll", root.GetProperty("entrypoint")[1].GetString());
            Assert.Equal("--urls", root.GetProperty("cmd")[0].GetString());
            Assert.Equal("http://+:5000", root.GetProperty("environment").GetProperty("ASPNETCORE_URLS").GetString());
            return JsonResponse(
                """
                {
                  "id": "sandbox-1",
                  "vmmType": "cloudhypervisor",
                  "sourcesRef": { "diskImage": { "id": "disk-1", "isPublic": false } },
                  "resources": { "cpu": "1000m", "memory": "2048Mi", "disk": "20480Mi" },
                  "ports": []
                }
                """,
                HttpStatusCode.Created);
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);

        var sandbox = await client.CreateSandboxAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            new AzureDevComputeSandboxRequest
            {
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["aspire-resource"] = "site-container"
                },
                Entrypoint = ["dotnet", "/app/app.dll"],
                Cmd = ["--urls"],
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ASPNETCORE_URLS"] = "http://+:5000"
                },
                SourcesRef = new AzureDevComputeSandboxSource
                {
                    DiskImage = new AzureDevComputeSandboxDiskImageSource
                    {
                        Id = "disk-1",
                        IsPublic = false
                    }
                },
                Resources = new AzureDevComputeSandboxResources
                {
                    Cpu = "2000m",
                    Memory = "4096Mi",
                    Disk = "32768Mi"
                }
            },
            CancellationToken.None);

        Assert.Equal("sandbox-1", sandbox.Id);
    }

    [Fact]
    public async Task AzureDevComputeClientSetsLifecycleWithAutoDelete()
    {
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/subscriptions/sub/resourceGroups/rg/sandboxGroups/sg/sandboxes/sandbox-1/lifecycle", request.RequestUri?.AbsolutePath);

            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.False(root.GetProperty("autoSuspendPolicy").GetProperty("enabled").GetBoolean());
            Assert.Equal(300, root.GetProperty("autoSuspendPolicy").GetProperty("interval").GetInt32());
            Assert.Equal("Disk", root.GetProperty("autoSuspendPolicy").GetProperty("mode").GetString());
            Assert.True(root.GetProperty("autoDeletePolicy").GetProperty("enabled").GetBoolean());
            Assert.Equal(3600, root.GetProperty("autoDeletePolicy").GetProperty("deleteIntervalInSeconds").GetInt64());
            Assert.Equal("AfterSuspend", root.GetProperty("autoDeletePolicy").GetProperty("trigger").GetString());

            return JsonResponse(
                """
                {
                  "id": "sandbox-1",
                  "ports": []
                }
                """);
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);

        var sandbox = await client.SetLifecycleAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            "sandbox-1",
            new AzureDevComputeSandboxLifecyclePolicy
            {
                AutoSuspendPolicy = new AzureDevComputeSandboxAutoSuspendPolicy
                {
                    Enabled = false,
                    Interval = 300,
                    Mode = "Disk"
                },
                AutoDeletePolicy = new AzureDevComputeSandboxAutoDeletePolicy
                {
                    Enabled = true,
                    DeleteIntervalInSeconds = 3600,
                    Trigger = "AfterSuspend"
                }
            },
            CancellationToken.None);

        Assert.Equal("sandbox-1", sandbox.Id);
    }

    [Fact]
    public async Task AzureDevComputeClientAddsAnonymousPort()
    {
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("management.westus3.azuredevcompute.io", request.RequestUri?.Host);
            Assert.Equal("/subscriptions/sub/resourceGroups/rg/sandboxGroups/sg/sandboxes/sandbox-1/ports/add", request.RequestUri?.AbsolutePath);
            Assert.Equal("?api-version=2026-02-01-preview", request.RequestUri?.Query);

            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal(80, root.GetProperty("port").GetInt32());
            Assert.True(root.GetProperty("auth").GetProperty("anonymous").GetBoolean());
            Assert.Equal("Http", root.GetProperty("protocol").GetString());

            return JsonResponse(
                """
                {
                  "ports": [
                    { "port": 80, "url": "https://sandbox.example.test" }
                  ]
                }
                """);
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);

        var ports = await client.AddPortAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            "sandbox-1",
            new AzureDevComputeAddPortRequest
            {
                Port = 80,
                Auth = new AzureDevComputePortAuthConfig { Anonymous = true },
                Protocol = "Http"
            },
            CancellationToken.None);

        var port = Assert.Single(ports);
        Assert.Equal(80, port.Port);
        Assert.Equal("https://sandbox.example.test/", port.Url.ToString());
    }

    [Fact]
    public async Task AzureDevComputeClientAddsEntraAuthorizedPortWithoutSecrets()
    {
        var handler = new RecordingHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal(5, root.EnumerateObject().Count());
            Assert.Equal("webhook", root.GetProperty("name").GetString());
            Assert.Equal(8080, root.GetProperty("port").GetInt32());
            Assert.Equal("OnDemand", root.GetProperty("activationMode").GetString());
            Assert.Equal("Http", root.GetProperty("protocol").GetString());

            var auth = root.GetProperty("auth");
            Assert.Equal(2, auth.EnumerateObject().Count());
            Assert.False(auth.GetProperty("anonymous").GetBoolean());
            var entraId = auth.GetProperty("entraId");
            Assert.True(entraId.GetProperty("enabled").GetBoolean());
            Assert.Equal(
                ["11111111-1111-1111-1111-111111111111"],
                entraId.GetProperty("objectIds").EnumerateArray().Select(static item => item.GetString()!).ToArray());
            Assert.Equal(
                ["22222222-2222-2222-2222-222222222222"],
                entraId.GetProperty("tenantIds").EnumerateArray().Select(static item => item.GetString()!).ToArray());

            return JsonResponse(
                """
                {
                  "ports": [
                    { "name": "webhook", "port": 8080, "url": "https://sandbox.example.test" }
                  ]
                }
                """);
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);

        var ports = await client.AddPortAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            "sandbox-1",
            new AzureDevComputeAddPortRequest
            {
                Name = "webhook",
                Port = 8080,
                ActivationMode = "OnDemand",
                Auth = new AzureDevComputePortAuthConfig
                {
                    Anonymous = false,
                    EntraId = new AzureDevComputePortEntraIdAuthConfig
                    {
                        Enabled = true,
                        ObjectIds = ["11111111-1111-1111-1111-111111111111"],
                        TenantIds = ["22222222-2222-2222-2222-222222222222"]
                    }
                },
                Protocol = "Http"
            },
            CancellationToken.None);

        Assert.Equal(8080, Assert.Single(ports).Port);
    }

    [Fact]
    public async Task SandboxContainerOptionsMapToRuntimeRequestShapes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
            {
                Tier = AzureSandboxTier.Large,
                AutoSuspendEnabled = false,
                AutoSuspendInterval = 300,
                AutoSuspendMode = "Disk",
                AutoDeleteEnabled = true,
                AutoDeleteIntervalInSeconds = 3600,
                AutoDeleteTrigger = "AfterSuspend",
                Endpoints =
                [
                    new AzureSandboxEndpointOptions
                    {
                        Name = "http",
                        Anonymous = false
                    }
                ]
            });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget?.DeploymentTarget);

        var resources = AzureSandboxContainerDeployment.CreateSandboxResources(sandboxContainer);
        Assert.Equal("2000m", resources.Cpu);
        Assert.Equal("4096Mi", resources.Memory);
        Assert.Equal("40960Mi", resources.Disk);

        var lifecycle = AzureSandboxContainerDeployment.CreateLifecyclePolicy(sandboxContainer);
        Assert.NotNull(lifecycle);
        Assert.NotNull(lifecycle.AutoSuspendPolicy);
        Assert.False(lifecycle.AutoSuspendPolicy.Enabled);
        Assert.Equal(300, lifecycle.AutoSuspendPolicy.Interval);
        Assert.Equal("Disk", lifecycle.AutoSuspendPolicy.Mode);
        Assert.NotNull(lifecycle.AutoDeletePolicy);
        Assert.True(lifecycle.AutoDeletePolicy.Enabled);
        Assert.Equal(3600, lifecycle.AutoDeletePolicy.DeleteIntervalInSeconds);
        Assert.Equal("AfterSuspend", lifecycle.AutoDeletePolicy.Trigger);

        var egress = AzureSandboxContainerDeployment.CreateEgressPolicy();
        Assert.Equal("Deny", egress.DefaultAction);
        Assert.Equal("Full", egress.TrafficInspection);

        var endpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal("Http", endpoint.Protocol);
        Assert.False(endpoint.Anonymous);
    }

    [Fact]
    public async Task SandboxContainerRejectsUnprovisionedVolumes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithVolume("cache", "/cache")
            .PublishAsAzureSandbox(sandboxGroup);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        var exception = Assert.Throws<NotSupportedException>(() => AzureSandboxContainerDeployment.ValidateSandboxVolumes(computeResource));
        Assert.Contains("volume provisioning is not supported", exception.Message);
    }

    [Fact]
    public async Task SandboxContainerEndpointResolutionMapsHttp2()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .AsHttp2Service()
            .PublishAsAzureSandbox(sandboxGroup);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget?.DeploymentTarget);

        var endpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal("Http2", endpoint.Protocol);
    }

    [Fact]
    public async Task SandboxEndpointResolutionSupportsSameSandboxGroupReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var api = builder.AddContainer("api", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup);

        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 3000)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        Assert.True(AzureSandboxContainerDeployment.TryResolveEndpointReferenceValue(api.GetEndpoint("http"), sandboxGroup.Resource, out var urlExpression));
        Assert.Equal("{api-sandbox-container.endpoints.http.url}", urlExpression.ValueExpression);
        var unresolved = await Assert.ThrowsAsync<InvalidOperationException>(async () => await urlExpression.GetValueAsync(default));
        Assert.Contains("does not have a deployed URL yet", unresolved.Message);

        Assert.True(AzureSandboxContainerDeployment.TryResolveEndpointReferenceValue(api.GetEndpoint("http").Property(EndpointProperty.TargetPort), sandboxGroup.Resource, out var targetPortExpression));
        Assert.Equal("{api-sandbox-container.endpoints.http.targetport}", targetPortExpression.ValueExpression);
        Assert.Equal("8080", await targetPortExpression.GetValueAsync(default));
    }

    [Fact]
    public async Task SandboxContainerEndpointResolutionRejectsUnknownEndpointOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
            {
                Endpoints =
                [
                    new AzureSandboxEndpointOptions
                    {
                        Name = "typo",
                        Anonymous = false
                    }
                ]
            });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget?.DeploymentTarget);

        var exception = Assert.Throws<InvalidOperationException>(() => AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Contains("endpoint options for endpoint(s) that are not exposed", exception.Message);
    }

    [Fact]
    public async Task SandboxContainerEndpointResolutionRejectsTcp()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("cache", "redis", "latest")
            .WithEndpoint(targetPort: 6379, scheme: "tcp", isExternal: true)
            .PublishAsAzureSandbox(sandboxGroup);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "cache");
        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget?.DeploymentTarget);

        var exception = Assert.Throws<NotSupportedException>(() => AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Contains("support only HTTP and HTTP/2 endpoints", exception.Message);
    }

    [Fact]
    public async Task SandboxGroupAddsDeploymentTargetsAndBuildOptionsForProjects()
    {
        using var tempDir = new TemporaryDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var frontend = builder.AddProject<TestProject>("frontend", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 5000)
            .WithExternalHttpEndpoints()
            .WithContainerBuildOptions(options =>
            {
                options.Destination = ContainerImageDestination.Archive;
                options.OutputPath = "frontend.tar";
                options.ImageFormat = ContainerImageFormat.Oci;
                options.TargetPlatform = ContainerTargetPlatform.LinuxArm64;
            });
        var backend = builder.AddProject<TestProject>("backend", launchProfileName: null)
            .WithContainerBuildOptions(options =>
            {
                options.Destination = ContainerImageDestination.Archive;
                options.OutputPath = "backend.tar";
                options.ImageFormat = ContainerImageFormat.Oci;
                options.TargetPlatform = ContainerTargetPlatform.LinuxArm64;
            })
            .PublishAsAzureSandbox(sandboxGroup);
        var frontendCallbackCount = frontend.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count();
        var backendCallbackCount = backend.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        Assert.Empty(model.Resources.OfType<AzureSandboxContainerResource>());

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        var explicitComputeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "backend");
        Assert.Same(sandboxGroup.Resource, computeResource.GetComputeEnvironment());
        Assert.Same(sandboxGroup.Resource, explicitComputeResource.GetComputeEnvironment());
        Assert.Equal(frontendCallbackCount + 1, computeResource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count());
        Assert.Equal(backendCallbackCount + 1, explicitComputeResource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count());

        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        Assert.Equal(frontendCallbackCount + 1, computeResource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count());
        Assert.Equal(backendCallbackCount + 1, explicitComputeResource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count());

        var buildOptions = new ContainerBuildOptionsCallbackContext(
            computeResource,
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));
        foreach (var annotation in computeResource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>())
        {
            await annotation.Callback(buildOptions);
        }

        Assert.Equal(ContainerImageDestination.Registry, buildOptions.Destination);
        Assert.Null(buildOptions.OutputPath);
        Assert.Equal(ContainerImageFormat.Docker, buildOptions.ImageFormat);
        Assert.Equal(ContainerTargetPlatform.LinuxAmd64, buildOptions.TargetPlatform);

        var explicitBuildOptions = new ContainerBuildOptionsCallbackContext(
            explicitComputeResource,
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));
        foreach (var annotation in explicitComputeResource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>())
        {
            await annotation.Callback(explicitBuildOptions);
        }

        Assert.Equal(ContainerImageDestination.Registry, explicitBuildOptions.Destination);
        Assert.Null(explicitBuildOptions.OutputPath);
        Assert.Equal(ContainerImageFormat.Docker, explicitBuildOptions.ImageFormat);
        Assert.Equal(ContainerTargetPlatform.LinuxAmd64, explicitBuildOptions.TargetPlatform);

        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        Assert.NotNull(deploymentTarget);
        Assert.Same(sandboxGroup.Resource.ContainerRegistry, deploymentTarget.ContainerRegistry);
        Assert.Same(sandboxGroup.Resource, deploymentTarget.ComputeEnvironment);

        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget.DeploymentTarget);
        Assert.Same(computeResource, sandboxContainer.TargetResource);
        Assert.Same(sandboxGroup.Resource, sandboxContainer.Parent);
        Assert.False(sandboxContainer.AutoSuspend);

        var sandboxEndpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal(5000, sandboxEndpoint.TargetPort);
        Assert.True(sandboxEndpoint.IsExternal);
        Assert.True(sandboxEndpoint.IsHttp);

        var pipelineAnnotation = Assert.Single(sandboxContainer.Annotations.OfType<PipelineStepAnnotation>());
        var steps = (await pipelineAnnotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = sandboxContainer
        })).ToList();

        var deployStep = Assert.Single(steps, step => step.Name == "deploy-frontend-sandbox-container");
        Assert.Contains(AzureEnvironmentResource.ProvisionInfrastructureStepName, deployStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.DeployPrereq, deployStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, deployStep.RequiredBySteps);
        Assert.Contains(WellKnownPipelineTags.DeployCompute, deployStep.Tags);

        var pushStep = new PipelineStep
        {
            Name = "push-frontend",
            Resource = computeResource,
            Tags = [WellKnownPipelineTags.PushContainerImage],
            Action = _ => Task.CompletedTask
        };
        steps.Add(pushStep);

        foreach (var annotation in sandboxContainer.Annotations.OfType<PipelineConfigurationAnnotation>())
        {
            await annotation.Callback(new PipelineConfigurationContext
            {
                Services = app.Services,
                Steps = steps,
                Model = model
            });
        }

        Assert.Contains(pushStep.Name, deployStep.DependsOnSteps);

        var destroyStep = Assert.Single(steps, step => step.Name == "destroy-frontend-sandbox-container");
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, destroyStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, destroyStep.RequiredBySteps);

        var cleanupResource = Assert.Single(model.Resources, resource => resource.Name == "azure-sandbox-cleanup");
        var cleanupSteps = await CreateStepsAsync(app, cleanupResource);
        var staleCleanupStep = Assert.Single(cleanupSteps, step => step.Name == "destroy-stale-azure-sandboxes");
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, staleCleanupStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, staleCleanupStep.RequiredBySteps);

        var azureDestroyStep = new PipelineStep
        {
            Name = "destroy-azure-sandboxes",
            Action = _ => Task.CompletedTask
        };
        var environmentSteps = cleanupSteps;
        environmentSteps.Add(azureDestroyStep);

        var configurationContext = new PipelineConfigurationContext
        {
            Services = app.Services,
            Steps = environmentSteps,
            Model = model
        };

        foreach (var annotation in sandboxGroup.Resource.Annotations.OfType<PipelineConfigurationAnnotation>()
            .Concat(cleanupResource.Annotations.OfType<PipelineConfigurationAnnotation>()))
        {
            await annotation.Callback(configurationContext);
        }

        Assert.Contains(destroyStep.Name, azureDestroyStep.DependsOnSteps);
        Assert.Contains(staleCleanupStep.Name, azureDestroyStep.DependsOnSteps);
    }

    [Fact]
    public void AddAzureSandboxGroupAddsSingleCleanupResource()
    {
        using var tempDir = new TemporaryDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path);

        builder.AddAzureSandboxGroup("sandboxes");
        builder.AddAzureSandboxGroup("othersandboxes");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Single(model.Resources, resource => resource.Name == "azure-sandbox-cleanup");
    }

    [Fact]
    public async Task SandboxGroupUsesExplicitComputeEnvironmentWhenMultipleEnvironmentsExist()
    {
        using var tempDir = new TemporaryDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddAzureSandboxGroup("othersandboxes");

        builder.AddProject<TestProject>("frontend", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 5000)
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(sandboxGroup);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        Assert.Same(sandboxGroup.Resource, computeResource.GetComputeEnvironment());

        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        Assert.NotNull(deploymentTarget);
        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget.DeploymentTarget);
        var sandboxEndpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal(5000, sandboxEndpoint.TargetPort);
        Assert.True(sandboxEndpoint.IsExternal);
    }

    [Fact]
    public async Task SandboxGroupAddsDeploymentTargetForContainerResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        Assert.NotNull(deploymentTarget);

        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget.DeploymentTarget);
        Assert.Same(computeResource, sandboxContainer.TargetResource);
        var sandboxEndpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal(8080, sandboxEndpoint.TargetPort);
    }

    private static async Task<List<PipelineStep>> CreateStepsAsync(
        DistributedApplication app,
        IResource resource,
        DistributedApplicationOperation operation = DistributedApplicationOperation.Publish)
    {
        var pipelineContext = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            new DistributedApplicationExecutionContext(operation),
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

        var results = new List<PipelineStep>();
        foreach (var annotation in resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            results.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = resource
            }));
        }

        return results;
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "testproject";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory(".aspire-test").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }

    private sealed class RecordingTokenCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = [.. requestContext.Scopes];
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = [.. requestContext.Scopes];
            return ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private static HttpResponseMessage JsonResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }
}
