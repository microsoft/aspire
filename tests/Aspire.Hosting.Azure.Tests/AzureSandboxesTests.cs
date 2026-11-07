// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CS0618 // Retain legacy ProjectResource Azure publishing/provisioning contracts with synthetic metadata.
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREAZURE001

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Utils;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Provisioning.Resources;
using Microsoft.Azure.ContainerApps.Sandbox;
using Microsoft.Azure.ContainerApps.Sandbox.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSandboxesTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(EndpointProperty.BrowserOrigins, "https://api.example.test")]
    [InlineData(EndpointProperty.PrimaryBrowserOrigin, "https://api.example.test")]
    public async Task BrowserOriginsUseDeployedSandboxProxyAuthority(EndpointProperty property, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        builder.Services.AddSingleton<IDeploymentStateManager>(new InMemoryDeploymentStateManager());
        var environment = builder.AddAzureSandboxGroup("sandboxes");
        var api = builder.AddContainer("api", "image").WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints().PublishAsAzureSandbox();
        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        var target = Assert.IsType<AzureSandboxContainerResource>(api.Resource.GetDeploymentTargetAnnotation(environment.Resource)!.DeploymentTarget);
        var manager = app.Services.GetRequiredService<IDeploymentStateManager>();
        var state = await manager.AcquireSectionAsync(AzureSandboxContainerDeployment.GetStateSectionName(target), default);
        state.Data["Ports"] = new JsonArray(new JsonObject
        {
            ["Name"] = "http",
            ["Port"] = 8080,
            ["Url"] = "https://api.example.test:443/path?query=value#fragment"
        });
        await manager.SaveSectionAsync(state, default);
        Assert.True(AzureSandboxContainerDeployment.TryResolveEndpointReferenceValue(
            api.GetEndpoint("http").Property(property), environment.Resource, out var value));
        var context = new ValueProviderContext { ExecutionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>() };

        Assert.Equal(expected, await value.GetValueAsync(context, default));
    }

    [Fact]
    public void AzureSandboxGroupUsesExplicitOutputReferenceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");

        Assert.Equal("{sandboxes.outputs.id}", sandboxGroup.Resource.IdOutputReference.ValueExpression);
        Assert.Equal("{sandboxes.outputs.name}", sandboxGroup.Resource.NameOutputReference.ValueExpression);
    }

    [Fact]
    public async Task AzureSandboxGroupsAddDashboardLinksToDeploymentSummary()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        var firstGroup = builder.AddAzureSandboxGroup("first");
        firstGroup.Resource.Outputs["id"] = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/example-rg/providers/Microsoft.App/sandboxGroups/first-group";
        firstGroup.Resource.Outputs["location"] = "westus3";
        var secondGroup = builder.AddAzureSandboxGroup("second");
        secondGroup.Resource.Outputs["id"] = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/other-rg/providers/Microsoft.App/sandboxGroups/second-group";
        secondGroup.Resource.Outputs["location"] = "eastus2";

        using var app = builder.Build();
        var firstSteps = await CreateStepsAsync(app, firstGroup.Resource);
        var firstSummaryStep = Assert.Single(firstSteps, step => step.Name == "print-azure-sandboxes-dashboard-first");
        Assert.Contains(AzureEnvironmentResource.ProvisionInfrastructureStepName, firstSummaryStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, firstSummaryStep.RequiredBySteps);
        Assert.Contains("print-summary", firstSummaryStep.Tags);

        var secondSteps = await CreateStepsAsync(app, secondGroup.Resource);
        var secondSummaryStep = Assert.Single(secondSteps, step => step.Name == "print-azure-sandboxes-dashboard-second");

        var pipelineContext = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await using var firstReportingStep = await new NullPublishingActivityReporter().CreateStepAsync("first");
        await firstSummaryStep.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = firstReportingStep
        });
        await using var secondReportingStep = await new NullPublishingActivityReporter().CreateStepAsync("second");
        await secondSummaryStep.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = secondReportingStep
        });

        Assert.Collection(
            pipelineContext.Summary.Items,
            item =>
            {
                const string expectedUrl = "https://sandboxes.azure.com/sandbox-groups/11111111-1111-1111-1111-111111111111/example-rg/first-group";
                Assert.Equal("first sandbox dashboard", item.Key);
                Assert.Equal($"[{expectedUrl}]({expectedUrl})", item.Value);
                Assert.True(item.EnableMarkdown);
            },
            item =>
            {
                const string expectedUrl = "https://sandboxes.azure.com/sandbox-groups/22222222-2222-2222-2222-222222222222/other-rg/second-group";
                Assert.Equal("second sandbox dashboard", item.Key);
                Assert.Equal($"[{expectedUrl}]({expectedUrl})", item.Value);
                Assert.True(item.EnableMarkdown);
            });
    }

    [Fact]
    public async Task ExcludedAzureSandboxGroupDoesNotAddDashboardLinkToDeploymentSummary()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .ExcludeFromManifest();

        using var app = builder.Build();
        var steps = await CreateStepsAsync(app, sandboxGroup.Resource);
        var summaryStep = Assert.Single(steps, step => step.Name == "print-azure-sandboxes-dashboard-sandboxes");
        var pipelineContext = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("summary");

        await summaryStep.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });

        Assert.Empty(pipelineContext.Summary.Items);
    }

    [Fact]
    public void AzureSandboxDashboardUrlEscapesRouteSegments()
    {
        Assert.Equal(
            "https://sandboxes.azure.com/sandbox-groups/subscription%20id/resource%20group/sandbox%2Fgroup",
            AzureSandboxGroupResource.GetDashboardUrl("subscription id", "resource group", "sandbox/group"));
    }

    [Fact]
    public void ExistingSandboxDataPlaneScopeUsesActualResourceOutputs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        sandboxGroup.Resource.Outputs["id"] = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/existing-rg/providers/Microsoft.App/sandboxGroups/existing-group";
        sandboxGroup.Resource.Outputs["location"] = "eastus2";

        var scope = AzureSandboxContainerDeployment.CreateDataPlaneScope(sandboxGroup.Resource);

        Assert.Equal("11111111-1111-1111-1111-111111111111", scope.SubscriptionId);
        Assert.Equal("existing-rg", scope.ResourceGroupName);
        Assert.Equal("existing-group", scope.SandboxGroupName);
        Assert.Equal("eastus2", scope.Region);
    }

    [Fact]
    public async Task AddAzureSandboxResourcesGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

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
    public async Task PublishGeneratesDeploymentPrincipalParameters()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            output,
            workspace.Path);

        builder.AddAzureSandboxGroup("sandboxes");

        using var app = builder.Build();
        app.Run();

        var mainBicep = File.ReadAllText(Path.Combine(workspace.Path, "main.bicep"));
        await Verify(mainBicep, "bicep");
    }

    [Fact]
    public void SandboxGroupUsesGeneratedDefaults()
    {
        var resource = new Aspire.Hosting.Azure.Sandboxes.Provisioning.SandboxGroup("sandboxes");
        var existing = Aspire.Hosting.Azure.Sandboxes.Provisioning.SandboxGroup.FromExisting("existing");
        var generated = new global::Azure.Provisioning.AppContainers.SandboxGroup("generated");

        Assert.Equal(generated.ResourceVersion, resource.ResourceVersion);
        Assert.Equal(generated.ResourceVersion, existing.ResourceVersion);
        Assert.Equivalent(generated.GetResourceNameRequirements(), resource.GetResourceNameRequirements());
        Assert.True(existing.IsExistingResource);
    }

    [Fact]
    public async Task SandboxGroupNamesPreserveDigits()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandbox1 = builder.AddAzureSandboxGroup("sandbox1");
        var sandbox2 = builder.AddAzureSandboxGroup("sandbox2");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var (_, sandbox1Bicep) = await AzureManifestUtils.GetManifestWithBicep(model, sandbox1.Resource);
        var (_, sandbox2Bicep) = await AzureManifestUtils.GetManifestWithBicep(model, sandbox2.Resource);

        Assert.Contains("name: take('sandbox1-${uniqueString(resourceGroup().id)}', 24)", sandbox1Bicep, StringComparison.Ordinal);
        Assert.Contains("name: take('sandbox2-${uniqueString(resourceGroup().id)}', 24)", sandbox2Bicep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAzureSandboxGroupSupportsExplicitManagedIdentities()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var identity = builder.AddAzureUserAssignedIdentity("nodeidentity");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .WithSystemAssignedIdentity()
            .WithUserAssignedIdentity(identity);

        builder.AddContainer("node", "node", "22-alpine")
            .WithAzureUserAssignedIdentity(identity)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox();

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        Assert.Equal(ManagedServiceIdentityType.SystemAssignedUserAssigned, sandboxGroup.Resource.WorkloadManagedIdentityType);
        Assert.Equal(identity.Resource, Assert.Single(sandboxGroup.Resource.WorkloadUserAssignedIdentities));
    }

    [Fact]
    public async Task SandboxGroupWithoutWorkloadIdentityEmitsImagePullIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .WithNoManagedIdentity();

        var (_, bicep) = await AzureManifestUtils.GetManifestWithBicep(sandboxGroup.Resource, skipPreparer: true);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task SandboxGroupWithSystemAssignedWorkloadIdentityAlsoEmitsImagePullIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .WithSystemAssignedIdentity();

        var (_, bicep) = await AzureManifestUtils.GetManifestWithBicep(sandboxGroup.Resource, skipPreparer: true);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithNoManagedIdentityClearsGroupIdentityButPreservesComputeWorkloadIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var clearedIdentity = builder.AddAzureUserAssignedIdentity("cleared-identity");
        var computeIdentity = builder.AddAzureUserAssignedIdentity("compute-identity");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .WithUserAssignedIdentity(clearedIdentity)
            .WithNoManagedIdentity();
        builder.AddContainer("worker", "image")
            .WithAnnotation(new AppIdentityAnnotation(computeIdentity.Resource))
            .PublishAsAzureSandbox();

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        Assert.Equal(ManagedServiceIdentityType.UserAssigned, sandboxGroup.Resource.WorkloadManagedIdentityType);
        Assert.Equal(computeIdentity.Resource, Assert.Single(sandboxGroup.Resource.WorkloadUserAssignedIdentities));

        var (_, bicep) = await AzureManifestUtils.GetManifestWithBicep(sandboxGroup.Resource, skipPreparer: true);
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task SandboxGroupAggregatesWorkloadManagedIdentities()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var identity = builder.AddAzureUserAssignedIdentity("workload-identity");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var otherGroup = builder.AddAzureSandboxGroup("other-sandboxes");
        builder.AddContainer("worker", "image")
            .WithAnnotation(new AppIdentityAnnotation(identity.Resource))
            .WithComputeEnvironment(sandboxGroup)
            .PublishAsAzureSandbox();

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        Assert.Equal(ManagedServiceIdentityType.UserAssigned, sandboxGroup.Resource.WorkloadManagedIdentityType);
        Assert.Equal(identity.Resource, Assert.Single(sandboxGroup.Resource.WorkloadUserAssignedIdentities));
        Assert.Empty(otherGroup.Resource.WorkloadUserAssignedIdentities);
        var (_, bicep) = await AzureManifestUtils.GetManifestWithBicep(sandboxGroup.Resource, skipPreparer: true);
        Assert.Contains("userAssignedIdentities", bicep, StringComparison.Ordinal);
        Assert.Contains("workload_identity_outputs_id", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingSandboxGroupRejectsWorkloadIdentityAttachment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var identity = builder.AddAzureUserAssignedIdentity("workload-identity");
        var pullIdentity = builder.AddAzureUserAssignedIdentity("pull-identity")
            .PublishAsExisting("existing-pull-identity", "existing-rg");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .WithAcrPullIdentity(pullIdentity)
            .PublishAsExisting("existing-sandboxes", "existing-rg");
        builder.AddContainer("worker", "image")
            .WithAnnotation(new AppIdentityAnnotation(identity.Resource))
            .PublishAsAzureSandbox();

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default));
        Assert.Equal(
            "Compute resource 'worker' uses managed identity 'workload-identity', but workload identities are not supported when publishing to existing Azure sandbox group 'sandboxes'.",
            exception.InnerException?.Message);
    }

    [Fact]
    public async Task ExistingAzureSandboxGroupDoesNotAddDeploymentPrincipalRoleAssignment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var pullIdentity = builder.AddAzureUserAssignedIdentity("pull-identity")
            .PublishAsExisting("existing-pull-identity", "existing-rg");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .WithAcrPullIdentity(pullIdentity)
            .PublishAsExisting("existing-sandboxes", "existing-rg");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (_, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, sandboxGroup.Resource);

        var annotation = Assert.Single(sandboxGroup.Resource.Annotations.OfType<AzureSandboxGroupAcrPullIdentityAnnotation>());
        Assert.Same(pullIdentity.Resource, annotation.Identity);
        Assert.DoesNotContain("roleAssignments", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("Container Apps SandboxGroup Data Owner", bicep, StringComparison.Ordinal);
        Assert.Contains("pull_identity_outputs_id", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_identity_outputs_clientid", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingAzureSandboxGroupRejectsNewAcrPullIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var pullIdentity = builder.AddAzureUserAssignedIdentity("pull-identity");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .WithAcrPullIdentity(pullIdentity)
            .PublishAsExisting("existing-sandboxes", "existing-rg");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.GetManifestWithBicep(model, sandboxGroup.Resource));
        Assert.Equal(
            "Existing Azure sandbox group 'sandboxes' requires a user-assigned ACR pull identity. " +
            "Call 'WithAcrPullIdentity' with an identity that is already attached to the sandbox group and has AcrPull on the configured registry.",
            exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureSandboxGroupRejectsReusingAcrPullIdentityForWorkloads(bool configurePullIdentityFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var identity = builder.AddAzureUserAssignedIdentity("shared-identity");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        if (configurePullIdentityFirst)
        {
            sandboxGroup.WithAcrPullIdentity(identity);
            sandboxGroup.WithUserAssignedIdentity(identity);
        }
        else
        {
            sandboxGroup.WithUserAssignedIdentity(identity);
            sandboxGroup.WithAcrPullIdentity(identity);
        }

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default));
        Assert.Equal(
            "Azure sandbox group 'sandboxes' uses identity 'shared-identity' for both image pulls and workloads. " +
            "Use a dedicated image-pull identity so its AcrPull permission is not exposed to sandbox workloads.",
            exception.InnerException?.Message);
    }

    [Fact]
    public async Task CrossResourceGroupRegistryUsesStandaloneAcrPullIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var registry = builder.AddAzureContainerRegistry("registry")
            .PublishAsExisting("existing-acr", "existing-rg");
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
            .WithAzureContainerRegistry(registry);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var identity = Assert.Single(
            model.Resources.OfType<AzureUserAssignedIdentityResource>(),
            resource => resource.Name == "sandboxes-mi");
        var roles = Assert.Single(
            model.Resources.OfType<AzureRoleAssignmentResource>(),
            resource => resource.Name == "sandboxes-mi-roles-registry");
        var annotation = Assert.Single(sandboxGroup.Resource.Annotations.OfType<AzureSandboxGroupAcrPullIdentityAnnotation>());

        Assert.Same(registry.Resource, roles.TargetAzureResource);
        Assert.Same(identity, annotation.Identity);
        Assert.Contains(identity, sandboxGroup.Resource.References);

        var (_, sandboxBicep) = await AzureManifestUtils.GetManifestWithBicep(sandboxGroup.Resource, skipPreparer: true);
        var (_, identityBicep) = await AzureManifestUtils.GetManifestWithBicep(identity, skipPreparer: true);
        var (rolesManifest, rolesBicep) = await AzureManifestUtils.GetManifestWithBicep(roles, skipPreparer: true);

        Assert.Contains("param sandboxes_mi_outputs_id string", sandboxBicep, StringComparison.Ordinal);
        Assert.DoesNotContain("sandboxes_mi_outputs_clientid", sandboxBicep, StringComparison.Ordinal);
        Assert.Contains("'${sandboxes_mi_outputs_id}': { }", sandboxBicep, StringComparison.Ordinal);
        Assert.Contains("output imagePullIdentityId string = sandboxes_mi_outputs_id", sandboxBicep, StringComparison.Ordinal);
        Assert.Contains("Microsoft.ManagedIdentity/userAssignedIdentities", identityBicep, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Authorization/roleAssignments", rolesBicep, StringComparison.Ordinal);
        Assert.Equal("existing-rg", rolesManifest["scope"]?["resourceGroup"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExistingAzureSandboxGroupRequiresAcrPullIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        builder.AddAzureSandboxGroup("sandboxes")
            .PublishAsExisting("existing-sandboxes", "existing-rg");

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default));
        Assert.Equal(
            "Existing Azure sandbox group 'sandboxes' requires a user-assigned ACR pull identity. " +
            "Call 'WithAcrPullIdentity' with an identity that is already attached to the sandbox group and has AcrPull on the configured registry.",
            exception.Message);
    }

    [Fact]
    public void PublishAsAzureSandboxDoesNotAddDeploymentTargetInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var container = builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0");
        var configureCalled = false;
        var buildOptionsCallbackCount = container.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count();

        container.PublishAsAzureSandbox(options => configureCalled = true);
        container.PublishAsAzureSandbox(new AzureSandboxOptions
        {
            AutoSuspendMode = (AzureSandboxAutoSuspendMode)(-1)
        });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");

        Assert.DoesNotContain(model.Resources, resource => resource is AzureSandboxGroupResource or AzureSandboxCleanupResource);
        Assert.DoesNotContain(model.Resources, resource => resource.Name == "sandboxes-acr");
        Assert.Null(computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource));
        Assert.False(configureCalled);
        Assert.Equal(buildOptionsCallbackCount, container.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count());
    }

    [Fact]
    public async Task SandboxImagePullIdentityIsOnlyUsedForConfiguredAcr()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        sandboxGroup.Resource.ContainerRegistry!.Outputs["loginServer"] = "example.azurecr.io";
        sandboxGroup.Resource.Outputs[AzureSandboxGroupResource.ImagePullIdentityIdOutputName] = PullIdentityResourceId;

        var acrIdentity = await AzureSandboxContainerDeployment.ResolveImagePullIdentityResourceIdAsync(
            sandboxGroup.Resource,
            "example.azurecr.io/site@sha256:abc123",
            CancellationToken.None);
        var publicIdentity = await AzureSandboxContainerDeployment.ResolveImagePullIdentityResourceIdAsync(
            sandboxGroup.Resource,
            "docker.io/library/nginx@sha256:def456",
            CancellationToken.None);

        Assert.Equal(PullIdentityResourceId, acrIdentity);
        Assert.Null(publicIdentity);
    }

    [Fact]
    public async Task SandboxGroupClientUsesRegionalEndpointAndManagementScope()
    {
        var credential = new RecordingTokenCredential();
        var client = CreateSandboxClient(
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("management.westus3.azuredevcompute.io", request.RequestUri?.Host);
                Assert.Equal("/subscriptions/sub/resourceGroups/rg/sandboxGroups/sg/diskimages/disk-1", request.RequestUri?.AbsolutePath);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
                return Task.FromResult(JsonResponse("""{ "id": "disk-1", "labels": {}, "status": { "state": "Ready" } }"""));
            },
            credential);

        var diskImage = (await client.GetDiskImageResource("disk-1").GetAsync()).Value;

        Assert.Equal("disk-1", diskImage.Id);
        Assert.Equal(["https://management.azuredevcompute.io/.default"], credential.Scopes);
    }

    [Fact]
    public async Task SandboxDiskImageUsesUserAssignedIdentityForConfiguredAcr()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        sandboxGroup.Resource.ContainerRegistry!.Outputs["loginServer"] = "example.azurecr.io";
        sandboxGroup.Resource.Outputs[AzureSandboxGroupResource.ImagePullIdentityIdOutputName] = PullIdentityResourceId;
        var sandboxContainer = new AzureSandboxContainerResource(
            "frontend-sandbox-container",
            builder.AddContainer("frontend", "example/image").Resource,
            sandboxGroup.Resource);
        using var app = builder.Build();
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = CreateStepContext(app, reportingStep);

        var requests = new List<JsonDocument>();
        var client = CreateSandboxClient(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/subscriptions/sub/resourceGroups/rg/sandboxGroups/sg/diskimages", request.RequestUri?.AbsolutePath);
            requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()));
            return JsonResponse("""{ "id": "disk-1", "labels": {}, "status": { "state": "Creating" } }""", HttpStatusCode.Created);
        });

        var acrDiskImage = await AzureSandboxContainerDeployment.CreateDiskImageAsync(
            stepContext,
            client,
            sandboxContainer,
            "example.azurecr.io/site@sha256:abc123",
            "site-1234",
            "owner-1",
            "deploy-1");
        await AzureSandboxContainerDeployment.CreateDiskImageAsync(
            stepContext,
            client,
            sandboxContainer,
            "docker.io/library/nginx@sha256:def456",
            "nginx-1234",
            "owner-1",
            "deploy-1");

        Assert.Equal("disk-1", acrDiskImage.Id);
        Assert.Equal(2, requests.Count);

        var acrRequest = requests[0].RootElement;
        Assert.Equal("site-1234", acrRequest.GetProperty("name").GetString());
        Assert.Equal("owner-1", acrRequest.GetProperty("labels").GetProperty("aspire-owner").GetString());
        Assert.Equal("frontend-sandbox-container", acrRequest.GetProperty("labels").GetProperty("aspire-resource").GetString());
        Assert.Equal("frontend", acrRequest.GetProperty("labels").GetProperty("aspire-source").GetString());
        Assert.Equal("deploy-1", acrRequest.GetProperty("labels").GetProperty("aspire-deploy").GetString());
        var acrSource = acrRequest.GetProperty("source");
        Assert.Equal("registry", acrSource.GetProperty("kind").GetString());
        Assert.Equal("example.azurecr.io/site@sha256:abc123", acrSource.GetProperty("imageUrl").GetString());
        var identity = acrSource.GetProperty("authentication").GetProperty("identity");
        Assert.Equal("UserAssigned", identity.GetProperty("type").GetString());
        Assert.Equal(PullIdentityResourceId, identity.GetProperty("identityResourceId").GetString());

        // The service rejects registry authentication for public registries.
        var publicSource = requests[1].RootElement.GetProperty("source");
        Assert.Equal("docker.io/library/nginx@sha256:def456", publicSource.GetProperty("imageUrl").GetString());
        Assert.False(publicSource.TryGetProperty("authentication", out _));
    }

    [Fact]
    public async Task SandboxCreateRequestIncludesContainerMetadataEgressAndIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .PublishAsAzureSandbox(new AzureSandboxOptions { Tier = AzureSandboxTier.Large });
        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(
            computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = CreateStepContext(app, reportingStep);

        JsonDocument? requestBody = null;
        var client = CreateSandboxClient(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/subscriptions/sub/resourceGroups/rg/sandboxGroups/sg/sandboxes", request.RequestUri?.AbsolutePath);
            requestBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return JsonResponse("""{ "id": "sandbox-1", "labels": {}, "ports": [] }""", HttpStatusCode.Created);
        });

        var sandbox = await AzureSandboxContainerDeployment.CreateSandboxAsync(
            stepContext,
            client,
            sandboxContainer,
            "disk-1",
            new Dictionary<string, string> { ["ASPNETCORE_URLS"] = "http://+:5000" },
            new AzureSandboxContainerDeployment.ContainerImageMetadata(
                ["dotnet", "/app/app.dll"],
                ["--urls"],
                new Dictionary<string, string>(),
                new HashSet<string>()),
            [new IdentitySetting(PullIdentityResourceId) { Lifecycle = IdentitySettingLifecycle.All }],
            AzureSandboxContainerDeployment.CreateEgressPolicy(["api.example.test"]),
            "owner-1",
            "deploy-1");

        Assert.Equal("sandbox-1", sandbox.Id);
        Assert.NotNull(requestBody);
        var root = requestBody.RootElement;
        Assert.Equal("disk-1", root.GetProperty("sourcesRef").GetProperty("diskImage").GetProperty("id").GetString());
        Assert.False(root.GetProperty("sourcesRef").GetProperty("diskImage").GetProperty("isPublic").GetBoolean());
        Assert.Equal("2000m", root.GetProperty("resources").GetProperty("cpu").GetString());
        Assert.Equal("4096Mi", root.GetProperty("resources").GetProperty("memory").GetString());
        Assert.Equal("40960Mi", root.GetProperty("resources").GetProperty("disk").GetString());
        Assert.Equal(["dotnet", "/app/app.dll"], root.GetProperty("entrypoint").EnumerateArray().Select(static item => item.GetString()).ToArray());
        Assert.Equal(["--urls"], root.GetProperty("cmd").EnumerateArray().Select(static item => item.GetString()).ToArray());
        Assert.Equal("http://+:5000", root.GetProperty("environment").GetProperty("ASPNETCORE_URLS").GetString());
        Assert.Equal("owner-1", root.GetProperty("labels").GetProperty("aspire-owner").GetString());
        var identitySetting = Assert.Single(root.GetProperty("identitySettings").EnumerateArray());
        Assert.Equal(PullIdentityResourceId, identitySetting.GetProperty("identity").GetString());
        Assert.Equal("All", identitySetting.GetProperty("lifecycle").GetString());
        var egress = root.GetProperty("egressPolicy");
        Assert.Equal("Deny", egress.GetProperty("defaultAction").GetString());
        Assert.Equal("Full", egress.GetProperty("trafficInspection").GetString());
        var hostRule = Assert.Single(egress.GetProperty("hostRules").EnumerateArray());
        Assert.Equal("api.example.test", hostRule.GetProperty("pattern").GetString());
        Assert.Equal("Allow", hostRule.GetProperty("action").GetString());
    }

    [Fact]
    public async Task SandboxCreateRejectsResponsesWithoutResourceIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        sandboxGroup.Resource.ContainerRegistry!.Outputs["loginServer"] = "example.azurecr.io";
        var sandboxContainer = new AzureSandboxContainerResource(
            "frontend-sandbox-container",
            builder.AddContainer("frontend", "example/image").Resource,
            sandboxGroup.Resource);
        using var app = builder.Build();
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = CreateStepContext(app, reportingStep);
        var client = CreateSandboxClient(_ => Task.FromResult(JsonResponse("""{ "labels": {} }""", HttpStatusCode.Created)));

        var exception = await Assert.ThrowsAsync<SandboxCreateResponseException>(() =>
            AzureSandboxContainerDeployment.CreateDiskImageAsync(
                stepContext,
                client,
                sandboxContainer,
                "docker.io/library/nginx@sha256:def456",
                "nginx-1234",
                "owner-1",
                "deploy-1"));

        Assert.True(SandboxGroupClientFactory.MayHaveCreatedResource(exception));
    }

    [Fact]
    public async Task SandboxClientRetriesForbiddenResponses()
    {
        var attempts = 0;
        var client = CreateSandboxClient(_ =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : JsonResponse("""{ "id": "disk-1", "labels": {}, "status": { "state": "Ready" } }"""));
        });

        var diskImage = (await client.GetDiskImageResource("disk-1").GetAsync()).Value;

        Assert.Equal("disk-1", diskImage.Id);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SandboxClientBoundsForbiddenRetries()
    {
        var attempts = 0;
        var client = CreateSandboxClient(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        });

        var exception = await Assert.ThrowsAsync<RequestFailedException>(() => client.GetDiskImageResource("disk-1").GetAsync());

        Assert.Equal((int)HttpStatusCode.Forbidden, exception.Status);
        Assert.Equal(21, attempts);
    }

    [Fact]
    public async Task SandboxClientRetriesThrottledAndServerResponsesForReads()
    {
        var statuses = new Queue<HttpStatusCode>(
        [
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK
        ]);
        var client = CreateSandboxClient(_ =>
        {
            var status = statuses.Dequeue();
            return Task.FromResult(status == HttpStatusCode.OK
                ? JsonResponse("""{ "id": "disk-1", "labels": {}, "status": { "state": "Ready" } }""")
                : new HttpResponseMessage(status));
        });

        var diskImage = (await client.GetDiskImageResource("disk-1").GetAsync()).Value;

        Assert.Equal("disk-1", diskImage.Id);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task SandboxClientRetriesTransientNetworkErrorsForReads()
    {
        var attempts = 0;
        var client = CreateSandboxClient(_ =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"))
                : Task.FromResult(JsonResponse("""{ "id": "disk-1", "labels": {}, "status": { "state": "Ready" } }"""));
        });

        var diskImage = (await client.GetDiskImageResource("disk-1").GetAsync()).Value;

        Assert.Equal("disk-1", diskImage.Id);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SandboxClientDoesNotRetryAmbiguousCreateNetworkErrors()
    {
        var attempts = 0;
        var client = CreateSandboxClient(_ =>
        {
            attempts++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"));
        });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => client.CreateDiskImageAsync(CreateDiskImageContent()));

        Assert.Equal(1, attempts);
        Assert.True(SandboxGroupClientFactory.MayHaveCreatedResource(exception));
    }

    [Fact]
    public async Task SandboxClientDoesNotRetryAmbiguousCreateServerErrors()
    {
        var attempts = 0;
        var client = CreateSandboxClient(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        });

        var exception = await Assert.ThrowsAsync<RequestFailedException>(() => client.CreateDiskImageAsync(CreateDiskImageContent()));

        Assert.Equal(1, attempts);
        Assert.True(SandboxGroupClientFactory.MayHaveCreatedResource(exception));
    }

    [Fact]
    public async Task SandboxClientMarksRejectedCreateResponsesAsDefinite()
    {
        var attempts = 0;
        var client = CreateSandboxClient(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        });

        var exception = await Assert.ThrowsAsync<RequestFailedException>(() => client.CreateDiskImageAsync(CreateDiskImageContent()));

        Assert.Equal(1, attempts);
        Assert.False(SandboxGroupClientFactory.MayHaveCreatedResource(exception));
    }

    [Fact]
    public async Task SandboxClientMarksMalformedCreateResponsesAsAmbiguous()
    {
        var client = CreateSandboxClient(_ => Task.FromResult(JsonResponse("{ not-json", HttpStatusCode.Created)));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => client.CreateDiskImageAsync(CreateDiskImageContent()));

        Assert.True(SandboxGroupClientFactory.MayHaveCreatedResource(exception));
    }

    [Fact]
    public void SandboxCreateFailuresThatAreNotHttpFailuresAreDefinite()
    {
        Assert.False(SandboxGroupClientFactory.MayHaveCreatedResource(new InvalidOperationException("missing output")));
        Assert.True(SandboxGroupClientFactory.MayHaveCreatedResource(new OperationCanceledException()));
    }

    [Fact]
    public async Task SandboxClientDoesNotExposeErrorBodies()
    {
        const string secret = "resolved-secret-environment-value";
        var client = CreateSandboxClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new
            {
                title = $"Invalid value: {secret}",
                detail = $"The request contained {secret}."
            })
        }));

        var exception = await Assert.ThrowsAsync<RequestFailedException>(() => client.GetDiskImageResource("disk-1").GetAsync());

        Assert.DoesNotContain(secret, exception.Message);
        Assert.Equal((int)HttpStatusCode.BadRequest, exception.Status);
    }

    [Fact]
    public async Task SandboxClientTreatsMissingDeletedResourcesAsSuccess()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        using var app = builder.Build();
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = CreateStepContext(app, reportingStep);
        var client = CreateSandboxClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var state = new DeploymentStateSection("sandbox", new JsonObject
        {
            ["SandboxId"] = "sandbox-1",
            ["DiskImageId"] = "disk-1",
            ["Ports"] = new JsonArray(new JsonObject { ["Port"] = 8080 })
        }, version: 0);

        await AzureSandboxContainerDeployment.DeleteExistingDeploymentAsync(stepContext, client, state, throwOnError: true);
    }

    [Fact]
    public void SandboxPortAuthDefaultsToEntraIdAndOnlyAllowsAnonymousExplicitly()
    {
        var secure = AzureSandboxContainerDeployment.CreatePortAuthConfig(anonymous: false);
        var anonymous = AzureSandboxContainerDeployment.CreatePortAuthConfig(anonymous: true);

        Assert.Null(secure.Anonymous);
        Assert.True(secure.EntraId?.Enabled);
        Assert.True(anonymous.Anonymous);
        Assert.Null(anonymous.EntraId);
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
            new Dictionary<string, string>
            {
                ["aspire-owner"] = "owner-1",
                ["aspire-resource"] = "frontend-sandbox-container"
            },
            AzureSandboxContainerDeployment.CreateLabelFilter("owner-1", "frontend-sandbox-container"));
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
            new SandboxDataPlaneScope("SUB-1", "RG-1", "SANDBOXES-1", "WESTUS3"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AzureSandboxContainerDeployment.ValidateDeploymentScope(
                state,
                new SandboxDataPlaneScope("sub-1", "rg-1", "sandboxes-2", "westus3")));

        Assert.Contains("aspire destroy", exception.Message);
    }

    [Fact]
    public void SandboxStableOwnerUsesAppHostPathIdentityAndPreservesIsolation()
    {
        using var firstPolyglotBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        using var secondPolyglotBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        firstPolyglotBuilder.Configuration["AppHost:DeploymentStatePathSha256"] = "POLYGLOT-APPHOST-PATH-ONE";
        secondPolyglotBuilder.Configuration["AppHost:DeploymentStatePathSha256"] = "POLYGLOT-APPHOST-PATH-TWO";

        var scope = new SandboxDataPlaneScope("sub", "rg", "sandboxes", "westus3");
        var firstAppHostIdentity = AzureSandboxContainerDeployment.GetStableAppHostIdentity(firstPolyglotBuilder.Configuration);
        var secondAppHostIdentity = AzureSandboxContainerDeployment.GetStableAppHostIdentity(secondPolyglotBuilder.Configuration);
        var owner = AzureSandboxContainerDeployment.CreateStableOwnerId(firstAppHostIdentity, "Production", scope, "frontend-sandbox-container");
        var freshRunOwner = AzureSandboxContainerDeployment.CreateStableOwnerId(firstAppHostIdentity.ToLowerInvariant(), "production", scope, "frontend-sandbox-container");
        var otherEnvironmentOwner = AzureSandboxContainerDeployment.CreateStableOwnerId(firstAppHostIdentity, "Staging", scope, "frontend-sandbox-container");
        var otherAppOwner = AzureSandboxContainerDeployment.CreateStableOwnerId(secondAppHostIdentity, "Production", scope, "frontend-sandbox-container");
        var otherScopeOwner = AzureSandboxContainerDeployment.CreateStableOwnerId(
            firstAppHostIdentity,
            "Production",
            new SandboxDataPlaneScope("sub", "other-rg", "sandboxes", "westus3"),
            "frontend-sandbox-container");

        Assert.NotEqual(firstAppHostIdentity, secondAppHostIdentity);
        Assert.Equal(owner, freshRunOwner);
        Assert.NotEqual(owner, otherEnvironmentOwner);
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
    public void SandboxOwnerMigrationRetainsPendingOwnersAcrossRetries()
    {
        var state = new DeploymentStateSection(
            "Azure:Sandboxes:frontend",
            new JsonObject
            {
                ["OwnerId"] = "legacy-owner",
                ["PendingOwnerCleanupIds"] = new JsonArray("older-owner", "current-owner")
            },
            version: 0);

        var pendingOwnerIds = AzureSandboxContainerDeployment.GetPendingOwnerCleanupIds(
            state,
            "current-owner");

        Assert.Equal(
            new HashSet<string>(["legacy-owner", "older-owner"], StringComparer.Ordinal),
            pendingOwnerIds);
    }

    [Fact]
    public void SandboxLegacyOwnerMigrationUsesOnlyPersistedDeploymentIds()
    {
        var scope = new SandboxDataPlaneScope("sub", "rg", "sandboxes", "westus3");
        var legacyOwner = AzureSandboxContainerDeployment.CreateLegacyStableOwnerId(
            "apphost",
            scope,
            "frontend");
        var productionOwner = AzureSandboxContainerDeployment.CreateStableOwnerId(
            "apphost",
            "Production",
            scope,
            "frontend");
        var stagingOwner = AzureSandboxContainerDeployment.CreateStableOwnerId(
            "apphost",
            "Staging",
            scope,
            "frontend");
        var state = new DeploymentStateSection(
            "Azure:Sandboxes:frontend",
            new JsonObject
            {
                ["OwnerId"] = legacyOwner,
                ["SandboxId"] = "production-sandbox",
                ["DiskImageId"] = "production-disk"
            },
            version: 0);

        var broadCleanupOwners = AzureSandboxContainerDeployment.GetPendingOwnerCleanupIds(
            state,
            productionOwner,
            legacyOwner);
        var directCleanupState = AzureSandboxContainerDeployment.CreatePendingLegacyDeploymentCleanup(
            state,
            productionOwner,
            legacyOwner);

        Assert.NotEqual(productionOwner, stagingOwner);
        Assert.Empty(broadCleanupOwners);
        Assert.Equal("production-sandbox", directCleanupState?["SandboxId"]?.GetValue<string>());
        Assert.Equal("production-disk", directCleanupState?["DiskImageId"]?.GetValue<string>());
    }

    [Fact]
    public void SandboxStableOwnerRequiresCanonicalAppHostIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        builder.Configuration["AppHost:DeploymentStatePathSha256"] = null;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AzureSandboxContainerDeployment.GetStableAppHostIdentity(builder.Configuration));

        Assert.Contains("AppHost:DeploymentStatePathSha256", exception.Message);
    }

    [Fact]
    public void SandboxSecurityChangesDisablePreviousGenerationRetention()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
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
        const string imageReference = "example/image@sha256:first";
        var identitySettings = new[]
        {
            new IdentitySetting("/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/app")
            {
                Lifecycle = IdentitySettingLifecycle.All
            }
        };
        var egressPolicy = AzureSandboxContainerDeployment.CreateEgressPolicy(
            ["api.example.com"]);
        var fingerprint = AzureSandboxContainerDeployment.CreateDeploymentSecurityFingerprint(
            imageReference,
            endpoints,
            identitySettings,
            egressPolicy);
        var previousState = new DeploymentStateSection(
            "Azure:Sandboxes:frontend-sandbox-container",
            new JsonObject
            {
                ["OwnerId"] = "owner",
                ["SandboxId"] = "sandbox"
            },
            version: 0);

        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, fingerprint, hasRuntimeEnvironmentConfiguration: false));

        previousState.Data["EndpointSecurityFingerprint"] = fingerprint;
        Assert.False(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, fingerprint, hasRuntimeEnvironmentConfiguration: false));
        var updatedImageFingerprint = AzureSandboxContainerDeployment.CreateDeploymentSecurityFingerprint(
            "example/image@sha256:second",
            endpoints,
            identitySettings,
            egressPolicy);
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, updatedImageFingerprint, hasRuntimeEnvironmentConfiguration: false));
        previousState.Data["EndpointSecurityFingerprint"] = "legacy-endpoint-only-fingerprint";
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, fingerprint, hasRuntimeEnvironmentConfiguration: false));
        previousState.Data["EndpointSecurityFingerprint"] = fingerprint;
        previousState.Data["PendingSecurityCleanup"] = true;
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, fingerprint, hasRuntimeEnvironmentConfiguration: false));
        previousState.Data["PendingSecurityCleanup"] = false;

        var anonymousFingerprint = AzureSandboxContainerDeployment.CreateDeploymentSecurityFingerprint(
            imageReference,
        [
            endpoints[0] with { Anonymous = true }
        ],
            identitySettings,
            egressPolicy);
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, anonymousFingerprint, hasRuntimeEnvironmentConfiguration: false));

        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, fingerprint, hasRuntimeEnvironmentConfiguration: true));
        previousState.Data["HasRuntimeEnvironmentConfiguration"] = true;
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, fingerprint, hasRuntimeEnvironmentConfiguration: false));
        previousState.Data["HasRuntimeEnvironmentConfiguration"] = false;
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(
            previousState,
            fingerprint,
            hasRuntimeEnvironmentConfiguration: false,
            hasRuntimeCommandConfiguration: true));
        previousState.Data["HasRuntimeCommandConfiguration"] = true;
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, fingerprint, hasRuntimeEnvironmentConfiguration: false));
        previousState.Data["HasRuntimeCommandConfiguration"] = false;

        var updatedIdentityFingerprint = AzureSandboxContainerDeployment.CreateDeploymentSecurityFingerprint(
            imageReference,
            endpoints,
            [
                new IdentitySetting("/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/replacement")
                {
                    Lifecycle = IdentitySettingLifecycle.All
                }
            ],
            egressPolicy);
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, updatedIdentityFingerprint, hasRuntimeEnvironmentConfiguration: false));

        var updatedEgressFingerprint = AzureSandboxContainerDeployment.CreateDeploymentSecurityFingerprint(
            imageReference,
            endpoints,
            identitySettings,
            AzureSandboxContainerDeployment.CreateEgressPolicy(["other.example.com"]));
        Assert.True(AzureSandboxContainerDeployment.HasSecurityRelevantEndpointChange(previousState, updatedEgressFingerprint, hasRuntimeEnvironmentConfiguration: false));
    }

    [Fact]
    public async Task SandboxDeletionRunsAfterPortRemovalFailure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
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
        var service = new FailingPortRemovalService();
        var client = CreateSandboxClient(service.HandleAsync);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            AzureSandboxContainerDeployment.DeleteSandboxAsync(
                stepContext,
                client,
                "sandbox-1",
                [8080],
                throwOnError: true));

        Assert.Contains("port removal failed", exception.Message);
        Assert.True(service.DeleteSandboxCalled);
    }

    [Fact]
    public async Task ExistingDeploymentDeletesDiskImageAfterPortRemovalFailure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
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
        var service = new FailingPortRemovalService();
        var client = CreateSandboxClient(service.HandleAsync);
        var state = new DeploymentStateSection("sandbox", new JsonObject
        {
            ["SandboxId"] = "sandbox-1",
            ["DiskImageId"] = "disk-1",
            ["Ports"] = new JsonArray(new JsonObject { ["Port"] = 8080 })
        }, version: 0);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            AzureSandboxContainerDeployment.DeleteExistingDeploymentAsync(
                stepContext,
                client,
                state,
                throwOnError: true));

        Assert.Contains("port removal failed", exception.Message);
        Assert.True(service.DeleteSandboxCalled);
        Assert.True(service.DeleteDiskImageCalled);
    }

    [Fact]
    public async Task ExistingDeploymentContinuesCleanupAfterRequestTimeout()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
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
        var service = new FailingPortRemovalService(new TaskCanceledException("request timed out"));
        var client = CreateSandboxClient(service.HandleAsync);
        var state = new DeploymentStateSection("sandbox", new JsonObject
        {
            ["SandboxId"] = "sandbox-1",
            ["DiskImageId"] = "disk-1",
            ["Ports"] = new JsonArray(new JsonObject { ["Port"] = 8080 })
        }, version: 0);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            AzureSandboxContainerDeployment.DeleteExistingDeploymentAsync(
                stepContext,
                client,
                state,
                throwOnError: true));

        Assert.Contains("request timed out", exception.Message);
        Assert.True(service.DeleteSandboxCalled);
        Assert.True(service.DeleteDiskImageCalled);
    }

    [Fact]
    public async Task SandboxDestroyPropagatesFallbackCleanupFailure()
    {
        var stateManager = ProvisioningTestHelpers.CreateUserSecretsManager();
        var azureState = await stateManager.AcquireSectionAsync("Azure", TestContext.Current.CancellationToken);
        azureState.Data["SubscriptionId"] = "sub";
        azureState.Data["ResourceGroup"] = "rg";
        azureState.Data["Location"] = "westus3";
        await stateManager.SaveSectionAsync(azureState, TestContext.Current.CancellationToken);
        Uri? requestUri = null;
        var handler = new RecordingHandler(request =>
        {
            requestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new { message = "cleanup failed" })
            });
        });

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        sandboxGroup.Resource.Outputs["id"] = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/existing-rg/providers/Microsoft.App/sandboxGroups/existing-group";
        sandboxGroup.Resource.Outputs["location"] = "eastus2";
        var existingScope = AzureSandboxContainerDeployment.CreateDataPlaneScope(sandboxGroup.Resource);
        Assert.Equal("existing-rg", existingScope.ResourceGroupName);
        Assert.Equal("existing-group", existingScope.SandboxGroupName);
        var targetResource = builder.AddContainer("frontend", "example/image").Resource;
        var sandboxResource = new AzureSandboxContainerResource(
            "frontend-sandbox-container",
            targetResource,
            sandboxGroup.Resource);
        builder.Services.AddSingleton<IDeploymentStateManager>(stateManager);
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory(handler));

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

        var exception = await Assert.ThrowsAsync<RequestFailedException>(() =>
            AzureSandboxContainerDeployment.DestroyAsync(stepContext, sandboxResource));

        Assert.Equal((int)HttpStatusCode.BadRequest, exception.Status);
        Assert.NotNull(requestUri);
        Assert.Contains("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/existing-rg/", requestUri?.AbsolutePath);
        Assert.Contains("/sandboxGroups/existing-group/", requestUri?.AbsolutePath);
    }

    [Fact]
    public async Task SandboxDiskImageFailureRedactsServiceStatusDetails()
    {
        const string secret = "short-lived-acr-refresh-token";
        var client = CreateSandboxClient(_ => Task.FromResult(JsonResponse(
            $$"""{ "id": "disk-1", "labels": {}, "status": { "state": "Error", "errorMessage": "Authentication failed using {{secret}}" } }""")));
        var diskImage = (await client.GetDiskImageResource("disk-1").GetAsync()).Value;

        var exception = AzureSandboxContainerDeployment.CreateDiskImageFailureException(diskImage);

        Assert.Contains("terminal state: 'Error'", exception.Message);
        Assert.DoesNotContain(secret, exception.Message);
        Assert.Contains("Service-provided error details were redacted", exception.Message);
    }

    [Fact]
    public async Task SandboxBestEffortPruneSuppressesNetworkFailures()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
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
        var service = new FailingPruneService();
        var client = CreateSandboxClient(service.HandleAsync);

        await AzureSandboxContainerDeployment.DeleteRemoteDeploymentsByResourceLabelAsync(
            stepContext,
            client,
            "owner",
            "frontend-sandbox-container",
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            throwOnError: false);

        Assert.Equal("aspire-owner=owner,aspire-resource=frontend-sandbox-container", service.LabelFilter);
        Assert.True(service.DeleteSandboxCalled);
    }

    [Fact]
    public async Task SandboxDiskImageCreateResponseLossReconcilesLabeledDiskImage()
    {
        var service = await RunCreateResponseLossAsync(includeSandbox: false);

        Assert.False(service.DeleteSandboxCalled);
        Assert.True(service.DeleteDiskImageCalled);
    }

    [Fact]
    public async Task SandboxCreateResponseLossReconcilesLabeledResources()
    {
        var service = await RunCreateResponseLossAsync(includeSandbox: true);

        Assert.True(service.DeleteSandboxCalled);
        Assert.True(service.DeleteDiskImageCalled);
    }

    [Fact]
    public async Task SandboxCreateResponseLossWaitsForDelayedResourceVisibility()
    {
        var service = await RunCreateResponseLossAsync(includeSandbox: true, emptyPollsBeforeVisible: 4);

        Assert.True(service.DeleteSandboxCalled);
        Assert.True(service.DeleteDiskImageCalled);
    }

    [Fact]
    public async Task DigestPinnedSandboxImageReferencesAreInspected()
    {
        var runtime = new FakeContainerRuntime
        {
            InspectedImageDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            InspectedImageOperatingSystem = "linux",
            InspectedImageArchitecture = "amd64"
        };

        var reference = await AzureSandboxContainerDeployment.ResolveContainerImageReferenceForDiskImageAsync(
            runtime,
            "example.azurecr.io/site@sha256:index",
            CancellationToken.None);

        Assert.True(runtime.WasInspectImageManifestCalled);
        Assert.Equal(["example.azurecr.io/site@sha256:index"], runtime.InspectImageManifestCalls);
        Assert.Equal(
            "example.azurecr.io/site@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            reference);
    }

    [Fact]
    public async Task SandboxContainerOptionsMapToRuntimeRequestShapes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(new AzureSandboxOptions
            {
                Tier = AzureSandboxTier.Large,
                AutoSuspendEnabled = false,
                AutoSuspendInterval = TimeSpan.FromMinutes(5),
                AutoSuspendMode = AzureSandboxAutoSuspendMode.Disk,
                AutoDeleteEnabled = true,
                AutoDeleteInterval = TimeSpan.FromHours(1),
                AutoDeleteTrigger = AzureSandboxAutoDeleteTrigger.AfterSuspend,
                Endpoints =
                [
                    new AzureSandboxEndpointOptions
                    {
                        Name = "HTTP",
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
        Assert.Equal(300, lifecycle.AutoSuspendPolicy.IntervalSeconds);
        Assert.Equal("Disk", lifecycle.AutoSuspendPolicy.Mode?.ToString());
        Assert.NotNull(lifecycle.AutoDeletePolicy);
        Assert.True(lifecycle.AutoDeletePolicy.Enabled);
        Assert.Null(lifecycle.AutoDeletePolicy.DeleteIntervalInDays);
        Assert.Equal(3600, lifecycle.AutoDeletePolicy.DeleteIntervalInSeconds);
        Assert.Equal("AfterSuspend", lifecycle.AutoDeletePolicy.Trigger?.ToString());

        var egress = AzureSandboxContainerDeployment.CreateEgressPolicy(
        [
            "api.example.test",
            "account.blob.core.windows.net",
            "API.example.test",
            "*",
            "+",
            "0.0.0.0",
            "::"
        ]);
        Assert.Equal("Deny", egress.DefaultAction?.ToString());
        Assert.Equal("Full", egress.TrafficInspection?.ToString());
        Assert.Collection(
            egress.HostRules,
            hostRule =>
            {
                Assert.Equal("Allow", hostRule.Action?.ToString());
                Assert.Equal("account.blob.core.windows.net", hostRule.Pattern);
            },
            hostRule =>
            {
                Assert.Equal("Allow", hostRule.Action?.ToString());
                Assert.Equal("api.example.test", hostRule.Pattern);
            });

        var endpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal("Http", endpoint.Protocol);
        Assert.False(endpoint.Anonymous);
    }

    [Fact]
    public async Task SandboxAutoSuspendPolicyIsEmittedOnlyWhenExplicitlyConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var defaultResource = builder.AddContainer("default", "image")
            .PublishAsAzureSandbox();
        var disabledResource = builder.AddContainer("disabled", "image")
            .PublishAsAzureSandbox(new AzureSandboxOptions
            {
                AutoSuspendEnabled = false
            });
        var enabledResource = builder.AddContainer("enabled", "image")
            .PublishAsAzureSandbox(new AzureSandboxOptions
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
        var disabledPolicy = Assert.IsType<SandboxLifecyclePolicy>(
            AzureSandboxContainerDeployment.CreateLifecyclePolicy(disabledSandbox));
        Assert.NotNull(disabledPolicy.AutoSuspendPolicy);
        Assert.False(disabledPolicy.AutoSuspendPolicy.Enabled);
        var enabledPolicy = Assert.IsType<SandboxLifecyclePolicy>(
            AzureSandboxContainerDeployment.CreateLifecyclePolicy(enabledSandbox));
        Assert.NotNull(enabledPolicy.AutoSuspendPolicy);
        Assert.True(enabledPolicy.AutoSuspendPolicy.Enabled);
        Assert.Equal(300, enabledPolicy.AutoSuspendPolicy.IntervalSeconds);
        Assert.Equal("Memory", enabledPolicy.AutoSuspendPolicy.Mode?.ToString());
    }

    [Fact]
    public void SandboxContainerOptionsValidateTypedDurationsAndEnums()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var container = builder.AddContainer("frontend", "image");

        Assert.Throws<ArgumentException>(() => container.PublishAsAzureSandbox(new AzureSandboxOptions
        {
            AutoSuspendInterval = TimeSpan.FromMilliseconds(1500)
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => container.PublishAsAzureSandbox(new AzureSandboxOptions
        {
            AutoSuspendInterval = TimeSpan.FromSeconds((double)int.MaxValue + 1)
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => container.PublishAsAzureSandbox(new AzureSandboxOptions
        {
            AutoDeleteInterval = TimeSpan.FromSeconds(-1)
        }));
        Assert.Throws<ArgumentException>(() => container.PublishAsAzureSandbox(new AzureSandboxOptions
        {
            AutoSuspendMode = (AzureSandboxAutoSuspendMode)(-1)
        }));
        Assert.Throws<ArgumentException>(() => container.PublishAsAzureSandbox(new AzureSandboxOptions
        {
            AutoDeleteTrigger = (AzureSandboxAutoDeleteTrigger)(-1)
        }));
        Assert.Throws<ArgumentException>(() => container.PublishAsAzureSandbox(new AzureSandboxOptions
        {
            Endpoints =
            [
                new AzureSandboxEndpointOptions { Name = "http" },
                new AzureSandboxEndpointOptions { Name = "HTTP" }
            ]
        }));
    }

    [Fact]
    public async Task SandboxContainerRejectsUnprovisionedVolumes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithVolume("cache", "/cache")
            .PublishAsAzureSandbox();

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("volume provisioning is not supported", exception.Message);
        Assert.IsType<NotSupportedException>(exception.InnerException);
    }

    [Fact]
    public async Task SandboxContainerEndpointResolutionMapsHttp2()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .AsHttp2Service()
            .PublishAsAzureSandbox();

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
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var api = builder.AddContainer("api", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox();

        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 3000)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox();

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
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(new AzureSandboxOptions
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
    public async Task SandboxContainerEndpointResolutionRejectsConflictingAnonymousAccessOnSharedPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(name: "public", targetPort: 8080)
            .WithHttpEndpoint(name: "private", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(new AzureSandboxOptions
            {
                Endpoints =
                [
                    new AzureSandboxEndpointOptions
                    {
                        Name = "public",
                        Anonymous = true
                    },
                    new AzureSandboxEndpointOptions
                    {
                        Name = "private",
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

        var exception = Assert.Throws<NotSupportedException>(() => AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal(
            "Endpoint 'private' on resource 'frontend' shares target port 8080 with endpoint 'public' but configures a different anonymous-access policy. Azure sandbox ports support a single access policy per target port.",
            exception.Message);
    }

    [Fact]
    public async Task SandboxContainerEndpointResolutionRejectsTcp()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("cache", "redis", "latest")
            .WithEndpoint(targetPort: 6379, scheme: "tcp", isExternal: true)
            .PublishAsAzureSandbox();

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
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output, outputPath: tempDir.Path);

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
            .PublishAsAzureSandbox();
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
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output, outputPath: tempDir.Path);

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
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output, outputPath: tempDir.Path);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddAzureSandboxGroup("othersandboxes");

        builder.AddProject<TestProject>("frontend", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 5000)
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(sandboxGroup)
            .PublishAsAzureSandbox(new AzureSandboxOptions
            {
                Tier = AzureSandboxTier.Large
            });

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
        var resources = AzureSandboxContainerDeployment.CreateSandboxResources(sandboxContainer);
        Assert.Equal("2000m", resources.Cpu);
        Assert.Equal("4096Mi", resources.Memory);
        Assert.Equal("40960Mi", resources.Disk);
    }

    [Theory]
    [InlineData(AzureSandboxTier.ExtraSmall, "250m", "512Mi", "5120Mi")]
    [InlineData(AzureSandboxTier.Small, "500m", "1024Mi", "10240Mi")]
    [InlineData(AzureSandboxTier.Medium, "1000m", "2048Mi", "20480Mi")]
    [InlineData(AzureSandboxTier.Large, "2000m", "4096Mi", "40960Mi")]
    [InlineData(AzureSandboxTier.ExtraLarge, "4000m", "8192Mi", "81920Mi")]
    public async Task SandboxTierMapsToExpectedResources(AzureSandboxTier tier, string expectedCpu, string expectedMemory, string expectedDisk)
    {
        var resources = await CreateSandboxResourcesForTierAsync(tier);

        Assert.Equal(expectedCpu, resources.Cpu);
        Assert.Equal(expectedMemory, resources.Memory);
        Assert.Equal(expectedDisk, resources.Disk);
    }

    [Fact]
    public async Task AllSandboxTiersRequestDiskWithinServiceTierMaximum()
    {
        // The Azure Dev Compute data plane rejects sandboxes whose disk exceeds cores x 20Gi with
        // an InvalidResourceTier error. Iterate every enum value so newly added tiers are covered.
        foreach (var tier in Enum.GetValues<AzureSandboxTier>())
        {
            var resources = await CreateSandboxResourcesForTierAsync(tier);

            Assert.EndsWith("m", resources.Cpu);
            Assert.EndsWith("Mi", resources.Disk);
            var millicores = int.Parse(resources.Cpu[..^1], CultureInfo.InvariantCulture);
            var diskMebibytes = int.Parse(resources.Disk[..^2], CultureInfo.InvariantCulture);
            var maxDiskMebibytes = millicores * 20 * 1024 / 1000;

            Assert.True(
                diskMebibytes <= maxDiskMebibytes,
                $"Tier '{tier}' requests disk '{resources.Disk}' which exceeds the tier maximum of {maxDiskMebibytes}Mi for CPU '{resources.Cpu}'.");
        }
    }

    private async Task<SandboxResources> CreateSandboxResourcesForTierAsync(AzureSandboxTier tier)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .PublishAsAzureSandbox(new AzureSandboxOptions { Tier = tier });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget?.DeploymentTarget);

        return AzureSandboxContainerDeployment.CreateSandboxResources(sandboxContainer);
    }

    [Fact]
    public async Task PublishAsAzureSandboxRequiresSandboxGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        builder.AddContainer("frontend", "image")
            .PublishAsAzureSandbox();

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default));

        Assert.Equal(
            "Resource 'frontend' is configured to publish as an Azure sandbox, but there are no 'AzureSandboxGroupResource' resources. Ensure you have added one by calling 'AddAzureSandboxGroup'.",
            exception.InnerException?.Message);
    }

    [Fact]
    public async Task PublishAsAzureSandboxRequiresMatchingSandboxGroup()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        builder.AddAzureSandboxGroup("sandboxes");
        var containerApps = builder.AddAzureContainerAppEnvironment("containerapps");
        builder.AddContainer("frontend", "image")
            .WithComputeEnvironment(containerApps)
            .PublishAsAzureSandbox();

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default));

        Assert.Equal(
            "Resource 'frontend' is configured to publish as an Azure sandbox, but it is assigned to compute environment 'containerapps', which is not an active Azure sandbox group. Assign it to an 'AzureSandboxGroupResource' by calling 'WithComputeEnvironment'.",
            exception.InnerException?.Message);
    }

    [Fact]
    public async Task SandboxGroupAutomaticallyDeploysDotNetProjectWithoutExposingEndpoints()
    {
        using var tempDir = new TemporaryDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output, outputPath: tempDir.Path);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddProject<TestProject>("frontend");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var project = Assert.Single(model.GetProjectResources(), resource => resource.Name == "frontend");
        Assert.Same(sandboxGroup.Resource, project.GetComputeEnvironment());
        Assert.Collection(
            project.GetEndpoints().OrderBy(static endpoint => endpoint.EndpointName, StringComparer.Ordinal),
            endpoint => Assert.Equal("http", endpoint.EndpointName),
            endpoint => Assert.Equal("https", endpoint.EndpointName));
        var deploymentTarget = Assert.IsType<AzureSandboxContainerResource>(
            project.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        Assert.Empty(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(deploymentTarget));

        var pipelineContext = new PipelineContext(
            model,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };
        var environment = await AzureSandboxContainerDeployment.ResolveEnvironmentVariablesAsync(stepContext, project);
        Assert.Equal("8080", environment.Values["HTTP_PORTS"]);
        Assert.False(environment.Values.ContainsKey("HTTPS_PORTS"));
    }

    [Fact]
    public async Task SandboxProjectExternalHttpEndpointUsesPlaintextHttpListener()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddProject<TestProject>("frontend", launchProfileName: null)
            .WithHttpsEndpoint(targetPort: 7001)
            .WithHttpEndpoint(targetPort: 5001)
            .WithEndpoint("http", endpoint => endpoint.IsExternal = true);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var project = Assert.Single(
            app.Services.GetRequiredService<DistributedApplicationModel>().GetProjectResources(),
            resource => resource.Name == "frontend");
        var deploymentTarget = Assert.IsType<AzureSandboxContainerResource>(
            project.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        var endpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(deploymentTarget));

        Assert.Equal("http", endpoint.Name);
        Assert.Equal(5001, endpoint.TargetPort);
        Assert.Equal("Http", endpoint.Protocol);
        Assert.True(endpoint.IsExternal);
    }

    [Fact]
    public async Task SandboxProjectRejectsExposedHttpsEndpointWithoutPlaintextHttpEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddProject<TestProject>("frontend", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var project = Assert.Single(
            app.Services.GetRequiredService<DistributedApplicationModel>().GetProjectResources(),
            resource => resource.Name == "frontend");
        var deploymentTarget = Assert.IsType<AzureSandboxContainerResource>(
            project.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        var exception = Assert.Throws<NotSupportedException>(
            () => AzureSandboxContainerDeployment.ResolveSandboxEndpoints(deploymentTarget));

        Assert.Equal(
            "Endpoint 'https' on project resource 'frontend' is exposed through Azure sandbox ingress, which terminates TLS and forwards plaintext HTTP. Add an HTTP endpoint that shares this endpoint's target port.",
            exception.Message);
    }

    [Fact]
    public async Task SandboxDotnetProjectRejectsExposedHttpsEndpointWithoutPlaintextHttpEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var project = builder.AddDotnetProject("frontend", "frontend.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var deploymentTarget = Assert.IsType<AzureSandboxContainerResource>(
            project.Resource.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        var exception = Assert.Throws<NotSupportedException>(
            () => AzureSandboxContainerDeployment.ResolveSandboxEndpoints(deploymentTarget));

        Assert.Contains("terminates TLS and forwards plaintext HTTP", exception.Message);
    }

    [Fact]
    public async Task SandboxProjectSharedHttpHttpsPortUsesConfiguredAccessPolicy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddProject<TestProject>("frontend")
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox(new AzureSandboxOptions
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

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var project = Assert.Single(
            model.GetProjectResources(),
            resource => resource.Name == "frontend");
        var deploymentTarget = Assert.IsType<AzureSandboxContainerResource>(
            project.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        var endpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(deploymentTarget));
        Assert.Equal("http", endpoint.Name);
        Assert.Equal(8080, endpoint.TargetPort);
        Assert.True(endpoint.Anonymous);

        var pipelineContext = new PipelineContext(
            model,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };
        var stateManager = app.Services.GetRequiredService<IDeploymentStateManager>();
        var state = await stateManager.AcquireSectionAsync(
            AzureSandboxContainerDeployment.GetStateSectionName(deploymentTarget),
            TestContext.Current.CancellationToken);
        state.Data["Ports"] = new JsonArray(new JsonObject
        {
            ["Name"] = "http",
            ["Port"] = 8080,
            ["Url"] = "https://frontend.example.test"
        });
        await stateManager.SaveSectionAsync(state, TestContext.Current.CancellationToken);

        Assert.True(AzureSandboxContainerDeployment.TryResolveEndpointReferenceValue(
            project.GetEndpoint("http"),
            sandboxGroup.Resource,
            out var httpUrlExpression));
        Assert.True(AzureSandboxContainerDeployment.TryResolveEndpointReferenceValue(
            project.GetEndpoint("https"),
            sandboxGroup.Resource,
            out var httpsUrlExpression));
        Assert.Equal("https://frontend.example.test", (await AzureSandboxContainerDeployment.ResolveValueWithEgressHostsAsync(stepContext, project, httpUrlExpression)).Value);
        Assert.Equal("https://frontend.example.test", (await AzureSandboxContainerDeployment.ResolveValueWithEgressHostsAsync(stepContext, project, httpsUrlExpression)).Value);
    }

    [Fact]
    public async Task MultipleSandboxGroupsDeployOneDotNetProjectEach()
    {
        using var tempDir = new TemporaryDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output, outputPath: tempDir.Path);

        var frontendGroup = builder.AddAzureSandboxGroup("frontend-group");
        var backendGroup = builder.AddAzureSandboxGroup("backend-group");
        var frontend = builder.AddProject<TestProject>("frontend", launchProfileName: null)
            .WithHttpEndpoint()
            .WithComputeEnvironment(frontendGroup);
        var backend = builder.AddProject<TestProject>("backend", launchProfileName: null)
            .WithHttpEndpoint()
            .WithComputeEnvironment(backendGroup);

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var frontendTarget = Assert.IsType<AzureSandboxContainerResource>(
            frontend.Resource.GetDeploymentTargetAnnotation(frontendGroup.Resource)?.DeploymentTarget);
        var backendTarget = Assert.IsType<AzureSandboxContainerResource>(
            backend.Resource.GetDeploymentTargetAnnotation(backendGroup.Resource)?.DeploymentTarget);
        Assert.Null(frontend.Resource.GetDeploymentTargetAnnotation(backendGroup.Resource));
        Assert.Null(backend.Resource.GetDeploymentTargetAnnotation(frontendGroup.Resource));
        Assert.Empty(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(frontendTarget));
        Assert.Empty(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(backendTarget));
    }

    [Fact]
    public async Task SandboxGroupAddsDeploymentTargetForContainerResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

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
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var container = builder.AddContainer("frontend", "example.azurecr.io/frontend", "latest")
            .PublishAsAzureSandbox();

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
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox();
        var endpointExpression = ReferenceExpression.Create($"{api.GetEndpoint("http")}/v1");
        var web = builder.AddContainer("web", "image")
            .WithEnvironment("API_URL", endpointExpression)
            .PublishAsAzureSandbox();

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
        var value = await AzureSandboxContainerDeployment.ResolveValueWithEgressHostsAsync(stepContext, web.Resource, endpointExpression);
        var environment = await AzureSandboxContainerDeployment.ResolveEnvironmentVariablesAsync(stepContext, web.Resource);

        Assert.Equal("https://api.example.test/v1", value.Value);
        Assert.Equal(["api.example.test"], value.EgressHosts);
        Assert.Equal("https://api.example.test/v1", environment.Values["API_URL"]);
        Assert.Equal(["api.example.test"], environment.EgressHosts);
    }

    [Fact]
    public async Task SandboxConnectionReferencesAreIncludedInEgressPolicy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        builder.AddAzureSandboxGroup("sandboxes");
        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");
        var worker = builder.AddProject<TestProject>("worker", launchProfileName: null)
            .WithReference(blobs)
            .PublishAsAzureSandbox();

        using var app = builder.Build();
        storage.Resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";
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

        var environment = await AzureSandboxContainerDeployment.ResolveEnvironmentVariablesAsync(
            stepContext,
            worker.Resource);
        var egressPolicy = AzureSandboxContainerDeployment.CreateEgressPolicy(environment.EgressHosts);

        Assert.Equal("https://storage.blob.core.windows.net/", environment.Values["ConnectionStrings__blobs"]);
        var hostRule = Assert.Single(egressPolicy.HostRules);
        Assert.Equal("storage.blob.core.windows.net", hostRule.Pattern);
    }

    [Fact]
    public async Task SandboxConnectionReferencesDoNotTreatCredentialsAsEgressHosts()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        builder.AddAzureSandboxGroup("sandboxes");
        var connection = builder.AddConnectionString(
            "external",
            ReferenceExpression.Create($"Endpoint=https://api.example.test;Password=https://credential.example.test"));
        var worker = builder.AddProject<TestProject>("worker", launchProfileName: null)
            .WithReference(connection)
            .PublishAsAzureSandbox();

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

        var environment = await AzureSandboxContainerDeployment.ResolveEnvironmentVariablesAsync(
            stepContext,
            worker.Resource);

        Assert.Equal(
            "Endpoint=https://api.example.test;Password=https://credential.example.test",
            environment.Values["ConnectionStrings__external"]);
        Assert.Equal(["api.example.test"], environment.EgressHosts);
    }

    [Fact]
    public async Task SandboxDeployStepsFollowReferencedEndpointDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox();
        var web = builder.AddContainer("web", "image")
            .WithEnvironment("API_URL", ReferenceExpression.Create($"{api.GetEndpoint("http")}/v1"))
            .PublishAsAzureSandbox();

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
    public async Task SandboxDeployStepsRejectCrossGroupEndpointDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var producerGroup = builder.AddAzureSandboxGroup("producer-group");
        var consumerGroup = builder.AddAzureSandboxGroup("consumer-group");
        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(producerGroup)
            .PublishAsAzureSandbox();
        builder.AddContainer("web", "image")
            .WithEnvironment("API_URL", api.GetEndpoint("http"))
            .WithComputeEnvironment(consumerGroup)
            .PublishAsAzureSandbox();

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        var steps = await CreateStepsAsync(app, consumerGroup.Resource);
        var context = new PipelineConfigurationContext
        {
            Services = app.Services,
            Steps = steps,
            Model = app.Services.GetRequiredService<DistributedApplicationModel>()
        };

        var exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            foreach (var annotation in consumerGroup.Resource.Annotations.OfType<PipelineConfigurationAnnotation>())
            {
                await annotation.Callback(context);
            }
        });

        Assert.Contains("producer-group", exception.Message);
        Assert.Contains("consumer-group", exception.Message);
    }

    [Fact]
    public async Task SandboxDeployStepsRejectCircularEndpointDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var first = builder.AddContainer("first", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox();
        var second = builder.AddContainer("second", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox();
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
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

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
        Assert.True(AzureSandboxContainerDeployment.HasModeledCommandConfiguration(project.Resource));

        var container = builder.AddContainer("container", "image")
            .WithEntrypoint("/bin/sh");
        Assert.True(AzureSandboxContainerDeployment.HasModeledCommandConfiguration(container.Resource));
    }

    [Fact]
    public async Task SandboxCommandEndpointReferencesAreIncludedInEgressPolicy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithExternalHttpEndpoints()
            .PublishAsAzureSandbox();
        var worker = builder.AddContainer("worker", "image")
            .WithArgs(ReferenceExpression.Create(
                $"https://{api.GetEndpoint("http").Property(EndpointProperty.HostAndPort)}/v1"))
            .PublishAsAzureSandbox();

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);
        var apiSandbox = Assert.IsType<AzureSandboxContainerResource>(
            api.Resource.GetDeploymentTargetAnnotation(sandboxGroup.Resource)?.DeploymentTarget);
        var stateManager = app.Services.GetRequiredService<IDeploymentStateManager>();
        var state = await stateManager.AcquireSectionAsync(
            AzureSandboxContainerDeployment.GetStateSectionName(apiSandbox),
            CancellationToken.None);
        state.Data["Ports"] = new JsonArray(new JsonObject
        {
            ["Name"] = "http",
            ["Url"] = "https://api.example.test:8443"
        });
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

        var command = await AzureSandboxContainerDeployment.ResolveModeledCommandAsync(stepContext, worker.Resource);
        var egressPolicy = AzureSandboxContainerDeployment.CreateEgressPolicy(command.EgressHosts);

        var hostRule = Assert.Single(egressPolicy.HostRules);
        Assert.Equal("api.example.test", hostRule.Pattern);
    }

    [Fact]
    public async Task SandboxLiteralEnvironmentValuesDoNotExpandEgressPolicy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        var secret = builder.AddParameter("secret-url", "https://attacker.example", secret: true);
        var worker = builder.AddContainer("worker", "image")
            .WithEnvironment("LITERAL_URL", "https://literal.example")
            .WithEnvironment("SECRET", secret)
            .PublishAsAzureSandbox();

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

        var environment = await AzureSandboxContainerDeployment.ResolveEnvironmentVariablesAsync(
            stepContext,
            worker.Resource);
        var egressPolicy = AzureSandboxContainerDeployment.CreateEgressPolicy(environment.EgressHosts);

        Assert.Equal("https://literal.example", environment.Values["LITERAL_URL"]);
        Assert.Equal("https://attacker.example", environment.Values["SECRET"]);
        Assert.Empty(egressPolicy.HostRules);
    }

    [Fact]
    public async Task SandboxDeploymentLeaseSerializesTheSameResourceAcrossStateMigration()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var firstManager = new TestDeploymentStateManager(Path.Combine(tempDirectory.FullName, "legacy", "state.json"));
            var secondManager = new TestDeploymentStateManager(Path.Combine(tempDirectory.FullName, "canonical", "state.json"));
            var firstLease = await AzureSandboxContainerDeployment.AcquireDeploymentLeaseAsync(
                firstManager,
                "app-host",
                "Development",
                "Azure:Sandboxes:web",
                CancellationToken.None);
            Assert.NotNull(firstLease);
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var secondLeaseTask = AzureSandboxContainerDeployment.AcquireDeploymentLeaseAsync(
                secondManager,
                "app-host",
                "development",
                "Azure:Sandboxes:web",
                cancellationTokenSource.Token);

            await Task.Delay(100, cancellationTokenSource.Token);
            Assert.False(secondLeaseTask.IsCompleted);

            firstLease.Dispose();
            using var secondLease = await secondLeaseTask;
            Assert.NotNull(secondLease);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
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

    private const string PullIdentityResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/pull";

    private static SandboxGroupClient CreateSandboxClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        TokenCredential? credential = null)
    {
        return SandboxGroupClientFactory.Create(
            new SandboxDataPlaneScope("sub", "rg", "sg", "westus3"),
            credential ?? new RecordingTokenCredential(),
            NullLogger.Instance,
            options =>
            {
                options.Transport = new HttpClientTransport(new HttpClient(new RecordingHandler(handler)));
                options.RetryPolicy = new SandboxRetryPolicy(NullLogger.Instance, forbiddenRetryDelay: TimeSpan.Zero, retryDelay: TimeSpan.Zero);
            });
    }

    private static CreateDiskImageContent CreateDiskImageContent() =>
        new(new RegistryDiskImageSource("example.azurecr.io/site@sha256:abc123"));

    private static PipelineStepContext CreateStepContext(
        DistributedApplication app,
        IReportingStep reportingStep,
        CancellationToken cancellationToken = default)
    {
        return new PipelineStepContext
        {
            PipelineContext = new PipelineContext(
                app.Services.GetRequiredService<DistributedApplicationModel>(),
                app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
                app.Services,
                NullLogger.Instance,
                cancellationToken),
            ReportingStep = reportingStep
        };
    }

    private async Task<ResponseLossCleanupService> RunCreateResponseLossAsync(
        bool includeSandbox,
        int emptyPollsBeforeVisible = 0)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, output);
        using var app = builder.Build();
        using var pipelineCts = new CancellationTokenSource();
        pipelineCts.Cancel();
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = CreateStepContext(app, reportingStep, pipelineCts.Token);
        var service = new ResponseLossCleanupService(includeSandbox, emptyPollsBeforeVisible);
        var client = CreateSandboxClient(service.HandleAsync);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            AzureSandboxContainerDeployment.CreateWithResponseLossCleanupAsync(
                service.CreateResourceThenLoseResponseAsync,
                stepContext,
                client,
                "owner",
                "frontend-sandbox-container",
                "deploy",
                responseLossReconciliationTimeout: TimeSpan.FromSeconds(1),
                pollInterval: TimeSpan.FromMilliseconds(1)));

        Assert.Equal("create response lost", exception.Message);
        Assert.Equal(
            "aspire-owner=owner,aspire-resource=frontend-sandbox-container,aspire-deploy=deploy",
            service.LabelFilter);
        return service;
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "testproject";

        public LaunchSettings LaunchSettings { get; } = new()
        {
            Profiles =
            {
                ["http"] = new()
                {
                    CommandName = "Project",
                    ApplicationUrl = "https://localhost:7001;http://localhost:5000"
                }
            }
        };
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

    private static string? GetLabelFilter(HttpRequestMessage request) =>
        System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["labels"];

    private sealed class FailingPruneService
    {
        public string? LabelFilter { get; private set; }

        public bool DeleteSandboxCalled { get; private set; }

        public Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                LabelFilter = GetLabelFilter(request);
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "value": [
                        {
                          "id": "old-sandbox",
                          "labels": { "aspire-owner": "owner", "aspire-resource": "frontend-sandbox-container" },
                          "ports": []
                        }
                      ]
                    }
                    """));
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/diskimages", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{ "value": [] }"""));
            }

            if (request.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                DeleteSandboxCalled = true;
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"));
            }

            throw new NotSupportedException($"{request.Method} {path}");
        }
    }

    private sealed class ResponseLossCleanupService(bool includeSandbox, int emptyPollsBeforeVisible)
    {
        private bool _resourceCreated;
        private bool _sandboxDeleted;
        private bool _diskImageDeleted;
        private int _listAttempts;

        public string? LabelFilter { get; private set; }

        public bool DeleteSandboxCalled { get; private set; }

        public bool DeleteDiskImageCalled { get; private set; }

        public Task<string> CreateResourceThenLoseResponseAsync()
        {
            _resourceCreated = true;
            throw new HttpRequestException("create response lost");
        }

        public Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                LabelFilter = GetLabelFilter(request);
                _listAttempts++;
                return Task.FromResult(JsonResponse(
                    _resourceCreated && _listAttempts > emptyPollsBeforeVisible && includeSandbox && !_sandboxDeleted
                        ? """{ "value": [ { "id": "sandbox", "labels": { "aspire-owner": "owner", "aspire-resource": "frontend-sandbox-container", "aspire-deploy": "deploy" }, "ports": [] } ] }"""
                        : """{ "value": [] }"""));
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/diskimages", StringComparison.Ordinal))
            {
                Assert.Equal(LabelFilter, GetLabelFilter(request));
                return Task.FromResult(JsonResponse(
                    _resourceCreated && _listAttempts > emptyPollsBeforeVisible && !_diskImageDeleted
                        ? """{ "value": [ { "id": "disk-image", "labels": { "aspire-owner": "owner", "aspire-resource": "frontend-sandbox-container", "aspire-deploy": "deploy" }, "status": { "state": "Ready" } } ] }"""
                        : """{ "value": [] }"""));
            }

            if (request.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                DeleteSandboxCalled = true;
                _sandboxDeleted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.Method == HttpMethod.Delete && path.Contains("/diskimages/", StringComparison.Ordinal))
            {
                DeleteDiskImageCalled = true;
                _diskImageDeleted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            throw new NotSupportedException($"{request.Method} {path}");
        }
    }

    private sealed class FailingPortRemovalService(Exception? portRemovalException = null)
    {
        private readonly Exception _portRemovalException = portRemovalException ?? new HttpRequestException("port removal failed");

        public bool DeleteSandboxCalled { get; private set; }

        public bool DeleteDiskImageCalled { get; private set; }

        public Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/ports/remove", StringComparison.Ordinal))
            {
                return Task.FromException<HttpResponseMessage>(_portRemovalException);
            }

            if (request.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                DeleteSandboxCalled = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.Method == HttpMethod.Delete && path.Contains("/diskimages/", StringComparison.Ordinal))
            {
                DeleteDiskImageCalled = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            throw new NotSupportedException($"{request.Method} {path}");
        }
    }

    private sealed class TestDeploymentStateManager(string stateFilePath) : IDeploymentStateManager
    {
        public string StateFilePath { get; } = stateFilePath;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearAllStateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class RecordingTokenCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];
        public int RequestCount { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = [.. requestContext.Scopes];
            RequestCount++;
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = [.. requestContext.Scopes];
            RequestCount++;
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
#pragma warning restore CS0618
