// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdResourceBuilderTests
{
    [Fact]
    public void AddEntraIdApplication_CreatesResource()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal("entra-api", resource.Name);
        Assert.Equal("test-tenant-id", resource.TenantId);
        Assert.Equal("test-client-id", resource.ClientId);
    }

    [Fact]
    public void AddEntraIdApplication_DefaultConfigSection()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal("AzureAd", resource.ConfigSectionName);
    }

    [Fact]
    public void AddEntraIdApplication_CustomConfigSection()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api", "AzureAdApi")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal("AzureAdApi", resource.ConfigSectionName);
    }

    [Fact]
    public void AddEntraIdApplication_AsExistingWithTenantIdParameter()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var tenantId = appBuilder.AddParameter("EntraTenantId");
        var clientId = appBuilder.AddParameter("EntraApiClientId");

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExisting(tenantId: tenantId, clientId: clientId);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.NotNull(resource.TenantIdParameter);
        Assert.Equal("EntraTenantId", resource.TenantIdParameter.Name);
    }

    [Fact]
    public void AddEntraIdApplication_AsExistingWithClientIdParameter()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var tenantId = appBuilder.AddParameter("EntraTenantId");
        var clientId = appBuilder.AddParameter("EntraApiClientId");

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExisting(tenantId: tenantId, clientId: clientId);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.NotNull(resource.ClientIdParameter);
        Assert.Equal("EntraApiClientId", resource.ClientIdParameter.Name);
    }

    [Fact]
    public void AddEntraIdApplication_WithClientSecret()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var secret = appBuilder.AddParameter("EntraWebClientSecret", secret: true);

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
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
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal("https://login.microsoftonline.com/", resource.Instance);
    }

    [Fact]
    public void AddEntraIdApplication_WithCustomInstance()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .WithInstance("https://login.microsoftonline.us/")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal("https://login.microsoftonline.us/", resource.Instance);
    }

    [Fact]
    public void AddEntraIdApplication_WithAppHomeTenantId()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
            .WithAppHomeTenantId("home-tenant-id");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal("home-tenant-id", resource.AppHomeTenantId);
    }

    [Fact]
    public void AddEntraIdApplication_WithClientCapability()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
            .WithClientCapability("cp1");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.ClientCapabilities);
        Assert.Contains("cp1", resource.ClientCapabilities);
    }

    [Fact]
    public void AddEntraIdApplication_WithAzureRegion()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
            .WithAzureRegion("TryAutoDetect");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Equal("TryAutoDetect", resource.AzureRegion);
    }

    [Fact]
    public void AddEntraIdApplication_WithACLAuthorization()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
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
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
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
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
            .WithAudience("api://test-client-id");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.Audiences);
        Assert.Contains("api://test-client-id", resource.Audiences);
    }

    [Fact]
    public void AddEntraIdApplication_WithFicMsi()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
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
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
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
    public void AddEntraIdApplication_WithManagedCertificate()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
            .WithManagedCertificate();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.Single(resource.ClientCredentials);
        var cred = Assert.IsType<EntraIdManagedCertificateCredential>(resource.ClientCredentials[0]);
        Assert.Equal("ManagedCertificate", cred.SourceType);
    }

    [Fact]
    public void AddEntraIdApplication_MultipleCredentials()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var secret = appBuilder.AddParameter("EntraSecret", secret: true);

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
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
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
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
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
            .WithClientSecret(secret)
            .WithAudience("api://test-client-id")
            .WithAppHomeTenantId("home-tenant")
            .WithClientCapability("cp1")
            .WithAzureRegion("westus2")
            .WithAllowWebApiToBeAuthorizedByACL()
            .WithExtraQueryParameter("dc", "prod-wst-01");

        var container = appBuilder.AddContainer("api", "myimage")
            .WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("https://login.microsoftonline.com/", env["AzureAd__Instance"]);
        Assert.Equal("test-tenant-id", env["AzureAd__TenantId"]);
        Assert.Equal("test-client-id", env["AzureAd__ClientId"]);
        Assert.Equal("home-tenant", env["AzureAd__AppHomeTenantId"]);
        Assert.Equal("true", env["AzureAd__SendX5C"]);
        Assert.Equal("westus2", env["AzureAd__AzureRegion"]);
        Assert.Equal("ClientSecret", env["AzureAd__ClientCredentials__0__SourceType"]);
        Assert.Equal("super-secret", env["AzureAd__ClientCredentials__0__ClientSecret"]);
        Assert.Equal("cp1", env["AzureAd__ClientCapabilities__0"]);
        Assert.Equal("api://test-client-id", env["AzureAd__Audiences__0"]);
        Assert.Equal("true", env["AzureAd__AllowWebApiToBeAuthorizedByACL"]);
        Assert.Equal("prod-wst-01", env["AzureAd__ExtraQueryParameters__dc"]);
    }

    [Fact]
    public async Task WithReference_UsesCustomConfigSectionNameAsPrefix()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-api", "AzureAdApi")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id");

        var container = appBuilder.AddContainer("api", "myimage")
            .WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("test-tenant-id", env["AzureAdApi__TenantId"]);
        Assert.Equal("test-client-id", env["AzureAdApi__ClientId"]);
        Assert.DoesNotContain(env, kvp => kvp.Key.StartsWith("AzureAd__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithReference_EmitsCertificateStoreCredential()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var entra = appBuilder.AddEntraIdApplication("entra-web")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
            .WithCertificateThumbprint("CurrentUser/My", "ABC123");

        var container = appBuilder.AddContainer("web", "myimage")
            .WithReference(entra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("StoreWithThumbprint", env["AzureAd__ClientCredentials__0__SourceType"]);
        Assert.Equal("CurrentUser/My", env["AzureAd__ClientCredentials__0__CertificateStorePath"]);
        Assert.Equal("ABC123", env["AzureAd__ClientCredentials__0__CertificateThumbprint"]);
        Assert.DoesNotContain("AzureAd__ClientCredentials__0__CertificateDistinguishedName", env.Keys);
    }

    [Fact]
    public void AddEntraIdApplication_WithCertificateThumbprint()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-web")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
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
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
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
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id")
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
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id");

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
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id");

        Assert.Throws<ArgumentException>(() => entra.WithClientSecret(notSecret));
    }

    [Fact]
    public void EntraIdApplicationResource_ThrowsWhenConfigSectionNameIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new EntraIdApplicationResource("entra-api", string.Empty));
        Assert.Throws<ArgumentNullException>(() => new EntraIdApplicationResource("entra-api", null!));
    }

    [Fact]
    public void AddEntraIdApplication_ThrowsWhenConfigSectionNameIsEmpty()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentException>(() =>
            appBuilder.AddEntraIdApplication("entra-api", string.Empty));
    }

    [Fact]
    public void AddEntraIdApplication_DoesNotImplementIResourceWithConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddEntraIdApplication("entra-api")
            .AsExisting(tenantId: "test-tenant-id", clientId: "test-client-id");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<EntraIdApplicationResource>());
        Assert.IsNotAssignableFrom<IResourceWithConnectionString>(resource);
    }
}
