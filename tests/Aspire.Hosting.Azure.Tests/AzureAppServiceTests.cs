// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Foundry;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureAppServiceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task AddContainerAppEnvironmentAddsDeploymentTargetWithContainerAppToProjectResources()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureAppServiceEnvironment("env");

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .PublishAsAzureAppServiceWebsite((infrastructure, site) =>
            {
                site.SiteConfig.IsWebSocketsEnabled = true;
            });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.IsType<IComputeResource>(Assert.Single(model.GetProjectResources()), exactMatch: false);

        var target = container.GetDeploymentTargetAnnotation();

        Assert.NotNull(target);
        Assert.Same(env.Resource, target.ComputeEnvironment);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddContainerAppEnvironmentAddsEnvironmentResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureAppServiceEnvironmentResource>());

        var (manifest, bicep) = await GetManifestWithBicep(environment);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task PublishToAppService_WithDashedConnectionStringName_FailsValidationInPipeline()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "validate-appservice-config-env");

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        builder.AddAzureAppServiceEnvironment("env");

        var cs = builder.AddConnectionString("my-db", ReferenceExpression.Create($"Host=example"));

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithReference(cs)
            .PublishAsAzureAppServiceWebsite(configure: (_, _) => { });

        using var app = builder.Build();

        // Get reference to the reporter before running the app (to avoid disposed ServiceProvider)
        var reporter = app.Services.GetRequiredService<IPipelineActivityReporter>() as TestPipelineActivityReporter;
        Assert.NotNull(reporter);

        // Run the app - validation happens during the pipeline execution
        // The pipeline catches exceptions and reports them via the activity reporter
        await app.RunAsync();

        // Verify the step completed with error
        Assert.Contains(reporter.CompletedSteps, step =>
            step.StepTitle == "validate-appservice-config-env" &&
            step.CompletionState == CompletionState.CompletedWithError);

        // Verify the error message was logged with details about the problematic connection string
        Assert.Contains(reporter.LoggedMessages, log =>
            log.Message.Contains("ConnectionStrings__my-db") &&
            log.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task PublishToAppService_WithDashedConnectionStringName_CanBeIgnored()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "validate-appservice-config-env");

        builder.Services.AddSingleton(testOutputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter, TestPipelineActivityReporter>();

        builder.AddAzureAppServiceEnvironment("env");

        var cs = builder.AddConnectionString("my-db", ReferenceExpression.Create($"Host=example"));

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithReference(cs)
            .PublishAsAzureAppServiceWebsite(configure: (_, _) => { })
            .SkipEnvironmentVariableNameChecks();

        using var app = builder.Build();

        var reporter = app.Services.GetRequiredService<IPipelineActivityReporter>() as TestPipelineActivityReporter;
        Assert.NotNull(reporter);

        await app.RunAsync();

        Assert.Contains(reporter.CompletedSteps, step =>
            step.StepTitle == "validate-appservice-config-env" &&
            step.CompletionState == CompletionState.Completed);
    }

    [Fact]
    public async Task KeyvaultReferenceHandling()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        var db = builder.AddAzureCosmosDB("mydb").WithAccessKeyAuthentication();
        db.AddCosmosDatabase("db");

        var kvName = builder.AddParameter("kvName");
        var sharedRg = builder.AddParameter("sharedRg");

        var existingKv = builder.AddAzureKeyVault("existingKv")
                                .PublishAsExisting(kvName, sharedRg);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(db)
            .WithEnvironment("SECRET_VALUE", existingKv.GetSecret("secret"));

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetProjectResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task EndpointReferencesAreResolvedAcrossProjects()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        // Add 2 projects with endpoints
        var project1 = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        var project2 = builder.AddProject<Project>("project2", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithReference(project1);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        project2.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task EndpointReferenceToFoundryHostedAgentIsResolvedAcrossComputeEnvironments()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var appServiceEnv = builder.AddAzureAppServiceEnvironment("env");

        var project = builder.AddFoundry("foundry")
            .AddProject("project");

        // The agent app is deployed to the Foundry project compute environment via AsHostedAgent.
        var agent = builder.AddProject<Project>("agent", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();
        agent.AsHostedAgent(project);

        // The web app is deployed to App Service and references the Foundry hosted agent. The App
        // Service publisher must delegate endpoint resolution to the Foundry compute environment
        // rather than looking the agent up in its own (App Service) endpoint map. See issue #17749.
        // WithReference(agent) exercises the bare EndpointReference branch; the explicit
        // Property(Url) environment variable exercises the EndpointReferenceExpression branch.
        var web = builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(appServiceEnv)
            .WithReference(agent)
            .WithEnvironment("AGENT_URL", agent.GetEndpoint("http").Property(EndpointProperty.Url));

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        SetFoundryProjectOutputs(project.Resource);

        web.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    private static void SetFoundryProjectOutputs(AzureCognitiveServicesProjectResource project)
    {
        project.Outputs["endpoint"] = "https://account.services.ai.azure.com/api/projects/my-project";
        project.Outputs["APPLICATION_INSIGHTS_CONNECTION_STRING"] = "";
        project.ProvisioningTaskCompletionSource?.TrySetResult();
    }

    [Fact]
    public async Task AzureAppServiceSupportBaitAndSwitchResources()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        builder.AddProject<Project>("api", launchProfileName: null)
            .PublishAsDockerFile()
            .WithHttpEndpoint(env: "PORT", targetPort: 80)
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddDockerfileWithAppServiceInfrastructureAddsDeploymentTargetWithAppServiceToContainerResources()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        var directory = Directory.CreateTempSubdirectory(".aspire-test");

        // Contents of the Dockerfile are not important for this test
        File.WriteAllText(Path.Combine(directory.FullName, "Dockerfile"), "");

        builder.AddDockerfile("api", directory.FullName)
               .WithHttpEndpoint(targetPort: 85, env: "PORT")
               .WithExternalHttpEndpoints();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task UnknownManifestExpressionProviderIsHandledWithAllocateParameter()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        var customProvider = new CustomManifestExpressionProvider();

        var apiProject = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["CUSTOM_VALUE"] = customProvider;
            })
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var project = Assert.Single(model.GetProjectResources());

        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void AzureAppServiceEnvironmentHasNameOutputReference()
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var env = builder.AddAzureAppServiceEnvironment("env");

        // Verify that the NameOutputReference property exists and returns the expected value
        Assert.NotNull(env.Resource.NameOutputReference);
        Assert.Equal("name", env.Resource.NameOutputReference.Name);
        Assert.Same(env.Resource, env.Resource.NameOutputReference.Resource);
    }

    [Fact]
    public async Task AzureAppServiceEnvironmentCanReferenceExistingAppServicePlan()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nameParameter = builder.AddParameter("appServicePlanName", "existing-plan-name");
        var resourceGroupParameter = builder.AddParameter("appServicePlanResourceGroup", "existing-rg");

        builder.AddAzureAppServiceEnvironment("env")
            .PublishAsExisting(nameParameter, resourceGroupParameter);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureAppServiceEnvironmentResource>());

        Assert.True(environment.IsExisting());
        Assert.True(environment.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var annotation));
        Assert.NotNull(annotation);

        var (manifest, bicep) = await GetManifestWithBicep(environment);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AzureAppServiceEnvironmentCanPublishExistingAppServicePlan()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var nameParameter = builder.AddParameter("appServicePlanName", "existing-plan-name");
        var resourceGroupParameter = builder.AddParameter("appServicePlanResourceGroup", "existing-rg");

        builder.AddAzureAppServiceEnvironment("env")
            .PublishAsExisting(nameParameter, resourceGroupParameter);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        await app.RunAsync();

        var mainBicepPath = Path.Combine(tempDir.Path, "main.bicep");
        Assert.True(File.Exists(mainBicepPath), $"Expected publish to produce '{mainBicepPath}'.");
        var mainBicep = await File.ReadAllTextAsync(mainBicepPath);

        var envBicepPath = Path.Combine(tempDir.Path, "env", "env.bicep");
        Assert.True(File.Exists(envBicepPath), $"Expected publish to produce '{envBicepPath}'.");
        var envBicep = await File.ReadAllTextAsync(envBicepPath);

        var apiBicepPath = Path.Combine(tempDir.Path, "api", "api.bicep");
        Assert.True(File.Exists(apiBicepPath), $"Expected publish to produce '{apiBicepPath}'.");
        var apiBicep = await File.ReadAllTextAsync(apiBicepPath);

        await Verify(mainBicep, "bicep")
            .AppendContentAsFile(envBicep, "bicep")
            .AppendContentAsFile(apiBicep, "bicep");
    }

    [Fact]
    public async Task AsExisting_WithExplicitContainerRegistry_PublishGeneratesEnvWithExistingPlanAndRegistry()
    {
        // Existing App Service Plan + existing ACR should reference both pre-provisioned resources.
        // Without WithAcrPullIdentity, Aspire still emits a new identity and AcrPull role assignment
        // for image pulls. Disable the dashboard so the snapshot focuses on plan/ACR behavior.
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var planName = builder.AddParameter("appServicePlanName");
        var registryName = builder.AddParameter("registryName");
        var sharedResourceGroup = builder.AddParameter("sharedRg");

        var acr = builder.AddAzureContainerRegistry("acr")
            .AsExisting(registryName, sharedResourceGroup);

        var env = builder.AddAzureAppServiceEnvironment("env")
            .AsExisting(planName, sharedResourceGroup)
            .WithAzureContainerRegistry(acr)
            .WithDashboard(false);

        var model = new DistributedApplicationModel([env.Resource]);
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, env.Resource);

        var roleAssignment = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(),
            r => r.Name == "env-roles-acr");
        Assert.Same(acr.Resource, roleAssignment.TargetAzureResource);
        Assert.Same(env.Resource, roleAssignment.OwnerResource);
        Assert.Same(sharedResourceGroup.Resource, roleAssignment.Scope?.ResourceGroup);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");
    }

    [Fact]
    public async Task WithAcrPullIdentity_PublishGeneratesEnvWithSuppliedIdentity()
    {
        // Greenfield App Service Plan, but the user has BYO'd a managed identity. The generated
        // Bicep should NOT declare an env_mi resource or an AcrPull role assignment - the
        // identity id/client id should flow into the env module via parameters and be emitted as
        // AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID/CLIENT_ID.
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var mi = builder.AddAzureUserAssignedIdentity("shared-mi");

        var env = builder.AddAzureAppServiceEnvironment("env")
            .WithAcrPullIdentity(mi);

        var (manifest, bicep) = await GetManifestWithBicep(env.Resource);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");
    }

    [Fact]
    public async Task WithAcrPullIdentity_AsExisting_PublishGeneratesEnvWithSuppliedIdentity()
    {
        // Existing App Service Plan + existing ACR + BYO identity that already has AcrPull on the ACR.
        // Disable the dashboard so the env module references the pre-provisioned resources without
        // emitting a new ACR-pull identity, AcrPull role assignment, dashboard, or dashboard contributor identity.
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var planName = builder.AddParameter("appServicePlanName");
        var registryName = builder.AddParameter("registryName");
        var identityName = builder.AddParameter("identityName");
        var sharedResourceGroup = builder.AddParameter("sharedRg");

        var acr = builder.AddAzureContainerRegistry("acr")
            .AsExisting(registryName, sharedResourceGroup);

        var mi = builder.AddAzureUserAssignedIdentity("shared-mi")
            .AsExisting(identityName, sharedResourceGroup);

        var env = builder.AddAzureAppServiceEnvironment("env")
            .AsExisting(planName, sharedResourceGroup)
            .WithAzureContainerRegistry(acr)
            .WithAcrPullIdentity(mi)
            .WithDashboard(false);

        var (manifest, bicep) = await GetManifestWithBicep(env.Resource);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithDashboardControlsDashboardUrlPrintStep(bool enableDashboard)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureAppServiceEnvironment("env")
            .WithDashboard(enableDashboard);

        using var app = builder.Build();

        var steps = await CreateStepsAsync(app, env.Resource);
        var hasPrintDashboardUrlStep = steps.Any(s => s.Name == "print-dashboard-url-env");

        Assert.Equal(enableDashboard, hasPrintDashboardUrlStep);
    }

    [Fact]
    public void AzureAppServiceEnvironmentImplementsIAzureComputeEnvironmentResource()
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var env = builder.AddAzureAppServiceEnvironment("env");

        Assert.IsAssignableFrom<IAzureComputeEnvironmentResource>(env.Resource);
        Assert.IsAssignableFrom<IComputeEnvironmentResource>(env.Resource);
    }

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11818", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task PublishAsAzureAppServiceWebsite_ThrowsIfNoEnvironment()
    {
        static async Task RunTest(Action<IDistributedApplicationBuilder> action)
        {
            var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
            // Do not add AddAzureAppServiceEnvironment

            action(builder);

            using var app = builder.Build();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

            Assert.Contains("there are no 'AzureAppServiceEnvironmentResource' resources", ex.Message);
        }

        await RunTest(builder =>
            builder.AddProject<Projects.ServiceA>("ServiceA")
                .PublishAsAzureAppServiceWebsite((_, _) => { }));

        await RunTest(builder =>
            builder.AddContainer("api", "myimage")
                .PublishAsAzureAppServiceWebsite((_, _) => { }));

        await RunTest(builder =>
            builder.AddExecutable("exe", "path/to/executable", ".")
                .PublishAsDockerFile()
                .PublishAsAzureAppServiceWebsite((_, _) => { }));
    }

    [Fact]
    public async Task ValidateAzureAppService_DoesNotThrowInRunMode()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/16940.
        // In run mode, AddAzureAppServiceEnvironment does not add the env resource to the model.
        // If a compute resource still ends up with an AzureAppServiceWebsiteCustomizationAnnotation
        // (e.g. via WithAnnotation), the validation step should not throw at 'aspire run' time —
        // PublishAs* customizations are only meaningful at publish/deploy time.
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.AddAzureAppServiceEnvironment("env");

        builder.AddContainer("api", "myimage")
            .WithAnnotation(new AzureAppServiceWebsiteCustomizationAnnotation((_, _) => { }));

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);
    }

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11818", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task MultipleAzureAppServiceEnvironmentsSupported()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "publish-manifest");

        var env1 = builder.AddAzureAppServiceEnvironment("env1");
        var env2 = builder.AddAzureAppServiceEnvironment("env2");

        builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(env1);

        builder.AddProject<Projects.ServiceB>("ServiceB")
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(env2);

        using var app = builder.Build();

        // Publishing will stop the app when it is done
        await app.RunAsync();

        var verifySettings = new VerifySettings();
        verifySettings.ScrubLines(line => line.Contains("\"path\"") && line.Contains(".csproj"));
        await VerifyFile(
            Path.Combine(tempDir.Path, "aspire-manifest.json"),
            verifySettings);
    }

    [Fact]
    public async Task ResourceWithProbes()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var env1 = builder.AddAzureAppServiceEnvironment("env");

