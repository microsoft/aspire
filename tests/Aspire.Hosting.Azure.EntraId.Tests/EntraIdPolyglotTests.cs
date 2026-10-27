// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREPIPELINES001

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.RemoteHost;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdPolyglotTests
{
    private const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    private const string ClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingApplication_AcceptsIndependentLiteralOrParameterIds(bool tenantParameter, bool clientParameter)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        object tenant = tenantParameter ? builder.AddParameter("tenant", TenantId) : TenantId;
        object client = clientParameter ? builder.AddParameter("client", ClientId) : ClientId;
        var entra = builder.AddExistingEntraApplicationForExport("entra", tenant, client);
        var consumer = builder.AddContainer("consumer", "image");
        DispatchReference(consumer, entra);

        Assert.True(entra.Resource.IsExistingApplication);
        Assert.Empty(builder.Resources.OfType<AzureEnvironmentResource>());
        Assert.Null(entra.Resource.ProvisioningTaskCompletionSource);
        Assert.Null(await ManifestUtils.GetManifestOrNull(entra.Resource));
        Assert.Equal(new Dictionary<string, string>
        {
            ["ENTRA_INSTANCE"] = "https://login.microsoftonline.com/",
            ["ENTRA_TENANT_ID"] = TenantId,
            ["ENTRA_CLIENT_ID"] = ClientId
        }, await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));
    }

    [Fact]
    public async Task AsExistingApplication_RetainsAzureSetupAndReplacesPreviousIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var entra = builder.AddEntraIdApplication("entra")
            .AsExistingApplicationForExport(builder.AddParameter("tenant", TenantId), builder.AddParameter("client", ClientId));
        entra.AsExistingApplicationForExport(TenantId, ClientId);

        Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        Assert.Null(entra.Resource.TenantIdParameter);
        Assert.Null(entra.Resource.ClientIdParameter);
        Assert.Equal(TenantId, entra.Resource.TenantId);
        Assert.Equal(ClientId, entra.Resource.ClientId);
        Assert.Empty(entra.Resource.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Null(await ManifestUtils.GetManifestOrNull(entra.Resource));
    }

    [Theory]
    [InlineData("organizations", ClientId)]
    [InlineData(TenantId, "not-a-guid")]
    public void ExistingApplication_RejectsInvalidLiteralsWithoutAddingResources(string tenant, string client)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resources = builder.Resources.ToArray();
        Assert.Throws<ArgumentException>(() => builder.AddExistingEntraApplicationForExport("entra", tenant, client));
        Assert.Equal(resources, builder.Resources);
    }

    [Fact]
    public void ExistingApplication_RejectsUnsupportedAndNullValuesWithoutMutation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddExistingEntraApplicationForExport("entra", TenantId, ClientId);
        Assert.Throws<ArgumentException>(() => entra.AsExistingApplicationForExport(42, ClientId));
        Assert.Throws<ArgumentNullException>(() => entra.AsExistingApplicationForExport(TenantId, null!));
        Assert.Equal(TenantId, entra.Resource.TenantId);
        Assert.Equal(ClientId, entra.Resource.ClientId);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task Credentials_EmitTypedSettingsAndPreserveSecretReferences(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var secret = builder.AddParameter("secret", "test-secret", secret: true);
        var password = builder.AddParameter("password", "test-password", secret: true);
        var entra = builder.AddExistingEntraApplicationForExport("entra", TenantId, ClientId)
            .WithClientSecret(secret)
            .WithFederatedManagedIdentityCredentialForExport(ClientId, new EntraIdTokenExchangeOptions
            {
                TokenExchangeUrl = "api://exchange",
                TokenExchangeAuthority = "https://login.microsoftonline.com/"
            })
            .WithCertificateFromKeyVault("https://vault.vault.azure.net", "certificate")
            .WithCertificateThumbprint("CurrentUser/My", "thumbprint")
            .WithCertificateDistinguishedName("CurrentUser/My", "CN=certificate")
            .WithCertificateFromFileForExport("/certs/client.pfx", password)
            .WithSignedAssertionFileForExport("/tokens/assertion")
            .WithSignedAssertionFileForExport()
            .WithSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs)
            .WithAudience("api://consumer")
            .WithClientCapability("cp1")
            .WithSendX5C()
            .WithAllowWebApiToBeAuthorizedByACL()
            .WithExtraQueryParameter("domain_hint", "contoso.com");
        var consumer = builder.AddContainer("consumer", "image");
        DispatchReference(consumer, entra, "AUTH");
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            consumer.Resource, operation, TestServiceProvider.Instance);

        Assert.Same(secret.Resource, Assert.IsType<EntraIdClientSecretCredential>(entra.Resource.ClientCredentials[0]).ClientSecret);
        Assert.Same(password.Resource, Assert.IsType<EntraIdFileCertificateCredential>(entra.Resource.ClientCredentials[5]).Password);
        await Verify(environment).UseParameters(operation);
    }

    [Fact]
    public void Credentials_RejectNonSecretParametersAndInvalidIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddExistingEntraApplicationForExport("entra", TenantId, ClientId);
        var notSecret = builder.AddParameter("not-secret");
        Assert.Throws<ArgumentException>(() => entra.WithCertificateFromFileForExport("/cert.pfx", notSecret));
        Assert.Throws<ArgumentException>(() => entra.WithClientSecret(notSecret));
        Assert.Throws<ArgumentException>(() => entra.WithFederatedManagedIdentityCredentialForExport("invalid"));
        Assert.Throws<ArgumentException>(() => entra.WithCertificateFromFileForExport(""));
        Assert.Throws<ArgumentException>(() => entra.WithSignedAssertionFileForExport(""));
        Assert.Empty(entra.Resource.ClientCredentials);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public void PublishFederation_DispatchesAllConsumerComputeAndIdentityForms(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var entra = builder.AddEntraIdApplication("entra");
        var consumer = builder.AddContainer("consumer", "image").WithReference(entra);
        var identity = builder.AddAzureUserAssignedIdentity("identity");

        entra.PublishWithFederatedManagedIdentityCredentialForExport();
        entra.PublishWithFederatedManagedIdentityCredentialForExport(consumer);
        entra.PublishWithFederatedManagedIdentityCredentialForExport(identity);

        if (operation == DistributedApplicationOperation.Run)
        {
            Assert.Empty(entra.Resource.ClientCredentials);
        }
        else
        {
            Assert.Collection(entra.Resource.ClientCredentials,
                c => Assert.True(Assert.IsType<EntraIdFederatedIdentityCredential>(c).UsesConsumerIdentity),
                c => Assert.Same(consumer.Resource, Assert.IsType<EntraIdFederatedIdentityCredential>(c).ComputeTarget),
                c => Assert.Same(identity.Resource, Assert.IsType<EntraIdFederatedIdentityCredential>(c).ManagedIdentity));
        }

        var existing = builder.AddExistingEntraApplicationForExport("existing", TenantId, ClientId);
        Assert.Throws<InvalidOperationException>(() => existing.PublishWithFederatedManagedIdentityCredentialForExport());
        Assert.Throws<InvalidOperationException>(() => existing.PublishWithFederatedManagedIdentityCredentialForExport(consumer));
        Assert.Throws<InvalidOperationException>(() => existing.PublishWithFederatedManagedIdentityCredentialForExport(identity));
        Assert.Throws<ArgumentException>(() => entra.PublishWithFederatedManagedIdentityCredentialForExport("invalid"));
    }

    [Fact]
    public async Task OwnersAndRedirects_PreserveDeferredValuesAndOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var host = builder.AddParameter("host");
        var redirects = builder.AddParameter("redirects");
        var owner = builder.AddParameter("owner");
        var entra = builder.AddEntraIdApplication("entra")
            .WithOwnersForExport(ClientId)
            .WithOwnersForExport(owner)
            .WithApplicationOwnersForExport(TenantId)
            .WithServicePrincipalOwnersForExport(TenantId)
            .WithRedirectUrisForExport("https://localhost/signin")
            .WithRedirectUrisForExport(redirects)
            .WithRedirectUrisForExport(host, ["/signin", "/signout"]);

        Assert.Equal([ClientId, owner.Resource, TenantId], entra.Resource.ApplicationOwners);
        Assert.Equal(entra.Resource.ApplicationOwners, entra.Resource.ServicePrincipalOwners);
        await Verify(entra.Resource.GetBicepTemplateString(), "bicep");
        var expression = Assert.IsType<ReferenceExpression>(entra.Resource.Parameters["redirectUri0"]);
        Assert.Equal("https://{0}/signin", expression.Format);
        Assert.Same(host.Resource, Assert.Single(expression.ValueProviders));
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task Redirects_KeepEndpointValuesDeferred(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var web = builder.AddContainer("web", "image")
            .WithHttpsEndpoint(targetPort: 8080)
            .WithComputeEnvironment(environment);
        var entra = builder.AddEntraIdApplication("entra")
            .WithRedirectUrisForExport(web.GetEndpoint("https"), ["/signin"]);

        Assert.False(web.GetEndpoint("https").IsAllocated);
        await Verify(entra.Resource.GetBicepTemplateString(), "bicep").UseParameters(operation);
        Assert.IsType<ReferenceExpression>(entra.Resource.Parameters["redirectUri0"]);
    }

    [Fact]
    public void RedirectsAndOwners_RejectInvalidUnionInputs()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra");
        var endpoint = builder.AddContainer("web", "image").WithHttpEndpoint(targetPort: 8080).GetEndpoint("http");
        var host = builder.AddParameter("host");
        Assert.Throws<ArgumentException>(() => entra.WithRedirectUrisForExport(endpoint));
        Assert.Throws<ArgumentException>(() => entra.WithRedirectUrisForExport(endpoint, []));
        Assert.Throws<ArgumentException>(() => entra.WithRedirectUrisForExport(endpoint, ["invalid-path"]));
        Assert.Throws<ArgumentException>(() => entra.WithRedirectUrisForExport(host, []));
        Assert.Throws<ArgumentException>(() => entra.WithRedirectUrisForExport("https://localhost", ["/signin"]));
        Assert.Throws<ArgumentException>(() => entra.WithRedirectUrisForExport(42));
        Assert.Throws<ArgumentException>(() => entra.WithOwnersForExport("invalid"));
        Assert.Throws<ArgumentException>(() => entra.WithApplicationOwnersForExport(42));
        Assert.Throws<ArgumentException>(() => entra.WithServicePrincipalOwnersForExport(42));
        Assert.Empty(entra.Resource.RedirectUris);
        Assert.Empty(entra.Resource.RedirectUriParameters);
        Assert.Empty(entra.Resource.ApplicationOwners);
        Assert.Empty(entra.Resource.ServicePrincipalOwners);
    }

    [Fact]
    public void GenericWithReference_RejectsUnsupportedOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddExistingEntraApplicationForExport("entra", TenantId, ClientId);
        var consumer = builder.AddContainer("consumer", "image");
        var optional = Assert.Throws<TargetInvocationException>(() => DispatchReference(consumer, entra, optional: true));
        Assert.IsType<InvalidOperationException>(optional.GetBaseException());
        var named = Assert.Throws<TargetInvocationException>(() => DispatchReference(consumer, entra, name: "service"));
        Assert.IsType<InvalidOperationException>(named.GetBaseException());
        Assert.Empty(entra.Resource.Consumers);
    }

    private static AtsCapabilityScanner.ScanResult ScanExports() => AtsCapabilityScanner.ScanAssemblies(
        [typeof(DistributedApplication).Assembly, typeof(AzureProvisioningResource).Assembly, typeof(EntraIdApplicationResource).Assembly]);

    private static void DispatchReference(IResourceBuilder<ContainerResource> consumer, IResourceBuilder<EntraIdApplicationResource> source,
        string? connectionName = null, bool optional = false, string? name = null)
    {
        var method = ScanExports().Methods["Aspire.Hosting/withReference"].MakeGenericMethod(typeof(ContainerResource));
        Assert.Same(consumer, method.Invoke(null, [consumer, source, connectionName, optional, name]));
    }
}
