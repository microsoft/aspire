// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Azure environment resources are experimental.
#pragma warning disable ASPIREAZURE003 // Tenant-scoped resource annotations are experimental.
#pragma warning disable ASPIREPIPELINES001 // Verifies provisioning annotations on existing resources.
#pragma warning disable ASPIREPIPELINES003 // Exercises the deployment pipeline for existing registrations.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.EntraId.Provisioning;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Tests.Utils;
using Azure.Provisioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdProvisioningTests(ITestOutputHelper outputHelper)
{
    private const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    private const string ClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";

    [Fact]
    public async Task WithOwners_GeneratesOwnerRelationships()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var spOwner = builder.AddParameter("sp-owner");
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithApplicationOwners("00001111-aaaa-2222-bbbb-3333cccc4444", "00001111-AAAA-2222-bbbb-3333cccc4444")
            .WithServicePrincipalOwners("11112222-aaaa-3333-bbbb-4444cccc5555")
            .WithServicePrincipalOwners(spOwner);

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void WithOwners_ValidatesIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");

        Assert.Throws<ArgumentException>(() => entra.WithApplicationOwners("not-a-guid"));
        Assert.Throws<ArgumentException>(() => entra.WithServicePrincipalOwners(" "));
        Assert.Empty(entra.Resource.ApplicationOwners);
    }

    [Fact]
    public async Task WithOwners_SetsApplicationAndServicePrincipalOwners()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var param = builder.AddParameter("owner");
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithOwners("00001111-aaaa-2222-bbbb-3333cccc4444")
            .WithOwners(param);

        Assert.Equal(2, entra.Resource.ApplicationOwners.Count);
        Assert.Equal(entra.Resource.ApplicationOwners, entra.Resource.ServicePrincipalOwners);
        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void DefaultProvisioning_HasNoOwners()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");

        Assert.DoesNotContain("owners", entra.Resource.GetBicepTemplateString());
    }

    [Fact]
    public async Task AddEntraIdApplication_GeneratesGraphBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");

        Assert.IsAssignableFrom<AzureProvisioningResource>(entra.Resource);
        var tenantScoped = Assert.Single(entra.Resource.Annotations.OfType<TenantScopedResourceAnnotation>());
        Assert.Equal(new Dictionary<string, string>
        {
            ["Microsoft.Graph/applications"] = "objectId",
            ["Microsoft.Graph/servicePrincipals"] = "servicePrincipalId"
        }, tenantScoped.ResourceIdOutputs);
        Assert.NotNull(entra.Resource.ProvisioningTaskCompletionSource);
        Assert.False(entra.Resource.ProvisioningTaskCompletionSource.Task.IsCompleted);
        Assert.Equal(new[] { "tenantResourceSuffix" }, entra.Resource.Parameters.Keys);
        Assert.Equal(EntraIdSignInAudience.AzureADMyOrg, entra.Resource.SignInAudience);
        Assert.Null(entra.Resource.Parameters[AzureBicepResource.KnownParameters.TenantResourceSuffix]);
        Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
        Assert.Equal(new[] { "tenantResourceSuffix" }, entra.Resource.Parameters.Keys);
    }

    [Fact]
    public async Task ConfigureInfrastructure_CustomizesGraphResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web")
            .ConfigureInfrastructure(infrastructure =>
            {
                var application = Assert.Single(infrastructure.GetProvisionableResources().OfType<MicrosoftGraphApplication>());
                var servicePrincipal = Assert.Single(infrastructure.GetProvisionableResources().OfType<MicrosoftGraphServicePrincipal>());
                application.DisplayName = "Contoso's web application";
                application.RedirectUris = ["https://localhost:7123/signin-oidc", "https://localhost:7123/auth/callback"];
                application.EnableIdTokenIssuance = true;
                Assert.Equal("application.appId", servicePrincipal.AppId.Compile().ToString());
                infrastructure.Add(new ProvisioningOutput("applicationDisplayName", typeof(string))
                {
                    Value = application.DisplayName.ToBicepExpression()
                });
            });

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void GetBicepTemplateFile_IsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris("https://localhost:7123/signin-oidc");
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        using var first = entra.Resource.GetBicepTemplateFile(workspace.Path);
        var firstContents = File.ReadAllText(first.Path);
        using var second = entra.Resource.GetBicepTemplateFile(workspace.Path);
        var secondContents = File.ReadAllText(second.Path);

        Assert.Equal(firstContents, secondContents);
        Assert.Equal(firstContents, entra.Resource.GetBicepTemplateString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetBicepTemplateString_RejectsNonResourceGroupScope(bool tenantScoped)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        entra.Resource.Scope = tenantScoped
            ? AzureBicepResourceScope.CreateForTenant()
            : AzureBicepResourceScope.CreateForSubscription("11111111-2222-3333-4444-555555555555");

        var exception = Assert.Throws<InvalidOperationException>(entra.Resource.GetBicepTemplateString);
        Assert.Equal("Entra ID application templates must be deployed at resource-group scope.", exception.Message);
    }

    [Fact]
    public async Task AddEntraIdApplication_PublishesManifestWithParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs)
            .WithRedirectUris("https://localhost:7123/signin-oidc")
            .WithIdTokenIssuance();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var manifest = await ManifestUtils.GetManifest(entra.Resource, workspace.Path);
        Assert.Equal(new[] { "tenantResourceSuffix" }, entra.Resource.Parameters.Keys);
        await Verify(manifest.ToJsonString(), "json")
            .AppendContentAsFile(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task Publish_SignInOnlyRegistration_RequiresExplicitIdTokenIssuance()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, workspace.Path);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(
                "https://localhost:7123/signin-oidc",
                "https://localhost:7123/signout-callback-oidc")
            .WithIdTokenIssuance();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environment = model.Resources.OfType<AzureEnvironmentResource>().Single();
        var options = app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>().Value;
        await using var step = await new TestPipelineActivityReporter(outputHelper).CreateStepAsync("Publishing sign-in-only registration");
        var context = new AzurePublishingContext(
            workspace.Path, options, app.Services, NullLogger<AzurePublishingContext>.Instance, step);

        await context.WriteModelAsync(model, environment);

        await Verify(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "entra-web", "entra-web.bicep")), "bicep");
    }

    [Fact]
    public async Task Publish_TenantResourceSuffix_IsADeploymentParameterWithExplicitOverrides()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, workspace.Path);
        builder.Configuration["Azure:TenantResourceSuffix"] = "local-developer";
        builder.AddEntraIdApplication("entra-web");
        builder.AddEntraIdApplication("entra-api")
            .WithParameter(AzureBicepResource.KnownParameters.TenantResourceSuffix, "production");
        builder.AddExistingEntraApplication("existing", tenantId: TenantId, clientId: ClientId);
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environment = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        await using var step = await new TestPipelineActivityReporter(outputHelper).CreateStepAsync("Publishing tenant suffix");
        var context = new AzurePublishingContext(
            workspace.Path,
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>().Value,
            app.Services,
            NullLogger<AzurePublishingContext>.Instance,
            step);

        await context.WriteModelAsync(model, environment);

        await Verify(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "entra-web", "entra-web.bicep")), "bicep");
    }

    [Fact]
    public async Task Publish_TenantResourceSuffix_ReusesExplicitNamedDeploymentParameter()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, workspace.Path);
        var suffix = builder.AddParameter("tenantResourceSuffix", "shared");
        builder.AddEntraIdApplication("entra-web");
        builder.AddEntraIdApplication("entra-api")
            .WithParameter(AzureBicepResource.KnownParameters.TenantResourceSuffix, suffix);
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await using var step = await new TestPipelineActivityReporter(outputHelper).CreateStepAsync("Publishing shared tenant suffix");
        var context = new AzurePublishingContext(
            workspace.Path,
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>().Value,
            app.Services,
            NullLogger<AzurePublishingContext>.Instance,
            step);

        await context.WriteModelAsync(model, Assert.Single(model.Resources.OfType<AzureEnvironmentResource>()));

        Assert.Same(context.ParameterLookup[suffix.Resource],
            Assert.Single(context.MainInfrastructure.GetProvisionableResources().OfType<ProvisioningParameter>(),
                parameter => parameter.BicepIdentifier == AzureBicepResource.KnownParameters.TenantResourceSuffix));
        await Verify(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep")), "bicep");
    }

    [Fact]
    public async Task Publish_CreatesSelfContainedGraphModulesAndOutputReferences()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, workspace.Path);
        var api = builder.AddEntraIdApplication("entra-api");
        var web = builder.AddEntraIdApplication("entra-web")
            .WithSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs)
            .WithRedirectUris("https://localhost:7123/signin-oidc");
        builder.AddExistingEntraApplication("existing", tenantId: TenantId, clientId: ClientId);
        builder.AddResource(new ProjectResource("web")).WithReference(web);
        builder.AddResource(new ProjectResource("api")).WithReference(api);
        builder.AddBicepTemplateString("consumer",
            """
            param location string = resourceGroup().location
            param tenantId string
            param clientId string

            output authority string = 'https://login.microsoftonline.com/${tenantId}/v2.0'
            output applicationClientId string = clientId
            """)
            .WithParameter("tenantId", web.Resource.TenantIdOutputReference)
            .WithParameter("clientId", web.Resource.ClientIdOutputReference);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environment = model.Resources.OfType<AzureEnvironmentResource>().Single();
        var options = app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>().Value;
        await using var step = await new TestPipelineActivityReporter(outputHelper).CreateStepAsync("Publishing Entra registrations");
        var context = new AzurePublishingContext(
            workspace.Path, options, app.Services, NullLogger<AzurePublishingContext>.Instance, step);

        await context.WriteModelAsync(model, environment);

        var mainBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep"));
        var apiBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "entra-api", "entra-api.bicep"));
        var webBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "entra-web", "entra-web.bicep"));
        var consumerBicep = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "consumer", "consumer.bicep"));
        Assert.Equal(new[] { "consumer", "entra-api", "entra-web" }, Directory.GetDirectories(workspace.Path).Select(Path.GetFileName).Order());
        await Verify(mainBicep, "bicep")
            .AppendContentAsFile(apiBicep, "bicep")
            .AppendContentAsFile(webBicep, "bicep")
            .AppendContentAsFile(consumerBicep, "bicep");
    }

    [Theory]
    [InlineData(EntraIdSignInAudience.AzureADMyOrg)]
    [InlineData(EntraIdSignInAudience.AzureADMultipleOrgs)]
    [InlineData(EntraIdSignInAudience.AzureADandPersonalMicrosoftAccount)]
    [InlineData(EntraIdSignInAudience.PersonalMicrosoftAccount)]
    public async Task WithSignInAudience_WritesResourceValueToInfrastructure(EntraIdSignInAudience audience)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        entra.WithSignInAudience(audience);

        Assert.Equal(audience, entra.Resource.SignInAudience);
        Assert.Equal(new[] { "tenantResourceSuffix" }, entra.Resource.Parameters.Keys);
        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithIdTokenIssuance_WritesResourceValueToInfrastructure(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var entra = builder.AddEntraIdApplication("entra-web");

        Assert.Same(entra, entra.WithIdTokenIssuance());
        Assert.Same(entra, entra.WithIdTokenIssuance());
        Assert.True(entra.Resource.EnableIdTokenIssuance);
        Assert.Equal(new[] { "tenantResourceSuffix" }, entra.Resource.Parameters.Keys);
        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void WithRedirectUris_ValidatesAndAccumulatesResourceValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");

        Assert.Same(entra, entra.WithRedirectUris("https://localhost:7123/signin-oidc"));
        entra.WithRedirectUris("https://localhost:7123/signout-callback-oidc");
        Assert.Throws<ArgumentNullException>(() => EntraIdResourceExtensions.WithRedirectUris(null!, "https://localhost:7123/signin-oidc"));
        Assert.Throws<ArgumentNullException>(() => entra.WithRedirectUris(null!));
        Assert.Throws<ArgumentException>(() => entra.WithRedirectUris("https://localhost:7252/callback", ""));
        Assert.Equal(new[] { "https://localhost:7123/signin-oidc", "https://localhost:7123/signout-callback-oidc" }, entra.Resource.RedirectUris);
    }

    [Fact]
    public async Task ConfigureInfrastructure_WritesDirectResourcePropertyValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        entra.Resource.SignInAudience = EntraIdSignInAudience.AzureADMultipleOrgs;
        entra.Resource.RedirectUris.Add("https://localhost:7123/signin-oidc");
        entra.Resource.EnableIdTokenIssuance = true;

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void WithIdTokenIssuance_ThrowsWhenBuilderIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => EntraIdResourceExtensions.WithIdTokenIssuance(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithIdTokenIssuance_DoesNotProvisionExistingApplication(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var entra = builder.AddExistingEntraApplication("entra-web", tenantId: TenantId, clientId: ClientId)
            .WithIdTokenIssuance();

        Assert.True(entra.Resource.IsExistingApplication);
        Assert.Equal(TenantId, entra.Resource.TenantId);
        Assert.Equal(ClientId, entra.Resource.ClientId);
        Assert.Null(entra.Resource.ProvisioningTaskCompletionSource);
        Assert.Empty(entra.Resource.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Empty(entra.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        Assert.Null(await ManifestUtils.GetManifestOrNull(entra.Resource));
    }

    [Fact]
    public async Task AddExistingEntraApplication_DoesNotPublishOrAwaitProvisioning()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddExistingEntraApplication("entra-web", tenantId: TenantId, clientId: ClientId);

        Assert.True(entra.Resource.IsExistingApplication);
        Assert.Null(entra.Resource.ProvisioningTaskCompletionSource);
        Assert.Empty(entra.Resource.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Empty(entra.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        Assert.Null(await ManifestUtils.GetManifestOrNull(entra.Resource));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsExistingApplication_PreservesAzureProvisioningSetup(bool useParameters)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var customization = new PipelineConfigurationAnnotation(_ => { });
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithAnnotation(customization);
        var existing = useParameters
            ? entra.AsExistingApplication(builder.AddParameter("tenant-id", TenantId), builder.AddParameter("client-id", ClientId))
            : entra.AsExistingApplication(TenantId, ClientId);

        Assert.Same(entra, existing);
        Assert.True(existing.Resource.IsExistingApplication);
        Assert.Null(existing.Resource.ProvisioningTaskCompletionSource);
        Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        Assert.Empty(existing.Resource.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Same(customization, Assert.Single(existing.Resource.Annotations.OfType<PipelineConfigurationAnnotation>()));
        Assert.Null(await ManifestUtils.GetManifestOrNull(existing.Resource));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddExistingEntraApplication_DeployDoesNotRequireAzure(bool useParameters)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputHelper, workspace.Path, step: WellKnownPipelineSteps.Deploy);
        var entra = useParameters
            ? builder.AddExistingEntraApplication("entra-web",
                builder.AddParameter("tenant-id", TenantId), builder.AddParameter("client-id", ClientId))
            : builder.AddExistingEntraApplication("entra-web", TenantId, ClientId);

        Assert.True(entra.Resource.IsExistingApplication);
        Assert.Empty(builder.Resources.OfType<AzureEnvironmentResource>());
        Assert.All(builder.Services, service => Assert.NotEqual(typeof(ITokenCredentialProvider), service.ServiceType));
        // Fail instead of acquiring real credentials if Azure authentication is accidentally activated.
        builder.Services.AddSingleton<ITokenCredentialProvider>(_ => throw new InvalidOperationException("Azure authentication must not be used."));
        var reporter = new TestPipelineActivityReporter(outputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);
        var deploymentExecuted = false;
        builder.Pipeline.AddStep("verify-existing-registration", context =>
        {
            Assert.Empty(context.Model.Resources.OfType<AzureEnvironmentResource>());
            Assert.All(context.PipelineContext.SelectedSteps, step =>
                Assert.False(step.Name is "validate-azure-login" or "create-provisioning-context" or "provision-azure-bicep-resources"));
            deploymentExecuted = true;
            return Task.CompletedTask;
        }, requiredBy: WellKnownPipelineSteps.Deploy);

        using var app = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await app.RunAsync(cts.Token);

        Assert.True(deploymentExecuted);
        Assert.True(reporter.CompletePublishCalled);
        Assert.NotEmpty(reporter.CompletedSteps);
        Assert.All(reporter.CompletedSteps, step => Assert.Equal(CompletionState.Completed, step.CompletionState));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddExistingEntraApplication_PreservesAzureProvisioningForOtherResources(bool addAzureFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var name = builder.AddParameter("arm-name", "existing-resource");
        var group = builder.AddParameter("arm-group", "existing-resource-group");
        if (addAzureFirst)
        {
            builder.AddAzureInfrastructure("other", _ => { }).AsExisting(name, group);
        }

        var existing = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId);
        if (!addAzureFirst)
        {
            builder.AddAzureInfrastructure("other", _ => { }).AsExisting(name, group);
        }

        Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        Assert.Empty(existing.Resource.Annotations.OfType<PipelineStepAnnotation>());
        var azure = Assert.Single(builder.Resources.OfType<AzureProvisioningResource>(), resource => resource.Name == "other");
        Assert.Single(azure.Annotations.OfType<PipelineStepAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddExistingEntraApplication_PreservesProvisionedEntraRegistrations(bool addCreatedFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        if (addCreatedFirst)
        {
            builder.AddEntraIdApplication("created");
        }

        builder.AddExistingEntraApplication("existing", TenantId, ClientId);
        if (!addCreatedFirst)
        {
            builder.AddEntraIdApplication("created");
        }

        Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        var created = Assert.Single(builder.Resources.OfType<EntraIdApplicationResource>(), resource => resource.Name == "created");
        Assert.Single(created.Annotations.OfType<PipelineStepAnnotation>());
        Assert.NotNull(created.ProvisioningTaskCompletionSource);
    }

    [Fact]
    public void AddExistingEntraApplication_PreservesAppHostPipelineAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var customization = new PipelineConfigurationAnnotation(_ =>
        {
        });
        var entra = builder.AddExistingEntraApplication("entra-web", tenantId: TenantId, clientId: ClientId)
            .WithAnnotation(customization);

        Assert.Same(customization, Assert.Single(entra.Resource.Annotations.OfType<PipelineConfigurationAnnotation>()));
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public void AsExisting_RejectsArmResourceIdentity(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var entra = builder.AddEntraIdApplication("entra-web")
            .AsExisting(builder.AddParameter("application-name"), builder.AddParameter("resource-group"));

        var exception = Assert.Throws<InvalidOperationException>(entra.Resource.GetBicepTemplateString);
        Assert.Equal(
            "Entra ID applications are identified by tenant ID and client ID, not by an Azure resource name or resource group. " +
            "Use AddExistingEntraApplication(name, tenantId, clientId) to reference an existing registration without provisioning it.",
            exception.Message);
    }

    [Fact]
    public void GetBicepTemplate_RejectsInvalidSignInAudience()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        entra.Resource.SignInAudience = (EntraIdSignInAudience)42;

        var exception = Assert.Throws<InvalidOperationException>(entra.Resource.GetBicepTemplateString);
        Assert.Equal("'42' is not a valid EntraIdSignInAudience value.", exception.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("ABC123", "CN=Contoso")]
    public void GetBicepTemplate_RejectsInvalidCredentialBeforeProvisioning(string? thumbprint, string? distinguishedName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithCredential(new EntraIdStoreCertificateCredential
            {
                StorePath = "CurrentUser/My",
                Thumbprint = thumbprint,
                DistinguishedName = distinguishedName
            });

        var exception = Assert.Throws<InvalidOperationException>(entra.Resource.GetBicepTemplateString);

        Assert.Equal(thumbprint is null
            ? "Either Thumbprint or DistinguishedName must be set on EntraIdStoreCertificateCredential."
            : "Only one of Thumbprint or DistinguishedName can be set on EntraIdStoreCertificateCredential.",
            exception.Message);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, EntraIdSignInAudience.AzureADMyOrg, null)]
    [InlineData(DistributedApplicationOperation.Publish, EntraIdSignInAudience.AzureADMyOrg, null)]
    [InlineData(DistributedApplicationOperation.Run, EntraIdSignInAudience.AzureADMultipleOrgs, "organizations")]
    [InlineData(DistributedApplicationOperation.Publish, EntraIdSignInAudience.AzureADMultipleOrgs, "organizations")]
    [InlineData(DistributedApplicationOperation.Run, EntraIdSignInAudience.AzureADandPersonalMicrosoftAccount, "common")]
    [InlineData(DistributedApplicationOperation.Publish, EntraIdSignInAudience.AzureADandPersonalMicrosoftAccount, "common")]
    [InlineData(DistributedApplicationOperation.Run, EntraIdSignInAudience.PersonalMicrosoftAccount, "consumers")]
    [InlineData(DistributedApplicationOperation.Publish, EntraIdSignInAudience.PersonalMicrosoftAccount, "consumers")]
    public async Task WithReference_CreatedApplication_UsesOutputs(
        DistributedApplicationOperation operation,
        EntraIdSignInAudience audience,
        string? signInTenant)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web").WithSignInAudience(audience);
        entra.Resource.Outputs["tenantId"] = TenantId;
        entra.Resource.Outputs["clientId"] = ClientId;
        entra.Resource.ProvisioningTaskCompletionSource!.SetResult();

        var project = builder.AddResource(new ProjectResource("web")).WithReference(entra);
        var container = builder.AddContainer("worker", "myimage").WithReference(entra, connectionName: "ENTRA");

        var tenantId = operation == DistributedApplicationOperation.Run ? TenantId : "{entra-web.outputs.tenantId}";
        var clientId = operation == DistributedApplicationOperation.Run ? ClientId : "{entra-web.outputs.clientId}";
        var expectedProject = new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = signInTenant ?? tenantId,
            ["AzureAd__ClientId"] = clientId
        };
        var expectedContainer = new Dictionary<string, string>
        {
            ["ENTRA_INSTANCE"] = "https://login.microsoftonline.com/",
            ["ENTRA_TENANT_ID"] = signInTenant ?? tenantId,
            ["ENTRA_CLIENT_ID"] = clientId
        };
        if (signInTenant is not null)
        {
            expectedProject["AzureAd__AppHomeTenantId"] = tenantId;
            expectedContainer["ENTRA_APP_HOME_TENANT_ID"] = tenantId;
        }

        Assert.Equal(expectedProject, await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource, operation, TestServiceProvider.Instance));
        Assert.Equal(expectedContainer, await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, operation, TestServiceProvider.Instance));
    }

    [Fact]
    public async Task WithReference_EnvironmentResolutionWaitsForProvisionedOutputs()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        var node = builder.AddExecutable("node", "node", ".")
            .WithReference(entra);

        using var app = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var environmentTask = EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            node.Resource, DistributedApplicationOperation.Run, app.Services).AsTask();

        Assert.False(environmentTask.IsCompleted);
        entra.Resource.Outputs["tenantId"] = TenantId;
        entra.Resource.Outputs["clientId"] = ClientId;
        entra.Resource.ProvisioningTaskCompletionSource!.SetResult();

        Assert.Equal(new Dictionary<string, string>
        {
            ["ENTRA_WEB_INSTANCE"] = "https://login.microsoftonline.com/",
            ["ENTRA_WEB_TENANT_ID"] = TenantId,
            ["ENTRA_WEB_CLIENT_ID"] = ClientId
        }, await environmentTask.WaitAsync(cts.Token));
    }

    [Theory]
    [InlineData(nameof(ProjectResource), false)]
    [InlineData(nameof(ExecutableResource), false)]
    [InlineData(nameof(ContainerResource), false)]
    [InlineData(nameof(ProjectResource), true)]
    [InlineData(nameof(ExecutableResource), true)]
    [InlineData(nameof(ContainerResource), true)]
    public void WithReference_DoesNotAddImplicitWaits(string resourceType, bool existing)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = existing
            ? builder.AddExistingEntraApplication("entra-web", tenantId: TenantId, clientId: ClientId)
            : builder.AddEntraIdApplication("entra-web");

        IResourceBuilder<IResourceWithEnvironment> consumer = resourceType switch
        {
            nameof(ProjectResource) => builder.AddResource(new ProjectResource("consumer")),
            nameof(ExecutableResource) => builder.AddExecutable("consumer", "node", "."),
            nameof(ContainerResource) => builder.AddContainer("consumer", "test-image"),
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType))
        };

        Assert.Same(consumer, consumer.WithReference(entra));
        consumer.WithReference(entra, connectionName: "OTHER");

        Assert.Empty(consumer.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public void WithReference_PreservesExplicitWaitBehavior()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        var node = builder.AddExecutable("node", "node", ".")
            .WaitFor(entra, WaitBehavior.StopOnResourceUnavailable)
            .WithReference(entra);

        var wait = Assert.Single(node.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Same(entra.Resource, wait.Resource);
        Assert.Equal(WaitBehavior.StopOnResourceUnavailable, wait.WaitBehavior);
    }

    [Fact]
    public async Task WithReference_EnvironmentResolutionPropagatesProvisioningFailure()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        var node = builder.AddExecutable("node", "node", ".")
            .WithReference(entra);

        using var app = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failure = new InvalidOperationException("Deployment failed.");
        var environmentTask = EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            node.Resource, DistributedApplicationOperation.Run, app.Services).AsTask();
        Assert.False(environmentTask.IsCompleted);
        entra.Resource.ProvisioningTaskCompletionSource!.SetException(failure);

        var exception = await Assert.ThrowsAsync<AggregateException>(() => environmentTask.WaitAsync(cts.Token));
        Assert.Collection(exception.InnerExceptions,
            error => Assert.Same(failure, error),
            error => Assert.Same(failure, error));
    }

    [Fact]
    public async Task ClientIdOutputReference_WaitsForProvisioning()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var value = entra.Resource.ClientIdOutputReference.GetValueAsync(cts.Token).AsTask();

        Assert.False(value.IsCompleted);
        entra.Resource.Outputs["clientId"] = ClientId;
        entra.Resource.ProvisioningTaskCompletionSource!.SetResult();

        Assert.Equal(ClientId, await value);
    }

    [Fact]
    public async Task ClientIdOutputReference_CancelingAWaitDoesNotCancelProvisioning()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        using var cts = new CancellationTokenSource();
        var value = entra.Resource.ClientIdOutputReference.GetValueAsync(cts.Token).AsTask();

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => value);
        Assert.False(entra.Resource.ProvisioningTaskCompletionSource!.Task.IsCompleted);
        entra.Resource.Outputs["clientId"] = ClientId;
        entra.Resource.ProvisioningTaskCompletionSource.SetResult();

        Assert.Equal(ClientId, await entra.Resource.ClientIdOutputReference.GetValueAsync());
    }
}