#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        builder
            .AddProject<Project>("project1", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithHttpProbe(ProbeType.Readiness, "/ready", initialDelaySeconds: 60) // This will be ignored
            .WithHttpProbe(ProbeType.Liveness, "/health");
#pragma warning restore ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var project = Assert.Single(model.GetProjectResources());
        var projectProvisioningResource = project.GetDeploymentTargetAnnotation()?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(projectProvisioningResource);

        var (_, projectBicep) = await GetManifestWithBicep(projectProvisioningResource);

        await Verify(projectBicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceEnvironmentWithoutDashboardAddsEnvironmentResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env").WithDashboard(false);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureAppServiceEnvironmentResource>());

        var (manifest, bicep) = await GetManifestWithBicep(environment);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceToEnvironmentWithoutDashboard()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env").WithDashboard(false);

        // Add 2 projects with endpoints
        var project1 = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        var project2 = builder.AddProject<Project>("project2", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithReference(project1);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        project2.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceWithArgs()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        // Add 2 projects with endpoints
        var project1 = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        var project2 = builder.AddProject<Project>("project2", launchProfileName: null)
            .WithHttpEndpoint()
            .WithArgs("--myarg", "myvalue")
            .WithExternalHttpEndpoints()
            .WithReference(project1);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        project2.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceWithTargetPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        // Add 2 projects with endpoints
        var project1 = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithHttpsEndpoint(targetPort:8000)
            .WithHttpEndpoint(targetPort: 8000)
            .WithExternalHttpEndpoints();

        var project2 = builder.AddProject<Project>("project2", launchProfileName: null)
            .WithHttpEndpoint(targetPort:9000)
            .WithExternalHttpEndpoints()
            .WithReference(project1);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        project2.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
                .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task CanPreserveHttpSchemeUsingWithHttpsUpgrade()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env")
               .WithHttpsUpgrade(false);

        var project1 = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithHttpsEndpoint(targetPort: 8000)
            .WithHttpEndpoint(targetPort: 8000)
            .WithExternalHttpEndpoints();

        // Add a second project that references project1 so we can verify
        // the resolved endpoint URLs use http:// (not upgraded to https://)
        var project2 = builder.AddProject<Project>("project2", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 9000)
            .WithExternalHttpEndpoints()
            .WithReference(project1);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        project2.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
                .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceWithTargetPortMultipleEndpoints()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        // Add 2 projects with endpoints
        var project1 = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithExternalHttpEndpoints();

        var project2 = builder.AddProject<Project>("project2", launchProfileName: null)
            .WithHttpsEndpoint(targetPort: 8000)
            .WithHttpEndpoint(targetPort: 8000)
            .WithExternalHttpEndpoints()
            .WithReference(project1);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        project2.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
                .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceWithMultipleTargetPortsThrowsNotSupportedException()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        // Add 2 projects with endpoints
        var project1 = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithExternalHttpEndpoints();

        var project2 = builder.AddProject<Project>("project2", launchProfileName: null)
            .WithHttpsEndpoint(targetPort: 8000)
            .WithHttpEndpoint(targetPort: 8800)
            .WithExternalHttpEndpoints()
            .WithReference(project1);

        using var app = builder.Build();

        var outer = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));
        var ex = Assert.IsType<NotSupportedException>(outer.InnerException);

        Assert.Equal("App Service does not support resources with multiple external endpoints.", ex.Message);
    }

    [Fact]
    public async Task AddAppServiceWithMixedNullAndExplicitTargetPortsThrowsNotSupportedException()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        // Add project with one endpoint with null target port and one with explicit target port
        var project = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint() // null target port (default)
            .WithHttpsEndpoint(targetPort: 8443) // explicit target port
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        var outer = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));
        var ex = Assert.IsType<NotSupportedException>(outer.InnerException);

        Assert.Equal("App Service does not support resources with multiple external endpoints.", ex.Message);
    }

    [Fact]
    public async Task AddAppServiceProjectWithoutTargetPortUsesContainerPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        // Add project with endpoints but no target port specified
        var project = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        project.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        // For project resources without explicit target port, should use container port reference
        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceContainerWithoutTargetPortUsesDefaultPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        // Add container with endpoints but no target port specified
        var container = builder.AddDockerfile("container1", "./myimage")
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        container.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        // For non-project resources without explicit target port, should default to 8000
        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task GetHostAddressExpression()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureAppServiceEnvironment("env");

        var project = builder
            .AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint();

        var endpointReferenceEx = env.Resource.GetHostAddressExpression(project.GetEndpoint("http"));
        Assert.NotNull(endpointReferenceEx);

        Assert.Equal("project1-{0}.azurewebsites.net", endpointReferenceEx.Format);
        var provider = Assert.Single(endpointReferenceEx.ValueProviders);
        var output = Assert.IsType<BicepOutputReference>(provider);
        Assert.Equal(env.Resource, output.Resource);
        Assert.Equal("webSiteSuffix", output.Name);
    }

    [Theory]
    [InlineData(EndpointProperty.Url, "https://project1-website123.azurewebsites.net")]
    [InlineData(EndpointProperty.Host, "project1-website123.azurewebsites.net")]
    [InlineData(EndpointProperty.IPV4Host, "project1-website123.azurewebsites.net")]
    [InlineData(EndpointProperty.Port, "443")]
    [InlineData(EndpointProperty.TargetPort, "5000")]
    [InlineData(EndpointProperty.Scheme, "https")]
    [InlineData(EndpointProperty.HostAndPort, "project1-website123.azurewebsites.net")]
    [InlineData(EndpointProperty.TlsEnabled, "True")]
    public async Task GetEndpointPropertyExpression_ReturnsAppServiceEndpointPropertyExpression(EndpointProperty property, string expected)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureAppServiceEnvironment("env");
        env.Resource.Outputs["webSiteSuffix"] = "website123";
        env.Resource.ProvisioningTaskCompletionSource?.TrySetResult();

        var project = builder
            .AddProject<Project>("project1", launchProfileName: null)
            .WithEndpoint(port: 8080, targetPort: 5000, scheme: "http", name: "http", isExternal: true);

