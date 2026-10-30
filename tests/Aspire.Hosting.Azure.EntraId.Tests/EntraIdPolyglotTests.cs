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
    private const string CertificateCapabilityId = "Aspire.Hosting.Azure.EntraId/runWithLocalDeveloperCertificateCredential";

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

    [Theory]
    [InlineData(false, null, "ENTRA_INSTANCE", "ENTRA_TENANT_ID", "ENTRA_CLIENT_ID")]
    [InlineData(false, "Identity", "IDENTITY_INSTANCE", "IDENTITY_TENANT_ID", "IDENTITY_CLIENT_ID")]
    [InlineData(true, null, "AzureAd__Instance", "AzureAd__TenantId", "AzureAd__ClientId")]
    [InlineData(true, "Identity", "Identity__Instance", "Identity__TenantId", "Identity__ClientId")]
    public async Task GenericWithReference_UsesTheConsumersConfigurationFormat(bool dotnet, string? connectionName, string instanceKey, string tenantKey, string clientKey)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddExistingEntraApplicationForExport("entra", "contoso.onmicrosoft.com", ClientId);
        IResource consumer;
        if (dotnet)
        {
            var project = builder.AddResource(new ProjectResource("web"));
            DispatchReference(project, entra, connectionName);
            consumer = project.Resource;
        }
        else
        {
            var container = builder.AddContainer("web", "image");
            DispatchReference(container, entra, connectionName);
            consumer = container.Resource;
        }

        Assert.Same(consumer, Assert.Single(entra.Resource.Consumers));
        Assert.Equal(new Dictionary<string, string>
        {
            [instanceKey] = "https://login.microsoftonline.com/",
            [tenantKey] = "contoso.onmicrosoft.com",
            [clientKey] = ClientId
        }, await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            consumer, DistributedApplicationOperation.Run, TestServiceProvider.Instance));
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
    public void LocalDeveloperCertificate_ExportMatchesTheCSharpMethod(DistributedApplicationOperation operation)
    {
        var exports = ScanExports();
        var capability = Assert.Single(exports.Capabilities, c => c.CapabilityId == CertificateCapabilityId);
        Assert.Equal("runWithLocalDeveloperCertificateCredential", capability.MethodName);
        var method = exports.Methods[CertificateCapabilityId];

        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var entra = builder.AddEntraIdApplication("entra-web");
        Assert.Same(entra, method.Invoke(null, [entra]));
        Assert.Same(entra, method.Invoke(null, [entra]));

        if (operation == DistributedApplicationOperation.Run)
        {
            Assert.IsType<EntraIdLocalDeveloperCertificateCredential>(Assert.Single(entra.Resource.ClientCredentials));
        }
        else
        {
            Assert.Empty(entra.Resource.ClientCredentials);
        }
    }

    [Fact]
    public void LocalDeveloperCertificate_ExportRejectsExistingApplicationsWithoutMutation()
    {
        var method = ScanExports().Methods[CertificateCapabilityId];
        using var builder = TestDistributedApplicationBuilder.Create();
        var existing = builder.AddExistingEntraApplicationForExport("existing", TenantId, ClientId);

        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [existing]));

        Assert.IsType<InvalidOperationException>(exception.GetBaseException());
        Assert.Empty(existing.Resource.ClientCredentials);
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
    public void AppRoles_ExportMatchesCSharpDeclarationAndValidatesInputs()
    {
        var roleId = new Guid("11112222-aaaa-3333-bbbb-4444cccc5555");
        using var exported = TestDistributedApplicationBuilder.Create();
        var exportedEntra = exported.AddEntraIdApplication("entra-api")
            .WithAppRoleForExport("User", "User", "Can use the API.")
            .WithAppRoleForExport("Admin", "Administrator", "Can administer the API.", ["User", "Application"])
            .WithAppRoleForExport("Reader", "Reader", "Can read data.", ["Application"], roleId.ToString())
            .WithAppRoleForExport("Retired", "Retired", "Being removed.", isEnabled: false);

        using var native = TestDistributedApplicationBuilder.Create();
        var nativeEntra = native.AddEntraIdApplication("entra-api")
            .WithAppRole(new EntraIdAppRole("User", "User", "Can use the API."))
            .WithAppRole(new EntraIdAppRole("Admin", "Administrator", "Can administer the API.", ["User", "Application"]))
            .WithAppRole(new EntraIdAppRole("Reader", "Reader", "Can read data.", ["Application"], roleId))
            .WithAppRole(new EntraIdAppRole("Retired", "Retired", "Being removed.", isEnabled: false));

        Assert.Equal(nativeEntra.Resource.GetBicepTemplateString(), exportedEntra.Resource.GetBicepTemplateString());

        Assert.Throws<ArgumentException>(() => exportedEntra.WithAppRoleForExport("Other", "Other", "Other role.", id: "not-a-guid"));
        Assert.Throws<ArgumentException>(() => exportedEntra.WithAppRoleForExport("User", "Duplicate", "Duplicate value."));
        Assert.Throws<ArgumentException>(() => exportedEntra.WithAppRoleForExport("Other", "Other", "Other role.", []));
        Assert.Equal(4, exportedEntra.Resource.AppRoles.Count);

        var existing = exported.AddExistingEntraApplicationForExport("existing", TenantId, ClientId);
        Assert.Throws<InvalidOperationException>(() => existing.WithAppRoleForExport("Admin", "Admin", "Admin role."));
    }

    [Fact]
    public void ApiExports_MatchTheCSharpModel()
    {
        var scopeId = new Guid("11112222-aaaa-3333-bbbb-4444cccc5555");
        var permissionId = new Guid("22223333-aaaa-4444-bbbb-5555cccc6666");
        var customApi = new EntraIdCustomApi
        {
            DisplayName = "Contoso API",
            ApplicationId = "99998888-aaaa-7777-bbbb-6666cccc5555",
            ResourceUri = "api://contoso",
            BaseUrl = "https://contoso.example"
        };

        using var exported = TestDistributedApplicationBuilder.Create();
        var exportedApi = exported.AddEntraIdApplication("entra-api")
            .WithApplicationIdUriForExport()
            .WithScopeForExport("access_as_user", "Allows the app to call the API.")
            .WithScopeForExport("admin", "Administer the API.", new EntraIdScopeOptions
            {
                DisplayName = "Administer",
                UserConsentDisplayName = "Administer for you",
                UserConsentDescription = "Administer on your behalf.",
                RequiresAdminConsent = true,
                IsEnabled = false,
                Id = scopeId.ToString()
            });
        var exportedClient = exported.AddEntraIdApplication("entra-client");
        var exportedWeb = exported.AddEntraIdApplication("entra-web")
            .WithApiPermissions(exportedApi, "access_as_user")
            .WithExternalApiPermissionsForExport(EntraIdWellKnownApi.MicrosoftGraph,
            [
                new EntraIdExternalApiPermission { Name = "User.Read" },
                new EntraIdExternalApiPermission { Name = "User.Read.All", Type = EntraIdPermissionType.Application }
            ])
            .WithExternalApiPermissionsForExport(customApi,
            [
                new EntraIdExternalApiPermission { Name = "items.read", Id = permissionId.ToString() },
                new EntraIdExternalApiPermission { Name = "items.write", Type = EntraIdPermissionType.Application, Id = permissionId.ToString() }
            ])
            .WithAdminConsent(exportedApi, "access_as_user")
            .WithExternalApiAdminConsentForExport(EntraIdWellKnownApi.MicrosoftGraph,
            [
                new EntraIdExternalApiPermission { Name = "User.Read" },
                new EntraIdExternalApiPermission { Name = "User.Read.All", Type = EntraIdPermissionType.Application }
            ]);
        exportedApi.WithPreAuthorizedApplication(exportedClient, "access_as_user");

        using var native = TestDistributedApplicationBuilder.Create();
        var nativeApi = native.AddEntraIdApplication("entra-api")
            .WithApplicationIdUri()
            .WithScope("access_as_user", "Allows the app to call the API.")
            .WithScope("admin", "Administer the API.", scope =>
            {
                scope.DisplayName = "Administer";
                scope.UserConsentDisplayName = "Administer for you";
                scope.UserConsentDescription = "Administer on your behalf.";
                scope.RequiresAdminConsent = true;
                scope.IsEnabled = false;
                scope.Id = scopeId;
            });
        var nativeClient = native.AddEntraIdApplication("entra-client");
        var nativeCustomApi = EntraIdApi.FromApplicationId("Contoso API", customApi.ApplicationId, "api://contoso", "https://contoso.example");
        var nativeWeb = native.AddEntraIdApplication("entra-web")
            .WithApiPermissions(nativeApi, "access_as_user")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"), EntraIdApiPermission.Application("User.Read.All"))
            .WithApiPermissions(nativeCustomApi,
                EntraIdApiPermission.Delegated("items.read", permissionId),
                EntraIdApiPermission.Application("items.write", permissionId))
            .WithAdminConsent(nativeApi, "access_as_user")
            .WithAdminConsent(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"), EntraIdApiPermission.Application("User.Read.All"));
        nativeApi.WithPreAuthorizedApplication(nativeClient, "access_as_user");

        Assert.Equal(nativeApi.Resource.GetBicepTemplateString(), exportedApi.Resource.GetBicepTemplateString());
        Assert.Equal(nativeWeb.Resource.GetBicepTemplateString(), exportedWeb.Resource.GetBicepTemplateString());
    }

    [Fact]
    public async Task ApiExports_ExistingRegistrationsFlowToConsumerConfiguration()
    {
        static async Task<Dictionary<string, string>> Evaluate(bool export)
        {
            using var builder = TestDistributedApplicationBuilder.Create();
            var scopeId = new Guid("11112222-aaaa-3333-bbbb-4444cccc5555");
            var api = builder.AddExistingEntraApplicationForExport("entra-api", TenantId, ClientId);
            var web = builder.AddExistingEntraApplicationForExport("entra-web", TenantId, "22223333-aaaa-4444-bbbb-5555cccc6666");
            var consumer = builder.AddContainer("consumer", "image");
            var permission = new EntraIdExternalApiPermission { Name = "User.Read" };

            if (export)
            {
                api.WithApplicationIdUriForExport("api://contoso-orders")
                    .WithScopeForExport("orders.read", "Read orders.", new EntraIdScopeOptions { Id = scopeId.ToString() });
                web.WithApiPermissions(api, "orders.read")
                    .WithExternalApiPermissionsForExport(EntraIdWellKnownApi.MicrosoftGraph, [permission]);
                consumer.WithEntraIdReferenceForExport(web, "AUTH", "Apis");
            }
            else
            {
                api.WithApplicationIdUri("api://contoso-orders")
                    .WithScope("orders.read", "Read orders.", scope => scope.Id = scopeId);
                web.WithApiPermissions(api, "orders.read")
                    .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"));
                consumer.WithReference(web, "AUTH", "Apis");
            }

            return await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        }

        var exported = await Evaluate(export: true);
        Assert.Equal(await Evaluate(export: false), exported);
        Assert.Contains(exported, e => e.Value == "api://contoso-orders/orders.read");
        Assert.Contains(exported, e => e.Value == "https://graph.microsoft.com/User.Read");
        Assert.DoesNotContain(exported.Keys, k => k.Contains("DOWNSTREAM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApiExports_GetScopeUriReturnsTheDeferredScopeName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddExistingEntraApplicationForExport("entra-api", TenantId, ClientId)
            .WithApplicationIdUriForExport("api://contoso-orders")
            .WithScopeForExport("orders.read", "Read orders.", new EntraIdScopeOptions { Id = "11112222-aaaa-3333-bbbb-4444cccc5555" });

        Assert.Equal("api://contoso-orders/orders.read", await api.GetScopeUriForExport("orders.read").GetValueAsync(default));
        Assert.Throws<ArgumentException>(() => api.GetScopeUriForExport("unknown"));
    }

    [Fact]
    public void ApiExports_RejectInvalidInputsWithoutMutation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScopeForExport("access_as_user", "Allows access.");
        var web = builder.AddEntraIdApplication("entra-web");
        var permission = new EntraIdExternalApiPermission { Name = "User.Read" };

        Assert.Throws<ArgumentException>(() => api.WithScopeForExport("other", "Other.", new EntraIdScopeOptions { Id = "not-a-guid" }));
        Assert.Throws<ArgumentException>(() => api.WithScopeForExport("access_as_user", "Duplicate."));
        Assert.Throws<ArgumentException>(() => api.WithApplicationIdUriForExport("not a uri"));
        Assert.Single(api.Resource.Scopes);

        Assert.Throws<ArgumentException>(() => web.WithExternalApiPermissionsForExport(42, [permission]));
        Assert.Throws<ArgumentException>(() => web.WithExternalApiPermissionsForExport(EntraIdWellKnownApi.MicrosoftGraph, []));
        Assert.Throws<ArgumentException>(() => web.WithExternalApiPermissionsForExport(EntraIdWellKnownApi.MicrosoftGraph, [new EntraIdExternalApiPermission { Name = "Unknown.Permission" }]));
        Assert.Throws<ArgumentException>(() => web.WithExternalApiPermissionsForExport(EntraIdWellKnownApi.MicrosoftGraph, [new EntraIdExternalApiPermission { Name = "User.Read", Id = "not-a-guid" }]));
        Assert.Throws<ArgumentException>(() => web.WithExternalApiPermissionsForExport(
            new EntraIdCustomApi { DisplayName = "Contoso", ApplicationId = "not-a-guid", ResourceUri = "api://contoso" }, [permission]));
        Assert.Empty(web.Resource.ApiAccess);

        var undefinedType = new EntraIdExternalApiPermission { Name = "User.Read.All", Type = (EntraIdPermissionType)2 };
        Assert.Throws<ArgumentOutOfRangeException>(() => web.WithExternalApiPermissionsForExport(EntraIdWellKnownApi.MicrosoftGraph, [undefinedType]));
        Assert.Throws<ArgumentOutOfRangeException>(() => web.WithExternalApiAdminConsentForExport(EntraIdWellKnownApi.MicrosoftGraph, [undefinedType]));
        Assert.Empty(web.Resource.ApiAccess);

        Assert.Throws<InvalidOperationException>(() => web.WithExternalApiAdminConsentForExport(EntraIdWellKnownApi.MicrosoftGraph, [permission]));
        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(api, "unknown"));
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

    private static void DispatchReference<T>(IResourceBuilder<T> consumer, IResourceBuilder<EntraIdApplicationResource> source,
        string? connectionName = null, bool optional = false, string? name = null)
        where T : IResourceWithEnvironment
    {
        var method = ScanExports().Methods["Aspire.Hosting/withReference"].MakeGenericMethod(typeof(T));
        Assert.Same(consumer, method.Invoke(null, [consumer, source, connectionName, optional, name]));
    }
}
