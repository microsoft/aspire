// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREAZURE003

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Utils;
using Azure.Core;
using Azure.Provisioning.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSandboxesTests
{
    [Fact]
    public void AzureSandboxGroupUsesExplicitOutputReferenceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");

        Assert.Equal("{sandboxes.outputs.id}", sandboxGroup.Resource.IdOutputReference.ValueExpression);
        Assert.Equal("{sandboxes.outputs.name}", sandboxGroup.Resource.NameOutputReference.ValueExpression);
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
    public async Task AzureSandboxPublishBindsPrincipalTypeInMainBicep()
    {
        using var tempDir = new TemporaryDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            outputPath: tempDir.Path);
        builder.AddAzureSandboxGroup("sandboxes");

        using var app = builder.Build();
        await app.RunAsync();

        var mainBicep = await File.ReadAllTextAsync(
            Path.Combine(tempDir.Path, "main.bicep"),
            TestContext.Current.CancellationToken);
        await Verify(mainBicep, "bicep");
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
    public async Task SandboxGroupAggregatesWorkloadManagedIdentities()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var identity = builder.AddAzureUserAssignedIdentity("workload-identity");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var otherGroup = builder.AddAzureSandboxGroup("other-sandboxes");
        builder.AddContainer("worker", "image")
            .WithAnnotation(new AppIdentityAnnotation(identity.Resource))
            .PublishAsAzureSandbox(sandboxGroup);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        Assert.Equal(ManagedServiceIdentityType.UserAssigned, sandboxGroup.Resource.ManagedIdentityType);
        Assert.Equal(identity.Resource, Assert.Single(sandboxGroup.Resource.UserAssignedIdentities));
        Assert.Empty(otherGroup.Resource.UserAssignedIdentities);
        var (_, bicep) = await AzureManifestUtils.GetManifestWithBicep(sandboxGroup.Resource, skipPreparer: true);
        Assert.Contains("userAssignedIdentities", bicep, StringComparison.Ordinal);
        Assert.Contains("workload_identity_outputs_id", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingSandboxGroupRejectsWorkloadIdentityAttachment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var identity = builder.AddAzureUserAssignedIdentity("workload-identity");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .PublishAsExisting("existing-sandboxes", "existing-rg");
        builder.AddContainer("worker", "image")
            .WithAnnotation(new AppIdentityAnnotation(identity.Resource))
            .PublishAsAzureSandbox(sandboxGroup);

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("existing Azure sandbox group", exception.Message);
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
        Assert.Throws<ArgumentException>(() => container.PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
        {
            AutoSuspendMode = (AzureSandboxAutoSuspendMode)(-1)
        }));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");

        Assert.Null(computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource));
        Assert.True(configureCalled);
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
        Assert.Empty(metadata.EnvironmentVariables);
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
    public void SandboxStableOwnerSurvivesClearedStateAndPreservesIsolation()
    {
        var scope = new AzureDevComputeResourceScope("sub", "rg", "sandboxes", "westus3");
        var owner = AzureSandboxContainerDeployment.CreateStableOwnerId("AppHost", scope, "frontend-sandbox-container");
        var freshRunOwner = AzureSandboxContainerDeployment.CreateStableOwnerId("apphost", scope, "frontend-sandbox-container");
        var otherAppOwner = AzureSandboxContainerDeployment.CreateStableOwnerId("OtherAppHost", scope, "frontend-sandbox-container");
        var otherScopeOwner = AzureSandboxContainerDeployment.CreateStableOwnerId(
            "AppHost",
            new AzureDevComputeResourceScope("sub", "other-rg", "sandboxes", "westus3"),
            "frontend-sandbox-container");

        Assert.Equal(owner, freshRunOwner);
        Assert.NotEqual(owner, otherAppOwner);
        Assert.NotEqual(owner, otherScopeOwner);
        Assert.True(AzureSandboxContainerDeployment.ShouldDeleteLabeledDeployment(
            "old-sandbox",
            new Dictionary<string, string>
            {
                ["aspire-owner"] = freshRunOwner,
                ["aspire-resource"] = "frontend-sandbox-container"
            },
            owner,
            "frontend-sandbox-container",
            new HashSet<string>(),
            new HashSet<string>()));
    }

    [Fact]
    public void SandboxSecurityChangesDisablePreviousGenerationRetention()
    {
        var endpoints = new[]
        {
            new AzureSandboxContainerDeployment.SandboxEndpoint(
                "http",
                8080,
                IsExternal: true,
                IsHttp: true,
                Protocol: "Http",
                Anonymous: false)
        };
        var fingerprint = AzureSandboxContainerDeployment.CreateDeploymentSecurityFingerprint(
            endpoints,
            "example/image@sha256:first");
        var previousState = new DeploymentStateSection(
            "Azure:Sandboxes:frontend-sandbox-container",
            new JsonObject
            {
                ["OwnerId"] = "owner",
                ["SandboxId"] = "sandbox"
            },
            version: 0);

        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, fingerprint));

        previousState.Data["EndpointSecurityFingerprint"] = fingerprint;
        Assert.False(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, fingerprint));

        var anonymousFingerprint = AzureSandboxContainerDeployment.CreateDeploymentSecurityFingerprint(
        [
            endpoints[0] with { Anonymous = true }
        ],
        "example/image@sha256:first");
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, anonymousFingerprint));

        var updatedImageFingerprint = AzureSandboxContainerDeployment.CreateDeploymentSecurityFingerprint(
            endpoints,
            "example/image@sha256:second");
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, updatedImageFingerprint));
    }

    [Fact]
    public async Task SandboxBestEffortPruneSuppressesNetworkFailures()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var app = builder.Build();
        var pipelineContext = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };
        var client = new FailingPruneClient();

        await AzureSandboxContainerDeployment.DeleteRemoteDeploymentsByResourceLabelAsync(
            stepContext,
            client,
            new AzureDevComputeResourceScope("sub", "rg", "sandboxes", "westus3"),
            "owner",
            "frontend-sandbox-container",
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            throwOnError: false);

        Assert.True(client.DeleteSandboxCalled);
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
    public async Task AzureDevComputeClientBoundsForbiddenRetriesAndExplainsRequiredRole()
    {
        var attempts = 0;
        var handler = new RecordingHandler(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        });
        var client = new AzureDevComputeClient(
            new HttpClient(handler),
            new RecordingTokenCredential(),
            NullLogger.Instance,
            TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDiskImageAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            "disk-1",
            CancellationToken.None));

        Assert.Equal(3, attempts);
        Assert.Contains("Container Apps SandboxGroup Data Owner", exception.Message);
    }

    [Fact]
    public void AzureDevComputeClientCapsRetryAfterDelays()
    {
        var now = DateTimeOffset.Parse("2026-08-05T20:00:00Z");
        using var secondsResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        secondsResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
        using var dateResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        dateResponse.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddDays(1));

        Assert.Equal(
            TimeSpan.FromSeconds(30),
            AzureDevComputeClient.GetRetryDelay(secondsResponse, TimeSpan.FromSeconds(5), now));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            AzureDevComputeClient.GetRetryDelay(dateResponse, TimeSpan.FromSeconds(5), now));
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
    public async Task AzureDevComputeClientDoesNotExposeErrorBodies()
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
        Assert.Contains("details were redacted", exception.Message);
    }

    [Fact]
    public async Task AzureDevComputeClientRedactsProblemDetailsThatEchoSecrets()
    {
        const string secret = "resolved-secret-environment-value";
        var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new
            {
                title = $"Invalid value: {secret}",
                detail = $"The request contained {secret}."
            })
        }));
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDiskImageAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            "disk-1",
            CancellationToken.None));

        Assert.DoesNotContain(secret, exception.Message);
        Assert.Contains("details were redacted", exception.Message);
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
                AutoSuspendInterval = TimeSpan.FromMinutes(5),
                AutoSuspendMode = AzureSandboxAutoSuspendMode.Disk,
                AutoDeleteEnabled = true,
                AutoDeleteInterval = TimeSpan.FromHours(1),
                AutoDeleteTrigger = AzureSandboxAutoDeleteTrigger.AfterSuspend,
                PublicEndpointReadyTimeout = TimeSpan.FromMinutes(2),
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
        Assert.Null(lifecycle.AutoDeletePolicy.DeleteIntervalInDays);
        Assert.Equal(3600, lifecycle.AutoDeletePolicy.DeleteIntervalInSeconds);
        Assert.Equal("AfterSuspend", lifecycle.AutoDeletePolicy.Trigger);
        Assert.Equal(TimeSpan.FromMinutes(2), AzureSandboxContainerDeployment.GetPublicEndpointReadyTimeout(sandboxContainer));

        var egress = AzureSandboxContainerDeployment.CreateEgressPolicy();
        Assert.Equal("Deny", egress.DefaultAction);
        Assert.Equal("Full", egress.TrafficInspection);

        var endpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal("Http", endpoint.Protocol);
        Assert.False(endpoint.Anonymous);
    }

    [Fact]
    public async Task SandboxAutoSuspendPolicyIsEmittedOnlyWhenExplicitlyConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var defaultResource = builder.AddContainer("default", "image")
            .PublishAsAzureSandbox(sandboxGroup);
        var disabledResource = builder.AddContainer("disabled", "image")
            .PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
            {
                AutoSuspendEnabled = false
            });
        var enabledResource = builder.AddContainer("enabled", "image")
            .PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
            {
                AutoSuspendEnabled = true,
                AutoSuspendInterval = TimeSpan.FromMinutes(5),
                AutoSuspendMode = AzureSandboxAutoSuspendMode.Memory
            });

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var defaultSandbox = Assert.IsType<AzureSandboxContainerResource>(
            defaultResource.Resource.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        var disabledSandbox = Assert.IsType<AzureSandboxContainerResource>(
            disabledResource.Resource.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        var enabledSandbox = Assert.IsType<AzureSandboxContainerResource>(
            enabledResource.Resource.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);

        Assert.Null(AzureSandboxContainerDeployment.CreateLifecyclePolicy(defaultSandbox));
        var disabledPolicy = Assert.IsType<AzureDevComputeSandboxLifecyclePolicy>(
            AzureSandboxContainerDeployment.CreateLifecyclePolicy(disabledSandbox));
        Assert.NotNull(disabledPolicy.AutoSuspendPolicy);
        Assert.False(disabledPolicy.AutoSuspendPolicy.Enabled);
        var enabledPolicy = Assert.IsType<AzureDevComputeSandboxLifecyclePolicy>(
            AzureSandboxContainerDeployment.CreateLifecyclePolicy(enabledSandbox));
        Assert.NotNull(enabledPolicy.AutoSuspendPolicy);
        Assert.True(enabledPolicy.AutoSuspendPolicy.Enabled);
        Assert.Equal(300, enabledPolicy.AutoSuspendPolicy.Interval);
        Assert.Equal("Memory", enabledPolicy.AutoSuspendPolicy.Mode);
    }

    [Fact]
    public void SandboxContainerOptionsValidateTypedDurationsAndEnums()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var container = builder.AddContainer("frontend", "image");

        Assert.Throws<ArgumentException>(() => container.PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
        {
            AutoSuspendInterval = TimeSpan.FromMilliseconds(1500)
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => container.PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
        {
            AutoSuspendInterval = TimeSpan.FromSeconds((double)int.MaxValue + 1)
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => container.PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
        {
            AutoDeleteInterval = TimeSpan.FromSeconds(-1)
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => container.PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
        {
            PublicEndpointReadyTimeout = TimeSpan.Zero
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => container.PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
        {
            PublicEndpointReadyTimeout = TimeSpan.FromSeconds((double)int.MaxValue + 1)
        }));
        Assert.Throws<ArgumentException>(() => container.PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
        {
            AutoSuspendMode = (AzureSandboxAutoSuspendMode)(-1)
        }));
        Assert.Throws<ArgumentException>(() => container.PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
        {
            AutoDeleteTrigger = (AzureSandboxAutoDeleteTrigger)(-1)
        }));
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
        var registryLoginStep = new PipelineStep
        {
            Name = "login-to-acr-sandboxes",
            Resource = sandboxGroup.Resource.ContainerRegistry,
            Tags = ["acr-login"],
            Action = _ => Task.CompletedTask
        };
        steps.Add(registryLoginStep);

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
        Assert.Contains(registryLoginStep.Name, deployStep.DependsOnSteps);

        var destroyStep = Assert.Single(steps, step => step.Name == "destroy-frontend-sandbox-container");
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, destroyStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, destroyStep.RequiredBySteps);

        var cleanupResource = Assert.Single(model.Resources, resource => resource.Name == "azure-sandbox-cleanup");
        var cleanupSteps = await CreateStepsAsync(app, cleanupResource);
        var staleCleanupStep = Assert.Single(cleanupSteps, step => step.Name == "destroy-stale-azure-sandboxes");
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, staleCleanupStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, staleCleanupStep.RequiredBySteps);

        var azureEnvironment = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var azureDestroyStep = new PipelineStep
        {
            Name = $"destroy-azure-{azureEnvironment.Name}",
            Resource = azureEnvironment,
            Action = _ => Task.CompletedTask
        };
        var azureNamedSandboxDestroyStep = new PipelineStep
        {
            Name = "destroy-azure-api-sandbox-container",
            Resource = sandboxContainer,
            Action = _ => Task.CompletedTask
        };
        var environmentSteps = cleanupSteps;
        environmentSteps.Add(azureDestroyStep);
        environmentSteps.Add(azureNamedSandboxDestroyStep);

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
        Assert.Empty(azureNamedSandboxDestroyStep.DependsOnSteps);
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

    [Fact]
    public async Task PrebuiltSandboxImageDependsOnManagedRegistryLogin()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var container = builder.AddContainer("frontend", "example.azurecr.io/frontend", "latest")
            .PublishAsAzureSandbox(sandboxGroup);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(
            container.Resource.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        var steps = AzureSandboxContainerDeployment.CreatePipelineSteps(sandboxContainer).ToList();
        var registry = Assert.IsType<AzureContainerRegistryResource>(sandboxGroup.Resource.ContainerRegistry);
        var loginStep = new PipelineStep
        {
            Name = "login-to-acr-sandboxes",
            Resource = registry,
            Tags = ["acr-login"],
            Action = _ => Task.CompletedTask
        };
        steps.Add(loginStep);
        var context = new PipelineConfigurationContext
        {
            Services = app.Services,
            Steps = steps,
            Model = app.Services.GetRequiredService<DistributedApplicationModel>()
        };

        await AzureSandboxContainerDeployment.ConfigureDeployOrderingAsync(context, sandboxContainer);

        var deployStep = Assert.Single(steps, step => step.Name == "deploy-frontend-sandbox-container");
        Assert.Contains(loginStep.Name, deployStep.DependsOnSteps);
        Assert.Empty(context.GetSteps(container.Resource, WellKnownPipelineTags.PushContainerImage));
    }

    [Fact]
    public async Task SandboxValueResolutionRecursesIntoReferenceExpressions()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup);
        var web = builder.AddContainer("web", "image")
            .PublishAsAzureSandbox(sandboxGroup);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var apiSandbox = Assert.IsType<AzureSandboxContainerResource>(
            api.Resource.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        var stateManager = app.Services.GetRequiredService<IDeploymentStateManager>();
        var state = await stateManager.AcquireSectionAsync(
            AzureSandboxContainerDeployment.GetStateSectionName(apiSandbox),
            CancellationToken.None);
        state.Data["Ports"] = new JsonArray
        {
            new JsonObject
            {
                ["Name"] = "http",
                ["Url"] = "https://api.example.test"
            }
        };
        await stateManager.SaveSectionAsync(state, CancellationToken.None);

        var pipelineContext = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };
        var expression = ReferenceExpression.Create($"{api.GetEndpoint("http")}/v1");

        var value = await AzureSandboxContainerDeployment.ResolveValueAsync(stepContext, web.Resource, expression);

        Assert.Equal("https://api.example.test/v1", value);
    }

    [Fact]
    public async Task SandboxDeployStepsFollowReferencedEndpointDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup);
        var web = builder.AddContainer("web", "image")
            .WithEnvironment("API_URL", ReferenceExpression.Create($"{api.GetEndpoint("http")}/v1"))
            .PublishAsAzureSandbox(sandboxGroup);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var dependencies = await web.Resource.GetResourceDependenciesAsync(
            executionContext,
            ResourceDependencyDiscoveryMode.DirectOnly);
        Assert.Contains(api.Resource, dependencies);
        var steps = await CreateStepsAsync(app, sandboxGroup.Resource);
        var context = new PipelineConfigurationContext
        {
            Services = app.Services,
            Steps = steps,
            Model = app.Services.GetRequiredService<DistributedApplicationModel>()
        };
        foreach (var annotation in sandboxGroup.Resource.Annotations.OfType<PipelineConfigurationAnnotation>())
        {
            await annotation.Callback(context);
        }

        var apiDeploy = Assert.Single(steps, step => step.Name == "deploy-api-sandbox-container");
        var webDeploy = Assert.Single(steps, step => step.Name == "deploy-web-sandbox-container");
        Assert.Contains(apiDeploy.Name, webDeploy.DependsOnSteps);
    }

    [Fact]
    public async Task SandboxDeployStepsRejectCircularEndpointDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var first = builder.AddContainer("first", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup);
        var second = builder.AddContainer("second", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(sandboxGroup);
        first.WithEnvironment("SECOND_URL", second.GetEndpoint("http"));
        second.WithEnvironment("FIRST_URL", first.GetEndpoint("http"));

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var firstDependencies = await first.Resource.GetResourceDependenciesAsync(
            executionContext,
            ResourceDependencyDiscoveryMode.DirectOnly);
        var secondDependencies = await second.Resource.GetResourceDependenciesAsync(
            executionContext,
            ResourceDependencyDiscoveryMode.DirectOnly);
        Assert.Contains(second.Resource, firstDependencies);
        Assert.Contains(first.Resource, secondDependencies);
        var steps = await CreateStepsAsync(app, sandboxGroup.Resource);
        var context = new PipelineConfigurationContext
        {
            Services = app.Services,
            Steps = steps,
            Model = app.Services.GetRequiredService<DistributedApplicationModel>()
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            foreach (var annotation in sandboxGroup.Resource.Annotations.OfType<PipelineConfigurationAnnotation>())
            {
                await annotation.Callback(context);
            }
        });

        Assert.Contains("circular deployment dependency", exception.Message);
    }

    [Fact]
    public async Task SandboxProjectArgumentsArePreserved()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var project = builder.AddProject<TestProject>("worker", launchProfileName: null)
            .WithArgs("--mode", "worker");

        using var app = builder.Build();
        var pipelineContext = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };

        var (entrypoint, command) = await AzureSandboxContainerDeployment.ResolveModeledCommandAsync(
            stepContext,
            project.Resource);

        Assert.Null(entrypoint);
        Assert.Equal(["--mode", "worker"], command);
    }

    private static async Task<List<PipelineStep>> CreateStepsAsync(DistributedApplication app, IResource resource)
    {
        var pipelineContext = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
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

    private sealed class FailingPruneClient : IAzureDevComputeClient
    {
        public bool DeleteSandboxCalled { get; private set; }

        public Task<List<AzureDevComputeSandbox>> ListSandboxesAsync(
            AzureDevComputeResourceScope scope,
            string? labels,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new List<AzureDevComputeSandbox>
            {
                new()
                {
                    Id = "old-sandbox",
                    Labels = new Dictionary<string, string>
                    {
                        ["aspire-owner"] = "owner",
                        ["aspire-resource"] = "frontend-sandbox-container"
                    }
                }
            });
        }

        public Task DeleteSandboxAsync(
            AzureDevComputeResourceScope scope,
            string sandboxId,
            CancellationToken cancellationToken)
        {
            DeleteSandboxCalled = true;
            throw new HttpRequestException("connection reset");
        }

        public Task<List<AzureDevComputeDiskImage>> ListDiskImagesAsync(
            AzureDevComputeResourceScope scope,
            string? labels,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new List<AzureDevComputeDiskImage>());
        }

        public Task<AzureDevComputeDiskImage> CreateDiskImageAsync(AzureDevComputeResourceScope scope, AzureDevComputeCreateDiskImageRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AzureDevComputeDiskImage> GetDiskImageAsync(AzureDevComputeResourceScope scope, string diskImageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteDiskImageAsync(AzureDevComputeResourceScope scope, string diskImageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AzureDevComputeSandbox> CreateSandboxAsync(AzureDevComputeResourceScope scope, AzureDevComputeSandboxRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AzureDevComputeSandbox> SetLifecycleAsync(AzureDevComputeResourceScope scope, string sandboxId, AzureDevComputeSandboxLifecyclePolicy lifecycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<List<AzureDevComputeSandboxPort>> AddPortAsync(AzureDevComputeResourceScope scope, string sandboxId, AzureDevComputeAddPortRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<List<AzureDevComputeSandboxPort>> RemovePortAsync(AzureDevComputeResourceScope scope, string sandboxId, AzureDevComputeRemovePortRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
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