#pragma warning disable ASPIRECOMPUTE002
        var expression = env.Resource.GetEndpointPropertyExpression(project.GetEndpoint("http").Property(property));
#pragma warning restore ASPIRECOMPUTE002

        Assert.Equal(expected, await expression.GetValueAsync(default));
    }

    [Fact]
    public async Task AddAppServiceWithApplicationInsightsLocation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env").WithAzureApplicationInsights("westus");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureAppServiceEnvironmentResource>());

        var (manifest, bicep) = await GetManifestWithBicep(environment);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceWithApplicationInsightsDefaultLocation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env").WithAzureApplicationInsights();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureAppServiceEnvironmentResource>());

        var (manifest, bicep) = await GetManifestWithBicep(environment);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceWithApplicationInsightsNormalizesBicepIdentifiers()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env-1").WithAzureApplicationInsights();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureAppServiceEnvironmentResource>());

        var (_, bicep) = await GetManifestWithBicep(environment);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceWithApplicationInsightsLocationParam()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var appInsightsParam = builder.AddParameter("appInsightsLocation", "westus");
        builder.AddAzureAppServiceEnvironment("env").WithAzureApplicationInsights(appInsightsParam);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureAppServiceEnvironmentResource>());

        var (manifest, bicep) = await GetManifestWithBicep(environment);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceWithExistingApplicationInsights()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var appInsights = builder.AddAzureApplicationInsights("existingAppInsights");
        builder.AddAzureAppServiceEnvironment("env").WithAzureApplicationInsights(appInsights);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureAppServiceEnvironmentResource>());

        var (manifest, bicep) = await GetManifestWithBicep(environment);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceProjectWithApplicationInsightsSetsAppSettings()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env").WithAzureApplicationInsights();

        // Add project with endpoints but no target port specified
        var project = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        project.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        // For project resources without explicit target port, should use container port reference
        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceWithDeploymentSlot()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env").WithDeploymentSlot("stage");

        // Add project with endpoints but no target port specified
        var project = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        project.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        // For project resources without explicit target port, should use container port reference
        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddAppServiceWithDeploymentSlotParameter()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var slotParam = builder.AddParameter("deploymentSlot", "stage");
        builder.AddAzureAppServiceEnvironment("env").WithDeploymentSlot(slotParam);

        // Add project with endpoints but no target port specified
        var project = builder.AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        project.Resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        // For project resources without explicit target port, should use container port reference
        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ConditionalExpressionWithParameterCondition()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        var featureFlag = builder.AddParameter("enable-feature");

        var project = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        project.WithEnvironment(context =>
        {
            var conditional = ReferenceExpression.CreateConditional(
                featureFlag.Resource,
                bool.TrueString,
                ReferenceExpression.Create($"enabled"),
                ReferenceExpression.Create($"disabled"));

            context.EnvironmentVariables["FEATURE_MODE"] = conditional;
        });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var proj = Assert.Single(model.GetProjectResources());

        proj.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    private static Task<(JsonNode ManifestNode, string BicepText)> GetManifestWithBicep(IResource resource) =>
        AzureManifestUtils.GetManifestWithBicep(resource);

    private static async Task<List<PipelineStep>> CreateStepsAsync(DistributedApplication app, AzureAppServiceEnvironmentResource resource)
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

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "/foo/bar/project.csproj";
    }

    private sealed class CustomManifestExpressionProvider : IManifestExpressionProvider
    {
        public string ValueExpression => "{customValue}";
    }
}
