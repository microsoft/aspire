// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Azure environment resources are experimental.
#pragma warning disable ASPIREAZURE003 // Tenant-scoped resource and managed identity annotations are experimental.
#pragma warning disable ASPIREPIPELINES001 // Verifies provisioning annotations on existing resources.
#pragma warning disable ASPIREPIPELINES003 // Exercises the deployment pipeline for existing registrations.

using Aspire.Hosting.ApplicationModel;
using System.Runtime.CompilerServices;
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
    public async Task WithAppRole_GeneratesAppRolesWithStableIds()
    {
        static string Generate()
        {
            using var builder = TestDistributedApplicationBuilder.Create();
            var entra = builder.AddEntraIdApplication("entra-api")
                .WithAppRole(new EntraIdAppRole("User", "User", "Can use the API."))
                .WithAppRole(new EntraIdAppRole("Admin", "Administrator", "Can administer the API.", ["User", "Application"]))
                .WithAppRole(new EntraIdAppRole("Reader", "Reader", "Can read data.", ["Application"], new Guid("11112222-aaaa-3333-bbbb-4444cccc5555")))
                .WithAppRole(new EntraIdAppRole("Retired", "Retired", "Being removed.", isEnabled: false));
            return entra.Resource.GetBicepTemplateString();
        }

        var bicep = Generate();
        Assert.Equal(bicep, Generate());
        await Verify(bicep, "bicep");
    }

    [Fact]
    public void WithAppRole_ValidatesArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api").WithAppRole(new EntraIdAppRole("Admin", "Admin", "Admin role."));

        Assert.Throws<ArgumentException>(() => entra.WithAppRole(new EntraIdAppRole("Admin", "Other", "Duplicate value.")));
        Assert.Throws<ArgumentException>(() => entra.WithAppRole(new EntraIdAppRole("Has Space", "Name", "Description.")));
        Assert.Throws<ArgumentException>(() => entra.WithAppRole(new EntraIdAppRole("Other", "Name", "Description.", [])));
        Assert.Throws<ArgumentException>(() => entra.WithAppRole(new EntraIdAppRole("Other", "Name", "Description.", id: Guid.Empty)));
        Assert.Throws<ArgumentException>(() => entra.WithAppRole(new EntraIdAppRole("Other", " ", "Description.")));
        Assert.Single(entra.Resource.AppRoles);
    }

    [Theory]
    [InlineData(".Admin")]
    [InlineData("Admin\"")]
    [InlineData("Ad min")]
    [InlineData("Adm\u00efn")]
    [InlineData("Admin\n")]
    public void AppRole_RejectsValuesThatGraphRejects(string value)
    {
        Assert.Throws<ArgumentException>(() => new EntraIdAppRole(value, "Name", "Description."));
    }

    [Theory]
    [InlineData("Task.Write")]
    [InlineData("Admin:Read-Only_2")]
    [InlineData("a!#$%&'()*+,-/:;<=>?@[]^_`{|}~")]
    public void AppRole_AcceptsValuesThatGraphAllows(string value)
    {
        Assert.Equal(value, new EntraIdAppRole(value, "Name", "Description.").Value);
    }

    [Fact]
    public void AppRole_RejectsValuesLongerThanGraphAllows()
    {
        Assert.Throws<ArgumentException>(() => new EntraIdAppRole(new string('a', 121), "Name", "Description."));
        Assert.Equal(120, new EntraIdAppRole(new string('a', 120), "Name", "Description.").Value.Length);
    }

    [Fact]
    public void AppRoles_CannotBeMutatedThroughTheCollection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api").WithAppRole(new EntraIdAppRole("Admin", "Admin", "Admin role."));

        var roles = Assert.IsAssignableFrom<IList<EntraIdAppRole>>(entra.Resource.AppRoles);
        Assert.Throws<NotSupportedException>(() => roles.Add(new EntraIdAppRole("Other", "Other", "Other role.")));
        Assert.Throws<NotSupportedException>(roles.Clear);
        Assert.Single(entra.Resource.AppRoles);
    }

    [Fact]
    public void WithAppRole_RejectsDuplicateRoleIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var id = Guid.NewGuid();
        var entra = builder.AddEntraIdApplication("entra-api").WithAppRole(new EntraIdAppRole("A", "A", "A role.", id: id));

        Assert.Throws<ArgumentException>(() => entra.WithAppRole(new EntraIdAppRole("B", "B", "B role.", id: id)));
    }

    [Fact]
    public void WithAppRole_ExistingApplication_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddExistingEntraApplication("entra-api", "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002");

        Assert.Throws<InvalidOperationException>(() => entra.WithAppRole(new EntraIdAppRole("Admin", "Admin", "Admin role.")));
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
    public void WithOwners_NormalizesAndDeduplicatesGuidFormats()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithApplicationOwners(
                "00001111-AAAA-2222-bbbb-3333cccc4444",
                "00001111aaaa2222bbbb3333cccc4444",
                "{00001111-aaaa-2222-bbbb-3333cccc4444}");

        Assert.Equal(["00001111-aaaa-2222-bbbb-3333cccc4444"], entra.Resource.ApplicationOwners);
    }

    [Fact]
    public void DefaultProvisioning_HasNoOwners()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");

        Assert.DoesNotContain("owners", entra.Resource.GetBicepTemplateString());
    }

    [Fact]
    public async Task WithFederatedManagedIdentityCredential_Identity_GeneratesFederatedCredential()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential(identity);

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task WithFederatedManagedIdentityCredential_ConsumerIdentity_GeneratesCredentialPerConsumer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var webIdentity = builder.AddAzureUserAssignedIdentity("web-identity");
        var apiIdentity = builder.AddAzureUserAssignedIdentity("api-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential();
        builder.AddResource(new ProjectResource("web")).WithAzureUserAssignedIdentity(webIdentity).WithReference(entra);
        builder.AddResource(new ProjectResource("api")).WithAzureUserAssignedIdentity(apiIdentity).WithReference(entra);

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task WithFederatedManagedIdentityCredential_ConsumerIdentity_EmitsClientIdOutputInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential();
        var web = builder.AddResource(new ProjectResource("web")).WithAzureUserAssignedIdentity(identity).WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            web.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Equal("{web-identity.outputs.clientId}", env["AzureAd__ClientCredentials__0__ManagedIdentityClientId"]);
        Assert.Equal("SignedAssertionFromManagedIdentity", env["AzureAd__ClientCredentials__0__SourceType"]);
    }

    [Fact]
    public void ValidateManagedIdentities_RejectsResourceWithoutIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential(identity);
        builder.AddResource(new ProjectResource("web")).WithReference(entra);

        var exception = Assert.Throws<InvalidOperationException>(entra.Resource.ValidateManagedIdentities);
        Assert.Contains("no user-assigned managed identity", exception.Message);
    }

    [Fact]
    public async Task ValidateManagedIdentities_RejectsResourceUsingAnotherIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var trusted = builder.AddAzureUserAssignedIdentity("trusted-identity");
        var other = builder.AddAzureUserAssignedIdentity("other-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential(trusted);
        var web = builder.AddResource(new ProjectResource("web")).WithAzureUserAssignedIdentity(other).WithReference(entra);

        Assert.Contains("WithAzureUserAssignedIdentity", Assert.Throws<InvalidOperationException>(entra.Resource.ValidateManagedIdentities).Message);
        // The identity that was passed in is used as the value without looking the consumer up.
        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            web.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);
        Assert.Equal("{trusted-identity.outputs.clientId}", env["AzureAd__ClientCredentials__0__ManagedIdentityClientId"]);
    }

    [Fact]
    public async Task PublishWithFederatedManagedIdentityCredential_EnvironmentCallbackRunBeforeIdentityExists_StillResolvesIt()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential();
        var web = builder.AddResource(new ProjectResource("web")).WithReference(entra);

        // The Azure resource preparer runs the environment callbacks to discover dependencies before it attaches
        // identities, and a callback's result is cached, so the value must not need the identity at that point.
        var dependencies = await web.Resource.GetResourceDependenciesAsync(builder.ExecutionContext, ResourceDependencyDiscoveryMode.DirectOnly);
        Assert.Contains(entra.Resource, dependencies);

        var identity = builder.AddAzureUserAssignedIdentity("web-identity");
        web.WithAnnotation(new AppIdentityAnnotation(identity.Resource));
        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            web.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);
        Assert.Equal("{web-identity.outputs.clientId}", env["AzureAd__ClientCredentials__0__ManagedIdentityClientId"]);
    }
    [Fact]
    public async Task BeforeStart_ComputeEnvironmentWithIdentities_TrustsTheIdentityTheAzurePreparerCreated()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential();
        builder.AddContainer("web", "image").WithReference(entra);

        using var app = builder.Build();
        // Pipeline resolution reads the template before the Azure resource preparer attaches the identities.
        Assert.DoesNotContain("web_identity_principalId", entra.Resource.GetBicepTemplateString());
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var identity = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());
        Assert.Equal("web-identity", identity.Name);
        Assert.Same(identity, Assert.Single(entra.Resource.GetFederatedIdentities()));
        Assert.Contains("web_identity_principalId", entra.Resource.GetBicepTemplateString());
    }

    [Fact]
    public async Task BeforeStart_ComputeEnvironmentWithoutIdentities_FailsWithTheResourceName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential();
        builder.AddContainer("web", "image").WithReference(entra);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("Resource 'web'", exception.ToString());
        Assert.Contains("no user-assigned managed identity", exception.ToString());
    }
    [Fact]
    public void PublishWithFederatedManagedIdentityCredential_Identity_DoesNothingWhenRunning()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var identity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential(identity);

        // Managed identities exist only in Azure, and when running the identity is not part of the model. A federated
        // credential would make the registration's Bicep reference an identity that is never provisioned.
        Assert.Empty(entra.Resource.ClientCredentials);
        var bicep = entra.Resource.GetBicepTemplateString();
        Assert.DoesNotContain("federatedIdentityCredentials", bicep);
        Assert.DoesNotContain("principalId", bicep);
    }

    [Fact]
    public void WithCredential_ConsumerIdentityCredential_GeneratesNoTrustWhenRunning()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithCredential(new EntraIdFederatedIdentityCredential(ClientId));
        var web = builder.AddResource(new ProjectResource("web")).WithReference(entra);

        // A raw credential cannot name an identity resource, so the registration's Bicep cannot reference an identity
        // that is not provisioned when running.
        var bicep = entra.Resource.GetBicepTemplateString();
        Assert.DoesNotContain("federatedIdentityCredentials", bicep);
        Assert.DoesNotContain("principalId", bicep);
        Assert.Empty(entra.Resource.GetFederatedIdentities());
    }

    [Fact]
    public void PublishWithFederatedManagedIdentityCredential_Identity_AddsTheCredentialWhenPublishing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential(identity);
        var web = builder.AddResource(new ProjectResource("web"));

        var credential = Assert.Single(entra.Resource.ClientCredentials);
        var envVars = new Dictionary<string, object>();
        Assert.True(credential.EmitEnvironmentVariables(envVars, "AzureAd__ClientCredentials__0", "__", entra.Resource, web.Resource, builder.ExecutionContext));
        Assert.NotEmpty(envVars);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(64)]
    public void FederatedCredentialName_IsPrefixedSoItFitsGraphsLengthLimits(int identityNameLength)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identityName = new string('i', identityNameLength);
        var identity = builder.AddAzureUserAssignedIdentity(identityName);
        var entra = builder.AddEntraIdApplication(new string('e', 64))
            .PublishWithFederatedManagedIdentityCredential(identity);

        // Microsoft Graph requires a federated credential's name to be 3-120 characters. Prefixing the registration's name
        // gives 129 characters for two valid 64-character names, and an unprefixed one- or two-character identity name is too short.
        var credentialName = $"aspire-{identityName}";
        Assert.InRange(credentialName.Length, 3, 120);
        var bicep = entra.Resource.GetBicepTemplateString();
        Assert.Contains($"/{credentialName}'", bicep);
        Assert.DoesNotContain($"{new string('e', 64)}-{identityName}", bicep);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/", "https://login.microsoftonline.com")]
    [InlineData("https://LOGIN.MICROSOFTONLINE.COM/", "https://login.microsoftonline.com")]
    [InlineData("https://login.microsoftonline.com:443/", "https://login.microsoftonline.com")]
    [InlineData("https://Login.MicrosoftOnline.US:443", "https://login.microsoftonline.us")]
    [InlineData("https://login.contoso.example:8443/", "https://login.contoso.example:8443")]
    public void GetIssuerPrefix_NormalizesTheHostAndDropsTheDefaultPort(string instance, string expected)
    {
        Assert.Equal(expected, EntraIdFederation.GetIssuerPrefix(instance));
    }

    [Fact]
    public void FederatedCredentialIssuer_UsesTheNormalizedInstance()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithInstance("https://LOGIN.MICROSOFTONLINE.COM:443/")
            .PublishWithFederatedManagedIdentityCredential(identity);

        // The issuer must equal the token's iss claim exactly.
        Assert.Contains("issuer: 'https://login.microsoftonline.com/${tenant().tenantId}/v2.0'", entra.Resource.GetBicepTemplateString());
    }

    [Fact]
    public void FederatedCredentialPrincipalIdParameter_DoesNotCollideWithOtherParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var redirectUris = builder.AddParameter("web-identity-principalId");
        var webIdentity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(redirectUris)
            .PublishWithFederatedManagedIdentityCredential(webIdentity);

        var bicep = entra.Resource.GetBicepTemplateString();

        // The redirect URI parameter keeps its value, and the identity gets its own parameter.
        Assert.Same(redirectUris.Resource, entra.Resource.Parameters["web_identity_principalId"]);
        Assert.Same(webIdentity.Resource, Assert.IsType<BicepOutputReference>(entra.Resource.Parameters["web_identity_principalId_2"]).Resource);
        Assert.Contains("subject: web_identity_principalId_2", bicep);

        // Generating the template again must give the same names.
        Assert.Equal(bicep, entra.Resource.GetBicepTemplateString());
    }

    [Fact]
    public void FederatedCredentialPrincipalIdParameter_DoesNotCollideWithParametersThatDifferOnlyInCasing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        // Bicep rejects parameters that differ only in casing (BCP353).
        var redirectUris = builder.AddParameter("WEB-identity-principalId");
        var webIdentity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(redirectUris)
            .PublishWithFederatedManagedIdentityCredential(webIdentity);

        var bicep = entra.Resource.GetBicepTemplateString();

        Assert.Same(redirectUris.Resource, entra.Resource.Parameters["WEB_identity_principalId"]);
        Assert.Same(webIdentity.Resource, Assert.IsType<BicepOutputReference>(entra.Resource.Parameters["web_identity_principalId_2"]).Resource);
        Assert.Contains("subject: web_identity_principalId_2", bicep);
    }

    [Fact]
    public void FederatedCredentialResourceIdentifier_DoesNotCollideWithParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        // Parameters and resources share one namespace, and this parameter normalizes to the credential's resource identifier.
        var redirectUris = builder.AddParameter("federatedCredential-web-identity");
        var webIdentity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(redirectUris)
            .PublishWithFederatedManagedIdentityCredential(webIdentity);

        var bicep = entra.Resource.GetBicepTemplateString();

        Assert.Contains("param federatedCredential_web_identity string", bicep);
        Assert.Contains("resource federatedCredential_web_identity_2 ", bicep);
    }

    [Fact]
    public void ValidateManagedIdentities_IgnoresResourcesExcludedFromTheManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential();
        var published = builder.AddResource(new ProjectResource("web")).WithAzureUserAssignedIdentity(identity).WithReference(entra);
        builder.AddResource(new ProjectResource("local-only")).ExcludeFromManifest().WithReference(entra);

        // The excluded resource is not deployed, so the Azure resource preparer never gives it an identity.
        entra.Resource.ValidateManagedIdentities();
        Assert.Same(identity.Resource, Assert.Single(entra.Resource.GetFederatedIdentities()));
        Assert.NotNull(published.Resource);
    }
    [Theory]
    [InlineData("https://login.microsoftonline.com/", "api://AzureADTokenExchange")]
    [InlineData("https://login.microsoftonline.us/", "api://AzureADTokenExchangeUSGov")]
    [InlineData("https://login.partner.microsoftonline.cn/", "api://AzureADTokenExchangeChina")]
    [InlineData("https://login.chinacloudapi.cn/", "api://AzureADTokenExchangeChina")]
    [InlineData("https://LOGIN.CHINACLOUDAPI.CN:443/", "api://AzureADTokenExchangeChina")]
    public void GetTokenExchangeAudience_MapsSupportedClouds(string instance, string expected)
    {
        Assert.Equal(expected, EntraIdFederation.GetTokenExchangeAudience(instance));
    }

    [Fact]
    public void WithFederatedManagedIdentityCredential_Identity_RejectsUnsupportedCloud()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("web-identity");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential(identity);
        entra.Resource.Instance = "https://login.contoso.example/";

        var exception = Assert.Throws<InvalidOperationException>(entra.Resource.GetBicepTemplateString);
        Assert.Contains("no known managed identity token exchange audience", exception.Message);
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
    public async Task Publish_EndpointRedirectUri_UsesContainerAppEnvironmentHostAndOutputs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, workspace.Path);
        builder.AddAzureContainerAppEnvironment("env");
        var entra = builder.AddEntraIdApplication("entra-web");
        var web = builder.AddContainer("web", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithReference(entra);
        entra.WithRedirectUris(web.GetEndpoint("http"), "/signin-oidc");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environment = model.Resources.OfType<AzureEnvironmentResource>().Single();
        await using var step = await new TestPipelineActivityReporter(outputHelper).CreateStepAsync("Publishing endpoint redirect URI");
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
    public async Task Publish_FederatedManagedIdentityCredential_PublishesConsumerIdentityClientId()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, workspace.Path);
        builder.AddAzureContainerAppEnvironment("env");
        var entra = builder.AddEntraIdApplication("entra-web")
            .PublishWithFederatedManagedIdentityCredential();
        builder.AddContainer("web", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithReference(entra);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environment = model.Resources.OfType<AzureEnvironmentResource>().Single();
        await using var step = await new TestPipelineActivityReporter(outputHelper).CreateStepAsync("Publishing federated credential");
        var context = new AzurePublishingContext(
            workspace.Path,
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>().Value,
            app.Services,
            NullLogger<AzurePublishingContext>.Instance,
            step);

        await context.WriteModelAsync(model, environment);

        await Verify(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "web", "web.bicep")), "bicep");
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
        Assert.Throws<ArgumentNullException>(() => entra.WithRedirectUris((string[])null!));
        Assert.Throws<ArgumentException>(() => entra.WithRedirectUris("https://localhost:7252/callback", ""));
        Assert.Equal(new[] { "https://localhost:7123/signin-oidc", "https://localhost:7123/signout-callback-oidc" }, entra.Resource.RedirectUris);
    }

    [Fact]
    public void WithRedirectUris_Parameters_ValidatesAndAccumulates()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        var signIn = builder.AddParameter("signin");
        var signOut = builder.AddParameter("signout");

        Assert.Same(entra, entra.WithRedirectUris(signIn, signOut));
        Assert.Throws<ArgumentNullException>(() => EntraIdResourceExtensions.WithRedirectUris(null!, signIn));
        Assert.Throws<ArgumentNullException>(() => entra.WithRedirectUris((IResourceBuilder<ParameterResource>[])null!));
        Assert.Throws<ArgumentNullException>(() => entra.WithRedirectUris(signIn, (IResourceBuilder<ParameterResource>)null!));
        Assert.Equal(new[] { signIn.Resource, signOut.Resource }, entra.Resource.RedirectUriParameters);
    }

    [Fact]
    public async Task WithRedirectUris_Parameters_WritesSplitParameterValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(builder.AddParameter("web-redirect-uris"))
            .WithRedirectUris("https://localhost:7123/signin-oidc")
            .WithRedirectUris(builder.AddParameter("extra-redirect-uris"));

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
        Assert.Equal(new[] { "tenantResourceSuffix", "web_redirect_uris", "extra_redirect_uris" }, entra.Resource.Parameters.Keys);
    }

    [Fact]
    public async Task WithRedirectUris_SingleParameter_WritesSplitParameterValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(builder.AddParameter("web-redirect-uris"));

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task WithRedirectUris_Endpoint_RunMode_UsesAllocatedEndpointUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddContainer("web", "image").WithHttpsEndpoint(targetPort: 8080, name: "https");
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(web.GetEndpoint("https"), "/signin-oidc", "/signout-callback-oidc");

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
        Assert.Equal(new[] { "tenantResourceSuffix", "redirectUri0", "redirectUri1" }, entra.Resource.Parameters.Keys);
        Assert.All(
            entra.Resource.Parameters.Where(p => p.Key.StartsWith("redirectUri", StringComparison.Ordinal)),
            p => Assert.IsType<ReferenceExpression>(p.Value));
    }

    [Fact]
    public async Task WithRedirectUris_Endpoint_PublishMode_UsesComputeEnvironmentHost()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddResource(new TestComputeEnvironment("env"));
        var web = builder.AddContainer("web", "image")
            .WithHttpsEndpoint(targetPort: 8080, name: "https")
            .WithComputeEnvironment(environment);
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(web.GetEndpoint("https"), "/signin-oidc");

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task WithRedirectUris_Endpoint_RunMode_UsesPrimaryUrlWhenItIsADevLocalhostName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddContainer("web", "image")
            .WithHttpsEndpoint(targetPort: 8080, name: "https")
            .WithEndpoint("https", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 7081));
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(web.GetEndpoint("https"), "/signin-oidc");

        _ = entra.Resource.GetBicepTemplateString();
        var expression = Assert.IsType<ReferenceExpression>(entra.Resource.Parameters["redirectUri0"]);
        var resolveTask = expression.GetValueAsync(new ValueProviderContext(), CancellationToken.None).AsTask();
        Assert.False(resolveTask.IsCompleted);

        // Mimics the orchestrator: when the dashboard uses a *.dev.localhost name the friendly URL is primary and
        // the plain localhost URL is only shown in the details.
        var endpoint = web.GetEndpoint("https");
        var urls = new List<ResourceUrlAnnotation>
        {
            new() { Url = "https://web-hashr.dev.localhost:7081", Endpoint = endpoint, DisplayLocation = UrlDisplayLocation.SummaryAndDetails },
            new() { Url = "https://localhost:7081", Endpoint = endpoint, DisplayLocation = UrlDisplayLocation.DetailsOnly }
        };
        var context = new ResourceUrlsCallbackContext(builder.ExecutionContext, web.Resource, urls);
        foreach (var callback in web.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        Assert.Equal("https://web-hashr.dev.localhost:7081/signin-oidc", await resolveTask.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    [Fact]
    public async Task WithRedirectUris_Host_WritesHostParameterWithPaths()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var host = builder.AddParameter("web-host");
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(host, "/signin-oidc", "/signout-callback-oidc");

        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
        var expression = Assert.IsType<ReferenceExpression>(entra.Resource.Parameters["redirectUri0"]);
        Assert.Equal("https://{0}/signin-oidc", expression.Format);
        Assert.Same(host.Resource, Assert.Single(expression.ValueProviders));
    }

    [Fact]
    public void WithRedirectUris_Host_RequiresPathStartingWithSlash()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var host = builder.AddParameter("web-host");
        var entra = builder.AddEntraIdApplication("entra-web");

        Assert.Throws<ArgumentException>("callbackPath", () => entra.WithRedirectUris(host, "signin-oidc"));
        Assert.Throws<ArgumentException>("callbackPath", () => entra.WithRedirectUris(host, "/signin-oidc", "other"));
    }

    [Fact]
    public void WithRedirectUris_SingleParameter_BindsToParameterOverload()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(builder.AddParameter("web-redirect-uris"));

        Assert.Single(entra.Resource.RedirectUriParameters);
    }

    [Fact]
    public async Task WithRedirectUris_Endpoint_RunMode_RelativeDisplayUrlResolvesAgainstEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddContainer("web", "image")
            .WithHttpsEndpoint(targetPort: 8080, name: "https")
            .WithEndpoint("https", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 7081));
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(web.GetEndpoint("https"), "/signin-oidc");

        _ = entra.Resource.GetBicepTemplateString();
        var expression = Assert.IsType<ReferenceExpression>(entra.Resource.Parameters["redirectUri0"]);

        // The orchestrator only makes relative URLs absolute after all callbacks have run.
        var urls = new List<ResourceUrlAnnotation> { new() { Url = "/swagger", Endpoint = web.GetEndpoint("https") } };
        var context = new ResourceUrlsCallbackContext(builder.ExecutionContext, web.Resource, urls);
        foreach (var callback in web.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        Assert.Equal("https://localhost:7081/signin-oidc", await expression.GetValueAsync(new ValueProviderContext(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WithRedirectUris_Endpoint_RunMode_OnlyUsesUrlsOfTheSameResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddContainer("web", "image")
            .WithHttpsEndpoint(targetPort: 8080, name: "https")
            .WithEndpoint("https", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 7081));
        var other = builder.AddContainer("other", "image")
            .WithHttpsEndpoint(targetPort: 8080, name: "https")
            .WithEndpoint("https", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 7082));
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(web.GetEndpoint("https"), "/signin-oidc");

        _ = entra.Resource.GetBicepTemplateString();
        var expression = Assert.IsType<ReferenceExpression>(entra.Resource.Parameters["redirectUri0"]);

        // web's own https URL is details-only; the other resource's same-named endpoint link is summary-visible.
        var urls = new List<ResourceUrlAnnotation>
        {
            new() { Url = "https://localhost:7081", Endpoint = web.GetEndpoint("https"), DisplayLocation = UrlDisplayLocation.DetailsOnly },
            new() { Url = "https://other.dev.localhost:7082", Endpoint = other.GetEndpoint("https"), DisplayLocation = UrlDisplayLocation.SummaryAndDetails }
        };
        var context = new ResourceUrlsCallbackContext(builder.ExecutionContext, web.Resource, urls);
        foreach (var callback in web.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        Assert.Equal("https://localhost:7081/signin-oidc", await expression.GetValueAsync(new ValueProviderContext(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WithRedirectUris_Endpoint_RunMode_ClearedUrlsFallBackToAllocatedUrlWithoutWaiting()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddContainer("web", "image")
            .WithHttpsEndpoint(targetPort: 8080, name: "https")
            .WithEndpoint("https", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 7081));
        var entra = builder.AddEntraIdApplication("entra-web")
            .WithRedirectUris(web.GetEndpoint("https"), "/signin-oidc");

        _ = entra.Resource.GetBicepTemplateString();
        var expression = Assert.IsType<ReferenceExpression>(entra.Resource.Parameters["redirectUri0"]);

        // An earlier WithUrls callback removed every displayed URL.
        var context = new ResourceUrlsCallbackContext(builder.ExecutionContext, web.Resource, []);
        foreach (var callback in web.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        // Far below the 30 second fallback timeout.
        Assert.Equal("https://localhost:7081/signin-oidc", await expression.GetValueAsync(new ValueProviderContext(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WithRedirectUris_DerivedParameterNames_DoNotCollideWithUserParameters()
    {
        foreach (var derivedFirst in new[] { false, true })
        {
            using var builder = TestDistributedApplicationBuilder.Create();
            var existing = builder.AddParameter("redirectUri0", "https://existing.contoso.com/callback");
            var host = builder.AddParameter("host", "web.contoso.com");
            var entra = builder.AddEntraIdApplication("entra");
            if (derivedFirst)
            {
                entra.WithRedirectUris(host, "/signin-oidc").WithRedirectUris(existing);
            }
            else
            {
                entra.WithRedirectUris(existing).WithRedirectUris(host, "/signin-oidc");
            }

            var bicep = entra.Resource.GetBicepTemplateString();
            Assert.Same(existing.Resource, entra.Resource.Parameters["redirectUri0"]);
            Assert.IsType<ReferenceExpression>(entra.Resource.Parameters["redirectUri1"]);
            Assert.Contains("split(redirectUri0, ',')", bicep);
            Assert.Contains("redirectUri1", bicep);

            // Generating again must give the same names.
            Assert.Equal(bicep, entra.Resource.GetBicepTemplateString());
        }
    }

    [Fact]
    public void WithRedirectUris_Endpoint_RequiresPathStartingWithSlash()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddContainer("web", "image").WithHttpsEndpoint(targetPort: 8080, name: "https");
        var entra = builder.AddEntraIdApplication("entra-web");

        Assert.Throws<ArgumentException>("callbackPaths", () => entra.WithRedirectUris(web.GetEndpoint("https")));
        Assert.Throws<ArgumentException>("callbackPaths", () => entra.WithRedirectUris(web.GetEndpoint("https"), "signin-oidc"));
    }

#pragma warning disable ASPIRECOMPUTE002 // Test compute environment implements the experimental host address member.
    private sealed class TestComputeEnvironment(string name) : Resource(name), IComputeEnvironmentResource
    {
        public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference) =>
            ReferenceExpression.Create($"{endpointReference.Resource.Name}.contoso.example");
    }
#pragma warning restore ASPIRECOMPUTE002

    [Fact]
    public async Task ConfigureInfrastructure_WritesDirectResourcePropertyValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");
        entra.Resource.SignInAudience = EntraIdSignInAudience.AzureADMultipleOrgs;
        entra.Resource.AddRedirectUri("https://localhost:7123/signin-oidc");
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AddExistingEntraApplication_DeployDoesNotRequireAzure(bool useParameters, bool useExport)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputHelper, workspace.Path, step: WellKnownPipelineSteps.Deploy);
        var entra = useExport
            ? builder.AddExistingEntraApplicationForExport("entra-web",
                useParameters ? builder.AddParameter("tenant-id", TenantId) : TenantId,
                useParameters ? builder.AddParameter("client-id", ClientId) : ClientId)
            : useParameters
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
        // The provisioning step and the managed identity validation step.
        Assert.Equal(2, created.Annotations.OfType<PipelineStepAnnotation>().Count());
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
