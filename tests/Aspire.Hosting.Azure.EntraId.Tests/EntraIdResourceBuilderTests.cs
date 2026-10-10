// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdResourceBuilderTests
{
    // Placeholder IDs from the Microsoft Learn documentation. They are well-formed but identify nothing.
    private const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    private const string ClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";

    [Fact]
    public void AddEntraIdApplication_CreatesResource()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal("entra-api", resource.Name);
        Assert.Equal(TenantId, resource.TenantId);
        Assert.Equal(ClientId, resource.ClientId);
    }

    [Fact]
    public void AddEntraIdApplication_AsExistingApplicationWithTenantIdParameter()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var tenantId = appBuilder.AddParameter("EntraTenantId");
        var clientId = appBuilder.AddParameter("EntraApiClientId");

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: tenantId, clientId: clientId);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.NotNull(resource.TenantIdParameter);
        Assert.Equal("EntraTenantId", resource.TenantIdParameter.Name);
    }

    [Fact]
    public void AddEntraIdApplication_AsExistingApplicationWithClientIdParameter()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var tenantId = appBuilder.AddParameter("EntraTenantId");
        var clientId = appBuilder.AddParameter("EntraApiClientId");

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: tenantId, clientId: clientId);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.NotNull(resource.ClientIdParameter);
        Assert.Equal("EntraApiClientId", resource.ClientIdParameter.Name);
    }

    [Fact]
    public void AsExistingApplication_AcceptsTenantDomainName()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: "contoso.onmicrosoft.com", clientId: ClientId);

        Assert.Equal("contoso.onmicrosoft.com", entra.Resource.TenantId);
    }

    [Theory]
    [InlineData("organizations", "AzureADMultipleOrgs")]
    [InlineData("Common", "AzureADandPersonalMicrosoftAccount")]
    [InlineData("CONSUMERS", "PersonalMicrosoftAccount")]
    public void AsExistingApplication_ThrowsWhenTenantIdIsSignInKeyword(string tenantId, string expectedSignInAudience)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var entra = appBuilder.AddEntraIdApplication("entra-api");

        var exception = Assert.Throws<ArgumentException>(() => entra.AsExistingApplication(tenantId: tenantId, clientId: ClientId));
        Assert.Equal("tenantId", exception.ParamName);
        Assert.Equal(
            $"'{tenantId}' is not a valid tenant ID. The keywords 'organizations', 'common' and 'consumers' choose who can sign in; " +
            "they do not identify a tenant. Use the ID of the tenant where the app is registered, and call " +
            $"WithSignInAudience(EntraIdSignInAudience.{expectedSignInAudience}) instead. (Parameter 'tenantId')",
            exception.Message);
    }

    [Fact]
    public void AsExistingApplication_ThrowsWhenTenantIdIsMalformed()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var entra = appBuilder.AddEntraIdApplication("entra-api");

        var exception = Assert.Throws<ArgumentException>(() => entra.AsExistingApplication(tenantId: "my-tenant", clientId: ClientId));
        Assert.Equal("tenantId", exception.ParamName);
        Assert.Equal(
            "'my-tenant' is not a valid tenant ID. Expected the directory (tenant) ID shown on the app registration's Overview page, " +
            "such as 'aaaabbbb-0000-cccc-1111-dddd2222eeee', or a domain name such as 'contoso.onmicrosoft.com'. (Parameter 'tenantId')",
            exception.Message);
    }

    [Fact]
    public void AsExistingApplication_ThrowsWhenClientIdIsMalformed()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var entra = appBuilder.AddEntraIdApplication("entra-api");

        var exception = Assert.Throws<ArgumentException>(() => entra.AsExistingApplication(tenantId: TenantId, clientId: "my-client"));
        Assert.Equal("clientId", exception.ParamName);
        Assert.Equal(
            "'my-client' is not a valid client ID. Expected the application (client) ID shown on the app registration's Overview page, " +
            "such as '00001111-aaaa-2222-bbbb-3333cccc4444'. (Parameter 'clientId')",
            exception.Message);
    }

    [Fact]
    public void AsExistingApplication_ThrowsWhenIdsAreMissing()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var entra = appBuilder.AddEntraIdApplication("entra-api");

        Assert.Throws<ArgumentNullException>(() => entra.AsExistingApplication(tenantId: (string)null!, clientId: ClientId));
        Assert.Throws<ArgumentException>(() => entra.AsExistingApplication(tenantId: string.Empty, clientId: ClientId));
        Assert.Throws<ArgumentNullException>(() => entra.AsExistingApplication(tenantId: TenantId, clientId: (string)null!));
        Assert.Throws<ArgumentException>(() => entra.AsExistingApplication(tenantId: TenantId, clientId: string.Empty));
    }

    [Fact]
    public void AddEntraIdApplication_WithClientSecret()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var secret = appBuilder.AddParameter("EntraWebClientSecret", secret: true);

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithClientSecret(secret);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.ClientCredentials);
        var cred = Assert.IsType<EntraIdClientSecretCredential>(resource.ClientCredentials[0]);
        Assert.Equal("ClientSecret", cred.SourceType);
        Assert.Equal("EntraWebClientSecret", cred.ClientSecret.Name);
    }

    [Fact]
    public void AddEntraIdApplication_DefaultInstance()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal("https://login.microsoftonline.com/", resource.Instance);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.us/")]
    [InlineData("https://contoso.ciamlogin.com/")]
    public void AddEntraIdApplication_WithCustomInstance(string instance)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .WithInstance(instance)
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal(instance, resource.Instance);
    }

    [Theory]
    [InlineData("login.microsoftonline.com")]
    [InlineData("http://login.microsoftonline.com/")]
    [InlineData("https://login.microsoftonline.com/?slice=testslice")]
    [InlineData("https://login.microsoftonline.com/#tenant")]
    public void WithInstance_ThrowsWhenInstanceIsNotHttpsUrl(string instance)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var entra = appBuilder.AddEntraIdApplication("entra-api");

        var exception = Assert.Throws<ArgumentException>(() => entra.WithInstance(instance));
        Assert.Equal("instance", exception.ParamName);
        Assert.Equal(
            $"'{instance}' is not a valid Entra ID instance. Expected an absolute HTTPS URL with no query string or fragment, " +
            "such as 'https://login.microsoftonline.com/'. (Parameter 'instance')",
            exception.Message);
    }

    [Fact]
    public void AddEntraIdApplication_DefaultSignInAudienceIsHomeTenantOnly()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var entra = appBuilder.AddEntraIdApplication("entra-api");

        Assert.Equal(EntraIdSignInAudience.AzureADMyOrg, entra.Resource.SignInAudience);
    }

    [Fact]
    public void WithSignInAudience_ThrowsWhenValueIsNotDefined()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var entra = appBuilder.AddEntraIdApplication("entra-api");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => entra.WithSignInAudience((EntraIdSignInAudience)42));
        Assert.Equal("signInAudience", exception.ParamName);
    }

    [Fact]
    public void AddEntraIdApplication_IsExcludedFromManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var entra = appBuilder.AddEntraIdApplication("entra-api");

        Assert.True(entra.Resource.TryGetAnnotationsOfType<ManifestPublishingCallbackAnnotation>(out var annotations));
        Assert.Equal(ManifestPublishingCallbackAnnotation.Ignore, Assert.Single(annotations));
    }

    [Fact]
    public void AddEntraIdApplication_StartsInWaitingState()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var entra = appBuilder.AddEntraIdApplication("entra-api");

        var annotation = Assert.Single(entra.Resource.Annotations.OfType<ResourceSnapshotAnnotation>());
        Assert.Equal("EntraIdApplication", annotation.InitialSnapshot.ResourceType);
        Assert.Equal(KnownResourceStates.Waiting, annotation.InitialSnapshot.State?.Text);
        Assert.Empty(annotation.InitialSnapshot.Properties);
    }

    [Fact]
    public void AddEntraIdApplication_WithClientCapability()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithClientCapability("cp1");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.ClientCapabilities);
        Assert.Contains("cp1", resource.ClientCapabilities);
    }

    [Fact]
    public void AddEntraIdApplication_WithACLAuthorization()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithAllowWebApiToBeAuthorizedByACL();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.True(resource.AllowWebApiToBeAuthorizedByACL);
    }

    [Fact]
    public void AddEntraIdApplication_WithExtraQueryParameter()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithExtraQueryParameter("dc", "prod-wst-01")
            .WithExtraQueryParameter("slice", "testslice");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal(2, resource.ExtraQueryParameters.Count);
        Assert.Equal("prod-wst-01", resource.ExtraQueryParameters["dc"]);
        Assert.Equal("testslice", resource.ExtraQueryParameters["slice"]);
    }

    [Fact]
    public void AddEntraIdApplication_WithAudiences()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithAudience($"api://{ClientId}");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.Audiences);
        Assert.Contains($"api://{ClientId}", resource.Audiences);
    }

    [Fact]
    public void AddEntraIdApplication_WithFicMsi()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithFicMsi("mi-client-id");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.ClientCredentials);
        var cred = Assert.IsType<EntraIdFederatedIdentityCredential>(resource.ClientCredentials[0]);
        Assert.Equal("SignedAssertionFromManagedIdentity", cred.SourceType);
        Assert.Equal("mi-client-id", cred.ManagedIdentityClientId);
    }

    [Fact]
    public void AddEntraIdApplication_WithFicMsi_SystemAssigned()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithFicMsi();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.ClientCredentials);
        var cred = Assert.IsType<EntraIdFederatedIdentityCredential>(resource.ClientCredentials[0]);
        Assert.Equal("SignedAssertionFromManagedIdentity", cred.SourceType);
        Assert.Null(cred.ManagedIdentityClientId);
    }

    [Fact]
    public void AddEntraIdApplication_MultipleCredentials()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var secret = appBuilder.AddParameter("EntraSecret", secret: true);

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithClientSecret(secret)
            .WithFicMsi("mi-client-id");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal(2, resource.ClientCredentials.Count);
        Assert.IsType<EntraIdClientSecretCredential>(resource.ClientCredentials[0]);
        Assert.IsType<EntraIdFederatedIdentityCredential>(resource.ClientCredentials[1]);
    }

    [Fact]
    public void AddEntraIdApplication_WithCertificateFromKeyVault()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithCertificateFromKeyVault("https://myvault.vault.azure.net", "MyCert");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.ClientCredentials);
        var cred = Assert.IsType<EntraIdKeyVaultCertificateCredential>(resource.ClientCredentials[0]);
        Assert.Equal("KeyVault", cred.SourceType);
        Assert.Equal("https://myvault.vault.azure.net", cred.KeyVaultUrl);
        Assert.Equal("MyCert", cred.CertificateNameInKeyVault);
    }

    [Fact]
    public async Task WithReference_InjectsEnvironmentVariables()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var secret = appBuilder.AddParameter("EntraSecret", "super-secret", secret: true);

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithClientSecret(secret)
            .WithAudience($"api://{ClientId}")
            .WithClientCapability("cp1")
            .WithAllowWebApiToBeAuthorizedByACL()
            .WithExtraQueryParameter("dc", "prod-wst-01");

        var container = appBuilder.AddResource(new ProjectResource("api"))
            .WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = TenantId,
            ["AzureAd__ClientId"] = ClientId,
            ["AzureAd__ClientCredentials__0__SourceType"] = "ClientSecret",
            ["AzureAd__ClientCredentials__0__ClientSecret"] = "super-secret",
            ["AzureAd__ClientCapabilities__0"] = "cp1",
            ["AzureAd__Audiences__0"] = $"api://{ClientId}",
            ["AzureAd__AllowWebApiToBeAuthorizedByACL"] = "true",
            ["AzureAd__ExtraQueryParameters__dc"] = "prod-wst-01"
        }, env);
    }

    [Fact]
    public async Task WithReference_DefaultSignInAudience_EmitsHomeTenantAsTenantId()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        var container = appBuilder.AddResource(new ProjectResource("api"))
            .WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = TenantId,
            ["AzureAd__ClientId"] = ClientId
        }, env);
    }

    [Theory]
    [InlineData(EntraIdSignInAudience.AzureADMultipleOrgs, "organizations")]
    [InlineData(EntraIdSignInAudience.AzureADandPersonalMicrosoftAccount, "common")]
    [InlineData(EntraIdSignInAudience.PersonalMicrosoftAccount, "consumers")]
    public async Task WithReference_OtherSignInAudiences_EmitKeywordAndHomeTenant(EntraIdSignInAudience signInAudience, string expectedTenantId)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithSignInAudience(signInAudience);

        var container = appBuilder.AddResource(new ProjectResource("api"))
            .WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(signInAudience, entra.Resource.SignInAudience);
        Assert.Equal(new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = expectedTenantId,
            ["AzureAd__AppHomeTenantId"] = TenantId,
            ["AzureAd__ClientId"] = ClientId
        }, env);
    }

    [Fact]
    public async Task WithReference_OtherSignInAudiences_EmitHomeTenantFromParameter()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var tenantId = appBuilder.AddParameter("EntraTenantId", TenantId);
        var clientId = appBuilder.AddParameter("EntraApiClientId", ClientId);

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: tenantId, clientId: clientId)
            .WithSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs);

        var container = appBuilder.AddResource(new ProjectResource("api"))
            .WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = "organizations",
            ["AzureAd__AppHomeTenantId"] = TenantId,
            ["AzureAd__ClientId"] = ClientId
        }, env);
    }

    [Fact]
    public async Task WithSendX5C_EmitsSendX5C()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithSendX5C();

        var container = appBuilder.AddResource(new ProjectResource("api"))
            .WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("true", env["AzureAd__SendX5C"]);
    }

    [Fact]
    public async Task WithCredential_FileCertificatePasswordFlowsThroughParameter()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var password = appBuilder.AddParameter("CertPassword", "p@ssw0rd", secret: true);

        var entra = appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithCredential(new EntraIdFileCertificateCredential
            {
                FilePath = "/certs/app.pfx",
                Password = password.Resource
            });

        var container = appBuilder.AddResource(new ProjectResource("web"))
            .WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("Path", env["AzureAd__ClientCredentials__0__SourceType"]);
        Assert.Equal("/certs/app.pfx", env["AzureAd__ClientCredentials__0__CertificateDiskPath"]);
        Assert.Equal("p@ssw0rd", env["AzureAd__ClientCredentials__0__CertificatePassword"]);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, "AzureAdApi", "AZURE_AD_API")]
    [InlineData(DistributedApplicationOperation.Publish, "AzureAdApi", "AZURE_AD_API")]
    [InlineData(DistributedApplicationOperation.Run, "Authentication__Schemes__AzureAd", "AUTHENTICATION__SCHEMES__AZURE_AD")]
    [InlineData(DistributedApplicationOperation.Publish, "Authentication__Schemes__AzureAd", "AUTHENTICATION__SCHEMES__AZURE_AD")]
    [InlineData(DistributedApplicationOperation.Run, "my-auth", "MY_AUTH")]
    [InlineData(DistributedApplicationOperation.Publish, "my-auth", "MY_AUTH")]
    [InlineData(DistributedApplicationOperation.Run, "Authentication:Entra", "AUTHENTICATION_ENTRA")]
    [InlineData(DistributedApplicationOperation.Publish, "Authentication:Entra", "AUTHENTICATION_ENTRA")]
    public async Task WithReference_UsesCustomConnectionName(DistributedApplicationOperation operation, string connectionName, string expectedPrefix)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        var container = appBuilder.AddContainer("api", "myimage")
            .WithReference(entra, connectionName: connectionName);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, operation, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            [$"{expectedPrefix}_INSTANCE"] = "https://login.microsoftonline.com/",
            [$"{expectedPrefix}_TENANT_ID"] = TenantId,
            [$"{expectedPrefix}_CLIENT_ID"] = ClientId
        }, env);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithReference_DotnetProgramPrefersMicrosoftIdentityWebDefaults(DistributedApplicationOperation operation)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);
        var project = appBuilder.AddResource(new ProjectResource("api"));

        IResourceBuilder<ProjectResource> result = project.WithReference(entra);
        Assert.Same(project, result);
        var explicitNull = appBuilder.AddResource(new ProjectResource("explicit-null"))
            .WithReference(entra, connectionName: null);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource, operation, TestServiceProvider.Instance);

        var expected = new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = TenantId,
            ["AzureAd__ClientId"] = ClientId
        };

        Assert.Equal(expected, env);
        Assert.Equal(expected, await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            explicitNull.Resource, operation, TestServiceProvider.Instance));
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, "AzureAdApi", "AzureAdApi")]
    [InlineData(DistributedApplicationOperation.Publish, "AzureAdApi", "AzureAdApi")]
    [InlineData(DistributedApplicationOperation.Run, "Authentication:AzureAd", "Authentication__AzureAd")]
    [InlineData(DistributedApplicationOperation.Publish, "Authentication:AzureAd", "Authentication__AzureAd")]
    [InlineData(DistributedApplicationOperation.Run, "Authentication:Providers:Entra", "Authentication__Providers__Entra")]
    [InlineData(DistributedApplicationOperation.Publish, "Authentication:Providers:Entra", "Authentication__Providers__Entra")]
    public async Task WithReference_DotnetProgramUsesCustomConfigSection(DistributedApplicationOperation operation, string connectionName, string expectedPrefix)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);
        var named = appBuilder.AddResource(new ProjectResource("named"))
            .WithReference(entra, connectionName: connectionName);
        var positional = appBuilder.AddResource(new ProjectResource("positional"))
            .WithReference(entra, connectionName);

        var expected = new Dictionary<string, string>
        {
            [$"{expectedPrefix}__Instance"] = "https://login.microsoftonline.com/",
            [$"{expectedPrefix}__TenantId"] = TenantId,
            [$"{expectedPrefix}__ClientId"] = ClientId
        };

        Assert.Equal(expected, await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            named.Resource, operation, TestServiceProvider.Instance));
        Assert.Equal(expected, await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            positional.Resource, operation, TestServiceProvider.Instance));
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, "AUTH", "AUTH__")]
    [InlineData(DistributedApplicationOperation.Publish, "AUTH", "AUTH__")]
    [InlineData(DistributedApplicationOperation.Run, "", "")]
    [InlineData(DistributedApplicationOperation.Publish, "", "")]
    public async Task WithReference_DotnetProgramUsesCustomConnectionName(DistributedApplicationOperation operation, string connectionName, string expectedPrefix)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);
        var project = appBuilder.AddResource(new ProjectResource("api"))
            .WithReference(entra, connectionName: connectionName);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource, operation, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            [$"{expectedPrefix}Instance"] = "https://login.microsoftonline.com/",
            [$"{expectedPrefix}TenantId"] = TenantId,
            [$"{expectedPrefix}ClientId"] = ClientId
        }, env);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, "entra", "ENTRA")]
    [InlineData(DistributedApplicationOperation.Publish, "entra", "ENTRA")]
    [InlineData(DistributedApplicationOperation.Run, "entra-api", "ENTRA_API")]
    [InlineData(DistributedApplicationOperation.Publish, "entra-api", "ENTRA_API")]
    [InlineData(DistributedApplicationOperation.Run, "myEntraAPI", "MY_ENTRA_API")]
    [InlineData(DistributedApplicationOperation.Publish, "myEntraAPI", "MY_ENTRA_API")]
    public async Task WithReference_OtherResourcesUsePortableResourceNamePrefix(DistributedApplicationOperation operation, string resourceName, string expectedPrefix)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication(resourceName)
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);
        var container = appBuilder.AddContainer("worker", "myimage")
            .WithReference(entra);
        var explicitNull = appBuilder.AddContainer("explicit-null", "myimage")
            .WithReference(entra, connectionName: null);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, operation, TestServiceProvider.Instance);

        var expected = new Dictionary<string, string>
        {
            [$"{expectedPrefix}_INSTANCE"] = "https://login.microsoftonline.com/",
            [$"{expectedPrefix}_TENANT_ID"] = TenantId,
            [$"{expectedPrefix}_CLIENT_ID"] = ClientId
        };

        Assert.Equal(expected, env);
        Assert.Equal(expected, await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            explicitNull.Resource, operation, TestServiceProvider.Instance));
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithReference_UsesActualResourceType(DistributedApplicationOperation operation)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);
        IResourceBuilder<IResourceWithEnvironment> project = appBuilder.AddResource(new ProjectResource("api"));
        project.WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource, operation, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = TenantId,
            ["AzureAd__ClientId"] = ClientId
        }, env);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithReference_ConnectionNamesAreSpecificToEachConsumer(DistributedApplicationOperation operation)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        var defaultConsumer = appBuilder.AddResource(new ProjectResource("api"))
            .WithReference(entra);

        var customConsumer = appBuilder.AddContainer("worker", "myimage");
        Assert.Same(customConsumer, customConsumer.WithReference(entra, connectionName: "ENTRA"));

        var defaultEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            defaultConsumer.Resource, operation, TestServiceProvider.Instance);
        var customEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            customConsumer.Resource, operation, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = TenantId,
            ["AzureAd__ClientId"] = ClientId
        }, defaultEnv);
        Assert.Equal(new Dictionary<string, string>
        {
            ["ENTRA_INSTANCE"] = "https://login.microsoftonline.com/",
            ["ENTRA_TENANT_ID"] = TenantId,
            ["ENTRA_CLIENT_ID"] = ClientId
        }, customEnv);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, "__")]
    [InlineData(DistributedApplicationOperation.Publish, "__")]
    [InlineData(DistributedApplicationOperation.Run, "_")]
    [InlineData(DistributedApplicationOperation.Publish, "_")]
    public async Task WithReference_UsesResourceSpecificSeparatorThroughoutAllSettings(DistributedApplicationOperation operation, string separator)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var secret = appBuilder.AddParameter("EntraSecret", "super-secret", secret: true);
        var password = appBuilder.AddParameter("CertPassword", "certificate-password", secret: true);
        var tenantId = appBuilder.AddParameter("EntraTenantId", TenantId);
        var clientId = appBuilder.AddParameter("EntraClientId", ClientId);

        var entra = appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId, clientId)
            .WithSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs)
            .WithClientSecret(secret)
            .WithCredential(new EntraIdFederatedIdentityCredential
            {
                ManagedIdentityClientId = ClientId,
                TokenExchangeUrl = "api://CustomTokenExchange",
                TokenExchangeAuthority = "https://login.microsoftonline.com/"
            })
            .WithCertificateFromKeyVault("https://myvault.vault.azure.net", "MyCert")
            .WithCertificateThumbprint("CurrentUser/My", "ABC123")
            .WithCertificateDistinguishedName("CurrentUser/My", "CN=MyCert")
            .WithCredential(new EntraIdFileCertificateCredential
            {
                FilePath = "/certs/app.pfx",
                Password = password.Resource
            })
            .WithCredential(new EntraIdSignedAssertionFileCredential { FilePath = "/var/run/secrets/token" })
            .WithSendX5C()
            .WithClientCapability("cp1")
            .WithClientCapability("cp2")
            .WithAudience($"api://{ClientId}")
            .WithAudience("api://another-api")
            .WithAllowWebApiToBeAuthorizedByACL()
            .WithExtraQueryParameter("dc", "prod-wst-01")
            .WithExtraQueryParameter("loginHint", "UPN@Contoso.com");

        IResourceBuilder<IResourceWithEnvironment> container = separator == "__"
            ? appBuilder.AddResource(new ProjectResource("web"))
            : appBuilder.AddContainer("web", "myimage");
        container.WithReference(entra, connectionName: "ENTRA");
        IResourceBuilder<IResourceWithEnvironment> unprefixed = separator == "__"
            ? appBuilder.AddResource(new ProjectResource("unprefixed"))
            : appBuilder.AddContainer("unprefixed", "myimage");
        unprefixed.WithReference(entra, connectionName: "");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, operation, TestServiceProvider.Instance);

        var expected = new Dictionary<string, string>
        {
            [$"ENTRA{separator}Instance"] = "https://login.microsoftonline.com/",
            [$"ENTRA{separator}TenantId"] = "organizations",
            [$"ENTRA{separator}AppHomeTenantId"] = operation == DistributedApplicationOperation.Run ? TenantId : "{EntraTenantId.value}",
            [$"ENTRA{separator}ClientId"] = operation == DistributedApplicationOperation.Run ? ClientId : "{EntraClientId.value}",
            [$"ENTRA{separator}SendX5C"] = "true",
            [$"ENTRA{separator}ClientCredentials{separator}0{separator}SourceType"] = "ClientSecret",
            [$"ENTRA{separator}ClientCredentials{separator}0{separator}ClientSecret"] = operation == DistributedApplicationOperation.Run ? "super-secret" : "{EntraSecret.value}",
            [$"ENTRA{separator}ClientCredentials{separator}1{separator}SourceType"] = "SignedAssertionFromManagedIdentity",
            [$"ENTRA{separator}ClientCredentials{separator}1{separator}ManagedIdentityClientId"] = ClientId,
            [$"ENTRA{separator}ClientCredentials{separator}1{separator}TokenExchangeUrl"] = "api://CustomTokenExchange",
            [$"ENTRA{separator}ClientCredentials{separator}1{separator}TokenExchangeAuthority"] = "https://login.microsoftonline.com/",
            [$"ENTRA{separator}ClientCredentials{separator}2{separator}SourceType"] = "KeyVault",
            [$"ENTRA{separator}ClientCredentials{separator}2{separator}KeyVaultUrl"] = "https://myvault.vault.azure.net",
            [$"ENTRA{separator}ClientCredentials{separator}2{separator}KeyVaultCertificateName"] = "MyCert",
            [$"ENTRA{separator}ClientCredentials{separator}3{separator}SourceType"] = "StoreWithThumbprint",
            [$"ENTRA{separator}ClientCredentials{separator}3{separator}CertificateStorePath"] = "CurrentUser/My",
            [$"ENTRA{separator}ClientCredentials{separator}3{separator}CertificateThumbprint"] = "ABC123",
            [$"ENTRA{separator}ClientCredentials{separator}4{separator}SourceType"] = "StoreWithDistinguishedName",
            [$"ENTRA{separator}ClientCredentials{separator}4{separator}CertificateStorePath"] = "CurrentUser/My",
            [$"ENTRA{separator}ClientCredentials{separator}4{separator}CertificateDistinguishedName"] = "CN=MyCert",
            [$"ENTRA{separator}ClientCredentials{separator}5{separator}SourceType"] = "Path",
            [$"ENTRA{separator}ClientCredentials{separator}5{separator}CertificateDiskPath"] = "/certs/app.pfx",
            [$"ENTRA{separator}ClientCredentials{separator}5{separator}CertificatePassword"] = operation == DistributedApplicationOperation.Run ? "certificate-password" : "{CertPassword.value}",
            [$"ENTRA{separator}ClientCredentials{separator}6{separator}SourceType"] = "SignedAssertionFilePath",
            [$"ENTRA{separator}ClientCredentials{separator}6{separator}SignedAssertionFileDiskPath"] = "/var/run/secrets/token",
            [$"ENTRA{separator}ClientCapabilities{separator}0"] = "cp1",
            [$"ENTRA{separator}ClientCapabilities{separator}1"] = "cp2",
            [$"ENTRA{separator}Audiences{separator}0"] = $"api://{ClientId}",
            [$"ENTRA{separator}Audiences{separator}1"] = "api://another-api",
            [$"ENTRA{separator}AllowWebApiToBeAuthorizedByACL"] = "true",
            [$"ENTRA{separator}ExtraQueryParameters{separator}dc"] = "prod-wst-01",
            [$"ENTRA{separator}ExtraQueryParameters{separator}loginHint"] = "UPN@Contoso.com"
        };

        if (separator == "_")
        {
            expected = new Dictionary<string, string>
            {
                ["ENTRA_INSTANCE"] = "https://login.microsoftonline.com/",
                ["ENTRA_TENANT_ID"] = "organizations",
                ["ENTRA_APP_HOME_TENANT_ID"] = operation == DistributedApplicationOperation.Run ? TenantId : "{EntraTenantId.value}",
                ["ENTRA_CLIENT_ID"] = operation == DistributedApplicationOperation.Run ? ClientId : "{EntraClientId.value}",
                ["ENTRA_SEND_X5_C"] = "true",
                ["ENTRA_AZURE_REGION"] = "westus2",
                ["ENTRA_CLIENT_CREDENTIALS_0_SOURCE_TYPE"] = "ClientSecret",
                ["ENTRA_CLIENT_CREDENTIALS_0_CLIENT_SECRET"] = operation == DistributedApplicationOperation.Run ? "super-secret" : "{EntraSecret.value}",
                ["ENTRA_CLIENT_CREDENTIALS_1_SOURCE_TYPE"] = "SignedAssertionFromManagedIdentity",
                ["ENTRA_CLIENT_CREDENTIALS_1_MANAGED_IDENTITY_CLIENT_ID"] = ClientId,
                ["ENTRA_CLIENT_CREDENTIALS_1_TOKEN_EXCHANGE_URL"] = "api://CustomTokenExchange",
                ["ENTRA_CLIENT_CREDENTIALS_1_TOKEN_EXCHANGE_AUTHORITY"] = "https://login.microsoftonline.com/",
                ["ENTRA_CLIENT_CREDENTIALS_2_SOURCE_TYPE"] = "KeyVault",
                ["ENTRA_CLIENT_CREDENTIALS_2_KEY_VAULT_URL"] = "https://myvault.vault.azure.net",
                ["ENTRA_CLIENT_CREDENTIALS_2_KEY_VAULT_CERTIFICATE_NAME"] = "MyCert",
                ["ENTRA_CLIENT_CREDENTIALS_3_SOURCE_TYPE"] = "StoreWithThumbprint",
                ["ENTRA_CLIENT_CREDENTIALS_3_CERTIFICATE_STORE_PATH"] = "CurrentUser/My",
                ["ENTRA_CLIENT_CREDENTIALS_3_CERTIFICATE_THUMBPRINT"] = "ABC123",
                ["ENTRA_CLIENT_CREDENTIALS_4_SOURCE_TYPE"] = "StoreWithDistinguishedName",
                ["ENTRA_CLIENT_CREDENTIALS_4_CERTIFICATE_STORE_PATH"] = "CurrentUser/My",
                ["ENTRA_CLIENT_CREDENTIALS_4_CERTIFICATE_DISTINGUISHED_NAME"] = "CN=MyCert",
                ["ENTRA_CLIENT_CREDENTIALS_5_SOURCE_TYPE"] = "Path",
                ["ENTRA_CLIENT_CREDENTIALS_5_CERTIFICATE_DISK_PATH"] = "/certs/app.pfx",
                ["ENTRA_CLIENT_CREDENTIALS_5_CERTIFICATE_PASSWORD"] = operation == DistributedApplicationOperation.Run ? "certificate-password" : "{CertPassword.value}",
                ["ENTRA_CLIENT_CREDENTIALS_6_SOURCE_TYPE"] = "SignedAssertionFilePath",
                ["ENTRA_CLIENT_CREDENTIALS_6_SIGNED_ASSERTION_FILE_DISK_PATH"] = "/var/run/secrets/token",
                ["ENTRA_CLIENT_CAPABILITIES_0"] = "cp1",
                ["ENTRA_CLIENT_CAPABILITIES_1"] = "cp2",
                ["ENTRA_AUDIENCES_0"] = $"api://{ClientId}",
                ["ENTRA_AUDIENCES_1"] = "api://another-api",
                ["ENTRA_ALLOW_WEB_API_TO_BE_AUTHORIZED_BY_ACL"] = "true",
                ["ENTRA_EXTRA_QUERY_PARAMETERS_DC"] = "prod-wst-01",
                ["ENTRA_EXTRA_QUERY_PARAMETERS_LOGIN_HINT"] = "UPN@Contoso.com"
            };
        }

        Assert.Equal(expected, env);
        var prefixLength = "ENTRA".Length + separator.Length;
        Assert.Equal(expected.ToDictionary(kvp => kvp.Key[prefixLength..], kvp => kvp.Value),
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                unprefixed.Resource, operation, TestServiceProvider.Instance));
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithReference_ContainersUseSingleUnderscore(DistributedApplicationOperation operation)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);
        var container = appBuilder.AddContainer("api", "mcr.microsoft.com/dotnet/aspnet")
            .WithReference(entra, connectionName: "AzureAd");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, operation, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["AZURE_AD_INSTANCE"] = "https://login.microsoftonline.com/",
            ["AZURE_AD_TENANT_ID"] = TenantId,
            ["AZURE_AD_CLIENT_ID"] = ClientId
        }, env);
    }

    [Fact]
    public async Task WithReference_EmitsCertificateStoreCredential()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithCertificateThumbprint("CurrentUser/My", "ABC123");

        var container = appBuilder.AddResource(new ProjectResource("web"))
            .WithReference(entra, connectionName: "AzureAd");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__TenantId"] = TenantId,
            ["AzureAd__ClientId"] = ClientId,
            ["AzureAd__ClientCredentials__0__SourceType"] = "StoreWithThumbprint",
            ["AzureAd__ClientCredentials__0__CertificateStorePath"] = "CurrentUser/My",
            ["AzureAd__ClientCredentials__0__CertificateThumbprint"] = "ABC123"
        }, env);
    }

    [Fact]
    public void AddEntraIdApplication_WithCertificateThumbprint()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithCertificateThumbprint("CurrentUser/My", "ABC123");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.ClientCredentials);
        var cred = Assert.IsType<EntraIdStoreCertificateCredential>(resource.ClientCredentials[0]);
        Assert.Equal("StoreWithThumbprint", cred.SourceType);
        Assert.Equal("CurrentUser/My", cred.StorePath);
        Assert.Equal("ABC123", cred.Thumbprint);
        Assert.Null(cred.DistinguishedName);
    }

    [Fact]
    public void AddEntraIdApplication_WithCertificateDistinguishedName()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithCertificateDistinguishedName("CurrentUser/My", "CN=MyCert");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.ClientCredentials);
        var cred = Assert.IsType<EntraIdStoreCertificateCredential>(resource.ClientCredentials[0]);
        Assert.Equal("StoreWithDistinguishedName", cred.SourceType);
        Assert.Equal("CurrentUser/My", cred.StorePath);
        Assert.Equal("CN=MyCert", cred.DistinguishedName);
    }

    [Fact]
    public void AddEntraIdApplication_WithRawCredential()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithCredential(new EntraIdSignedAssertionFileCredential
            {
                FilePath = "/var/run/secrets/token"
            });

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.ClientCredentials);
        var cred = Assert.IsType<EntraIdSignedAssertionFileCredential>(resource.ClientCredentials[0]);
        Assert.Equal("SignedAssertionFilePath", cred.SourceType);
        Assert.Equal("/var/run/secrets/token", cred.FilePath);
    }

    [Fact]
    public void WithReference_CreatesReferenceRelationship()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        var project = appBuilder.AddContainer("api", "myimage")
            .WithReference(entra);

        var relationships = project.Resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .ToList();

        Assert.Contains(relationships, r => r.Resource == entra.Resource);
    }

    [Fact]
    public void AddEntraIdApplication_ThrowsWhenNameNull()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentNullException>(() =>
            appBuilder.AddEntraIdApplication(null!));
    }

    [Fact]
    public void AddEntraIdApplication_ThrowsWhenNameEmpty()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentException>(() =>
            appBuilder.AddEntraIdApplication(string.Empty));
    }

    [Fact]
    public void EntraIdStoreCertificateCredential_ThrowsWhenNeitherThumbprintNorDN()
    {
        var credential = new EntraIdStoreCertificateCredential
        {
            StorePath = "CurrentUser/My"
        };

        Assert.Throws<InvalidOperationException>(() => _ = credential.SourceType);
    }

    [Fact]
    public void EntraIdStoreCertificateCredential_ThrowsWhenBothThumbprintAndDN()
    {
        var credential = new EntraIdStoreCertificateCredential
        {
            StorePath = "CurrentUser/My",
            Thumbprint = "ABC123",
            DistinguishedName = "CN=MyCert"
        };

        Assert.Throws<InvalidOperationException>(() => _ = credential.SourceType);
    }

    [Fact]
    public void WithClientSecret_ThrowsWhenParameterIsNotSecret()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var notSecret = appBuilder.AddParameter("EntraWebClientSecret");

        var entra = appBuilder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        Assert.Throws<ArgumentException>(() => entra.WithClientSecret(notSecret));
    }

    [Fact]
    public void WithCredential_ThrowsWhenClientSecretParameterIsNotSecret()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var notSecret = appBuilder.AddParameter("EntraSecret");
        var entra = appBuilder.AddEntraIdApplication("entra-web");

        var exception = Assert.Throws<ArgumentException>(() => entra.WithCredential(new EntraIdClientSecretCredential
        {
            ClientSecret = notSecret.Resource
        }));

        Assert.Equal(nameof(EntraIdClientSecretCredential.ClientSecret), exception.ParamName);
        Assert.Empty(entra.Resource.ClientCredentials);
    }

    [Fact]
    public void WithCredential_ThrowsWhenCertificatePasswordParameterIsNotSecret()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var notSecret = appBuilder.AddParameter("CertPassword");
        var entra = appBuilder.AddEntraIdApplication("entra-web");

        var exception = Assert.Throws<ArgumentException>(() => entra.WithCredential(new EntraIdFileCertificateCredential
        {
            FilePath = "/certs/app.pfx",
            Password = notSecret.Resource
        }));

        Assert.Equal(nameof(EntraIdFileCertificateCredential.Password), exception.ParamName);
        Assert.Empty(entra.Resource.ClientCredentials);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, false)]
    [InlineData(DistributedApplicationOperation.Publish, false)]
    [InlineData(DistributedApplicationOperation.Run, true)]
    [InlineData(DistributedApplicationOperation.Publish, true)]
    public async Task WithCredential_SecretParametersFlowThroughEnvironment(DistributedApplicationOperation operation, bool dotnet)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var secret = appBuilder.AddParameter("EntraSecret", "client-secret", secret: true);
        var password = appBuilder.AddParameter("CertPassword", "certificate-password", secret: true);
        var replacement = appBuilder.AddParameter("ReplacementPassword", "replacement-password", secret: true);
        var notSecret = appBuilder.AddParameter("NotSecret");
        var certificate = new EntraIdFileCertificateCredential
        {
            FilePath = "/certs/app.pfx",
            Password = password.Resource
        };
        var entra = appBuilder.AddEntraIdApplication("entra-web")
            .WithCredential(new EntraIdClientSecretCredential { ClientSecret = secret.Resource })
            .WithCredential(certificate);

        Assert.Same(password.Resource, certificate.Password);
        certificate.Password = null;
        Assert.Null(certificate.Password);
        certificate.Password = replacement.Resource;

        IResourceBuilder<IResourceWithEnvironment> consumer = dotnet
            ? appBuilder.AddResource(new ProjectResource("web"))
            : appBuilder.AddContainer("web", "myimage");
        consumer.WithReference(entra);
        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            consumer.Resource, operation, TestServiceProvider.Instance);

        var expectedSecret = operation == DistributedApplicationOperation.Run ? "client-secret" : "{EntraSecret.value}";
        var expectedPassword = operation == DistributedApplicationOperation.Run ? "replacement-password" : "{ReplacementPassword.value}";
        Assert.Equal(dotnet ? new Dictionary<string, string>
        {
            ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd__ClientCredentials__0__SourceType"] = "ClientSecret",
            ["AzureAd__ClientCredentials__0__ClientSecret"] = expectedSecret,
            ["AzureAd__ClientCredentials__1__SourceType"] = "Path",
            ["AzureAd__ClientCredentials__1__CertificateDiskPath"] = "/certs/app.pfx",
            ["AzureAd__ClientCredentials__1__CertificatePassword"] = expectedPassword
        } : new Dictionary<string, string>
        {
            ["ENTRA_WEB_INSTANCE"] = "https://login.microsoftonline.com/",
            ["ENTRA_WEB_CLIENT_CREDENTIALS_0_SOURCE_TYPE"] = "ClientSecret",
            ["ENTRA_WEB_CLIENT_CREDENTIALS_0_CLIENT_SECRET"] = expectedSecret,
            ["ENTRA_WEB_CLIENT_CREDENTIALS_1_SOURCE_TYPE"] = "Path",
            ["ENTRA_WEB_CLIENT_CREDENTIALS_1_CERTIFICATE_DISK_PATH"] = "/certs/app.pfx",
            ["ENTRA_WEB_CLIENT_CREDENTIALS_1_CERTIFICATE_PASSWORD"] = expectedPassword
        }, env);
    }

    [Fact]
    public void EntraIdApplicationResource_CreatesResource()
    {
        var resource = new EntraIdApplicationResource("entra-api");

        Assert.Equal("entra-api", resource.Name);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithReference_EmptyConnectionNameOmitsLeadingDefaultSeparator(DistributedApplicationOperation operation)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);
        var container = appBuilder.AddContainer("api", "myimage")
            .WithReference(entra, connectionName: "");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, operation, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["INSTANCE"] = "https://login.microsoftonline.com/",
            ["TENANT_ID"] = TenantId,
            ["CLIENT_ID"] = ClientId
        }, env);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithReference_NonDotnetNamingPreservesUnrelatedEnvironmentVariables(DistributedApplicationOperation operation)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);
        var container = appBuilder.AddContainer("api", "myimage")
            .WithEnvironment("existingSetting", "KeepThisCase")
            .WithReference(entra)
            .WithEnvironment("anotherSetting", "AlsoKeepThisCase");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, operation, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["existingSetting"] = "KeepThisCase",
            ["ENTRA_API_INSTANCE"] = "https://login.microsoftonline.com/",
            ["ENTRA_API_TENANT_ID"] = TenantId,
            ["ENTRA_API_CLIENT_ID"] = ClientId,
            ["anotherSetting"] = "AlsoKeepThisCase"
        }, env);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithReference_NonDotnetNamingRejectsCollidingKeys(DistributedApplicationOperation operation)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var entra = appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithExtraQueryParameter("loginHint", "first")
            .WithExtraQueryParameter("login_hint", "second");
        var container = appBuilder.AddContainer("api", "myimage")
            .WithReference(entra);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                container.Resource, operation, TestServiceProvider.Instance);
        });
    }

    [Fact]
    public void WithReference_ThrowsWhenSourceIsNull()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var container = appBuilder.AddContainer("api", "myimage");

        Assert.Throws<ArgumentNullException>(() =>
            container.WithReference(source: null!, connectionName: "AzureAd"));
    }

    [Fact]
    public void WithReference_ThrowsWhenBuilderIsNull()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();
        var entra = appBuilder.AddEntraIdApplication("entra-api");

        Assert.Throws<ArgumentNullException>(() =>
            EntraIdResourceExtensions.WithReference<ContainerResource>(null!, entra));
    }

    [Fact]
    public void AddEntraIdApplication_DoesNotImplementIResourceWithConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.IsNotAssignableFrom<IResourceWithConnectionString>(resource);
    }
}
