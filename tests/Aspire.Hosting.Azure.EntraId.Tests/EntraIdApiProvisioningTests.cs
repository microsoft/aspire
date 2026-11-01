// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Azure environment resources are experimental.
#pragma warning disable ASPIREAZURE003 // Tenant-scoped resource annotations are experimental.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdApiProvisioningTests(ITestOutputHelper outputHelper)
{
    private const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    private const string ClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";
    private const string ApiUri = "api://apiservice-entra";
    private const string ScopeDescription = "Allows the app to call the API on behalf of the signed-in user.";

    [Fact]
    public async Task WithScope_UsesDefaultApplicationIdUriSetBySecondDeclaration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api")
            .WithRedirectUris("https://localhost:7123/signin-oidc")
            .WithScope("access_as_user", ScopeDescription);

        Assert.Null(api.Resource.ApplicationIdUri);
        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task WithApplicationIdUri_Default_ExposesApiWithoutScopes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithApplicationIdUri();

        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task WithApplicationIdUri_Custom_IsWrittenToTheApplicationWithoutASecondDeclaration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api")
            .WithApplicationIdUri("api://contoso-orders")
            .WithScope("orders.read", "Read orders.", scope =>
            {
                scope.DisplayName = "Read orders";
                scope.RequiresAdminConsent = true;
                scope.UserConsentDisplayName = "Read your orders";
                scope.UserConsentDescription = "Allows the app to read your orders.";
            })
            .WithScope("orders.write", "Write orders.", scope => scope.IsEnabled = false);

        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void DefaultProvisioning_DoesNotExposeAnApi()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddEntraIdApplication("entra-api").Resource.GetBicepTemplateString();

        Assert.DoesNotContain("identifierUris", bicep);
        Assert.DoesNotContain("oauth2PermissionScopes", bicep);
        Assert.DoesNotContain("applicationIdUri", bicep);
        Assert.DoesNotContain("requiredResourceAccess", bicep);
        Assert.DoesNotContain("oauth2PermissionGrants", bicep);
    }

    [Fact]
    public void GetBicepTemplateString_IsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"))
            .WithAdminConsent(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"));

        Assert.Equal(api.Resource.GetBicepTemplateString(), api.Resource.GetBicepTemplateString());
        Assert.Equal(web.Resource.GetBicepTemplateString(), web.Resource.GetBicepTemplateString());
    }

    [Fact]
    public void ScopeIds_AreStableAcrossBuildersAndDistinctPerScope()
    {
        static Guid Resolve(string resourceName, string scopeName)
        {
            using var builder = TestDistributedApplicationBuilder.Create();
            var api = builder.AddEntraIdApplication(resourceName).WithScope(scopeName, ScopeDescription);
            return api.Resource.GetScopeId(Assert.Single(api.Resource.Scopes));
        }

        var id = Resolve("entra-api", "access_as_user");

        Assert.Equal(id, Resolve("entra-api", "access_as_user"));
        Assert.NotEqual(id, Resolve("entra-api", "other"));
        Assert.NotEqual(id, Resolve("other-api", "access_as_user"));

        // The ID is a version 8 GUID in the standard variant, as Graph requires GUID-shaped scope IDs.
        var text = id.ToString("D");
        Assert.Equal('8', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    [Fact]
    public void ScopeIds_AreStableAcrossChangedScopeText()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", "Original description.");
        var second = builder.AddEntraIdApplication("entra-api2").WithScope("access_as_user", "Changed description.");
        var id = first.Resource.GetScopeId(first.Resource.Scopes[0]);

        first.Resource.Scopes[0].Description = "A different description.";

        Assert.Equal(id, first.Resource.GetScopeId(first.Resource.Scopes[0]));
        Assert.NotEqual(id, second.Resource.GetScopeId(second.Resource.Scopes[0]));
    }

    [Fact]
    public void WithScope_UsesExplicitId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var id = Guid.Parse("11112222-aaaa-3333-bbbb-4444cccc5555");
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription, scope => scope.Id = id);

        Assert.Equal(id, api.Resource.GetScopeId(Assert.Single(api.Resource.Scopes)));
        Assert.Contains("11112222-aaaa-3333-bbbb-4444cccc5555", api.Resource.GetBicepTemplateString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData(".read")]
    [InlineData(".default")]
    [InlineData("é")]
    public void WithScope_RejectsInvalidNames(string name)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api");

        Assert.ThrowsAny<ArgumentException>(() => api.WithScope(name, ScopeDescription));
        Assert.Empty(api.Resource.Scopes);
    }

    [Fact]
    public void WithScope_RejectsNamesLongerThanGraphAllows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api");

        api.WithScope(new string('a', 120), ScopeDescription);
        Assert.Throws<ArgumentException>(() => api.WithScope(new string('b', 121), ScopeDescription));
    }

    [Fact]
    public void WithScope_RejectsDuplicateNameAndId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var id = Guid.Parse("11112222-aaaa-3333-bbbb-4444cccc5555");
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription, scope => scope.Id = id);

        Assert.Throws<ArgumentException>(() => api.WithScope("ACCESS_AS_USER", ScopeDescription));
        Assert.Throws<ArgumentException>(() => api.WithScope("other", ScopeDescription, scope => scope.Id = id));
        Assert.Single(api.Resource.Scopes);
    }

    [Theory]
    [InlineData("access_as_user")]
    [InlineData("Files.Read.All")]
    [InlineData("orders/read")]
    [InlineData("a:b-c~d")]
    public void WithScope_AcceptsTheCharactersGraphAllows(string name)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope(name, ScopeDescription);

        Assert.Equal(name, Assert.Single(api.Resource.Scopes).Name);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("relative/path")]
    [InlineData("/orders")]
    [InlineData("file:///orders")]
    [InlineData("api://contoso?x=1")]
    [InlineData("api://contoso#fragment")]
    public void WithApplicationIdUri_RejectsInvalidUris(string uri)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api");

        // A rooted path such as "/orders" parses as an absolute file URI on Linux and macOS, so it must be rejected on purpose.
        Assert.Throws<ArgumentException>(() => api.WithApplicationIdUri(uri));
        Assert.Null(api.Resource.ApplicationIdUri);
    }

    [Theory]
    [InlineData("api://contoso-orders")]
    [InlineData("https://contoso.onmicrosoft.com/orders")]
    [InlineData("urn:contoso:orders")]
    public void WithApplicationIdUri_AcceptsAbsoluteUris(string uri)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithApplicationIdUri(uri);

        Assert.Equal(uri, api.Resource.ApplicationIdUri);
    }

    [Fact]
    public void WithApplicationIdUri_DefaultOverridesAnEarlierCustomUriInEitherOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var customThenDefault = builder.AddEntraIdApplication("custom-then-default")
            .WithScope("access_as_user", ScopeDescription)
            .WithApplicationIdUri("api://custom")
            .WithApplicationIdUri();
        var defaultThenCustom = builder.AddEntraIdApplication("default-then-custom")
            .WithScope("access_as_user", ScopeDescription)
            .WithApplicationIdUri()
            .WithApplicationIdUri("api://custom");

        Assert.Null(customThenDefault.Resource.ApplicationIdUri);
        var customThenDefaultBicep = customThenDefault.Resource.GetBicepTemplateString();
        Assert.Contains("'api://${application.appId}'", customThenDefaultBicep);
        Assert.DoesNotContain("api://custom", customThenDefaultBicep);

        Assert.Equal("api://custom", defaultThenCustom.Resource.ApplicationIdUri);
        var defaultThenCustomBicep = defaultThenCustom.Resource.GetBicepTemplateString();
        Assert.Contains("'api://custom'", defaultThenCustomBicep);
        Assert.DoesNotContain("api://${application.appId}", defaultThenCustomBicep);
    }

    [Fact]
    public void WithApplicationIdUri_DefaultDoesNotClearTheUriOfAnExistingRegistration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existing = builder.AddExistingEntraApplication("existing-api", TenantId, ClientId)
            .WithApplicationIdUri("api://contoso-orders")
            .WithApplicationIdUri();

        Assert.Equal("api://contoso-orders", existing.Resource.ApplicationIdUri);
    }

    [Theory]
    [InlineData("User.Read Directory.Read.All")]
    [InlineData("User.Read\tDirectory.Read.All")]
    [InlineData(" User.Read")]
    [InlineData("")]
    [InlineData(".default")]
    public void EntraIdApiPermission_RejectsNamesThatAreNotValidScopeValues(string name)
    {
        // A name with a space would be written into the space-separated scope of a consent grant, granting a second, undeclared permission.
        var userRead = new Guid("e1fe6dd8-ba31-4d61-89e7-88639da4683d");

        Assert.ThrowsAny<ArgumentException>(() => EntraIdApiPermission.Delegated(name));
        Assert.ThrowsAny<ArgumentException>(() => EntraIdApiPermission.Delegated(name, userRead));
        Assert.ThrowsAny<ArgumentException>(() => EntraIdApiPermission.Application(name));
        Assert.ThrowsAny<ArgumentException>(() => EntraIdApiPermission.Application(name, userRead));
    }

    [Fact]
    public void WithApiPermissions_RejectsAnExplicitIdOfAnotherCatalogPermission()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web");

        // The ID is User.Read's. A grant would name "Typo.Read" while the declaration requests User.Read.
        var exception = Assert.Throws<ArgumentException>(() => web.WithApiPermissions(
            EntraIdApi.MicrosoftGraph,
            EntraIdApiPermission.Delegated("Typo.Read", new Guid("e1fe6dd8-ba31-4d61-89e7-88639da4683d"))));
        Assert.Contains("'User.Read'", exception.Message);
        Assert.Empty(web.Resource.ApiAccess);

        // The same ID is the application permission of a different name, so the delegated catalog doesn't object.
        web.WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Application("Custom.Role", new Guid("e1fe6dd8-ba31-4d61-89e7-88639da4683d")));
    }

    [Fact]
    public async Task WithReference_OnlyMicrosoftGraphExcludesTheOpenIdScopesFromDownstreamApis()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var contoso = EntraIdApi.FromApplicationId("Contoso API", "99998888-aaaa-7777-bbbb-6666cccc5555", "api://contoso");
        var profileOnly = EntraIdApi.FromApplicationId("Profile API", "11110000-aaaa-7777-bbbb-6666cccc5555", "api://profile");
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("openid"), EntraIdApiPermission.Delegated("User.Read"))
            .WithApiPermissions(contoso, EntraIdApiPermission.Delegated("profile", Guid.NewGuid()), EntraIdApiPermission.Delegated("email", Guid.NewGuid()))
            .WithApiPermissions(profileOnly, EntraIdApiPermission.Delegated("offline_access", Guid.NewGuid()));
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        // Graph's OpenID Connect scopes are requested at sign-in; another API's scopes with the same names are its own.
        Assert.Equal("https://graph.microsoft.com/User.Read", Assert.Single(env, e => e.Key.StartsWith("DownstreamApis__MicrosoftGraph__Scopes", StringComparison.Ordinal)).Value);
        Assert.Equal("api://contoso/profile", env["DownstreamApis__ContosoAPI__Scopes__0"]);
        Assert.Equal("api://contoso/email", env["DownstreamApis__ContosoAPI__Scopes__1"]);
        Assert.Equal("api://profile/offline_access", env["DownstreamApis__ProfileAPI__Scopes__0"]);
    }

    [Fact]
    public async Task WithReference_UsesTheBaseUrlOfAnApiCreatedFromAnApplicationId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var graph = EntraIdApi.FromApplicationId(
            "Microsoft Graph", "00000003-0000-0000-c000-000000000000", "https://graph.microsoft.us", "https://graph.microsoft.us/v1.0");
        var contoso = EntraIdApi.FromApplicationId("Contoso API", "99998888-aaaa-7777-bbbb-6666cccc5555", "api://contoso");
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(graph, EntraIdApiPermission.Delegated("User.Read", new Guid("e1fe6dd8-ba31-4d61-89e7-88639da4683d")))
            .WithApiPermissions(contoso, EntraIdApiPermission.Delegated("items.read", Guid.NewGuid()));
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("https://graph.microsoft.us/User.Read", env["DownstreamApis__MicrosoftGraph__Scopes__0"]);
        Assert.Equal("https://graph.microsoft.us/v1.0", env["DownstreamApis__MicrosoftGraph__BaseUrl"]);
        Assert.Equal("api://contoso/items.read", env["DownstreamApis__ContosoAPI__Scopes__0"]);
        Assert.DoesNotContain("DownstreamApis__ContosoAPI__BaseUrl", env.Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("graph.microsoft.us")]
    [InlineData("/v1.0")]
    [InlineData("ftp://graph.microsoft.us")]
    public void FromApplicationId_RejectsABaseUrlThatIsNotAnAbsoluteHttpUrl(string baseUrl)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => EntraIdApi.FromApplicationId("Contoso API", "99998888-aaaa-7777-bbbb-6666cccc5555", "api://contoso", baseUrl));

        Assert.Equal("baseUrl", exception.ParamName);
    }

    [Fact]
    public async Task WithApiPermissions_Declares_ButDoesNotGrantOrConsent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user")
            .WithApiPermissions(
                EntraIdApi.MicrosoftGraph,
                EntraIdApiPermission.Delegated("User.Read"),
                EntraIdApiPermission.Delegated("offline_access"),
                EntraIdApiPermission.Application("User.Read.All"));

        var bicep = web.Resource.GetBicepTemplateString();

        Assert.DoesNotContain("oauth2PermissionGrants", bicep);
        Assert.DoesNotContain("appRoleAssignedTo", bicep);
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithAdminConsent_GrantsDelegatedScopesToAllUsersAndAppRolesToTheServicePrincipal()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithScope("other", "Another scope.");
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user", "other")
            .WithAdminConsent(api, "access_as_user")
            .WithApiPermissions(
                EntraIdApi.MicrosoftGraph,
                EntraIdApiPermission.Delegated("User.Read"),
                EntraIdApiPermission.Delegated("User.Read.All"),
                EntraIdApiPermission.Application("User.Read.All"),
                EntraIdApiPermission.Application("Directory.Read.All"))
            .WithAdminConsent(
                EntraIdApi.MicrosoftGraph,
                EntraIdApiPermission.Delegated("User.Read"),
                EntraIdApiPermission.Application("User.Read.All"));

        await Verify(web.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task WithApiPermissions_ExistingApi_UsesConfiguredScopeIdsAndClientId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var clientIdParameter = builder.AddParameter("api-client-id");
        var existingApi = builder.AddExistingEntraApplication("existing-api", builder.AddParameter("api-tenant-id"), clientIdParameter)
            .WithApplicationIdUri("api://contoso-orders")
            .WithScope("orders.read", "Read orders.", scope => scope.Id = Guid.Parse("11112222-aaaa-3333-bbbb-4444cccc5555"));
        var literalApi = builder.AddExistingEntraApplication("literal-api", TenantId, ClientId)
            .WithScope("items.read", "Read items.", scope => scope.Id = Guid.Parse("22223333-aaaa-4444-bbbb-5555cccc6666"));
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(existingApi, "orders.read")
            .WithApiPermissions(literalApi, "items.read")
            .WithAdminConsent(existingApi, "orders.read");

        await Verify(web.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void ExistingApplication_ExposedApiIsConfigurationOnly()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingApi = builder.AddExistingEntraApplication("existing-api", TenantId, ClientId)
            .WithApplicationIdUri("api://contoso-orders")
            .WithScope("orders.read", "Read orders.", scope => scope.Id = Guid.NewGuid());

        Assert.Equal("api://contoso-orders", existingApi.Resource.ApplicationIdUri);
        Assert.Throws<InvalidOperationException>(existingApi.Resource.GetBicepTemplateString);
    }

    [Fact]
    public void WithApiPermissions_ExistingApiScopeIdCanBeSetAfterTheDeclaration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingApi = builder.AddExistingEntraApplication("existing-api", TenantId, ClientId)
            .WithScope("orders.read", "Read orders.");
        var web = builder.AddEntraIdApplication("entra-web");

        // The ID of an existing registration's scope can't be derived, but it is only needed when the template is generated,
        // so it can be supplied after the permission is declared.
        web.WithApiPermissions(existingApi, "orders.read");
        var exception = Assert.Throws<InvalidOperationException>(web.Resource.GetBicepTemplateString);
        Assert.Contains("EntraIdScope.Id", exception.Message);

        var id = Guid.Parse("11112222-aaaa-3333-bbbb-4444cccc5555");
        existingApi.Resource.Scopes[0].Id = id;
        Assert.Contains($"id: '{id:D}'", web.Resource.GetBicepTemplateString());
    }
    [Fact]
    public void WithApiPermissions_ValidatesScopes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithScope("retired", "Retired.", scope => scope.IsEnabled = false);
        var web = builder.AddEntraIdApplication("entra-web");

        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(api));
        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(api, "unknown"));
        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(api, "ACCESS_AS_USER"));
        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(api, "retired"));
        Assert.Throws<ArgumentException>(() => api.WithApiPermissions(api, "access_as_user"));
        Assert.Empty(web.Resource.ApiAccess);
    }

    [Fact]
    public void ScopeDisabledAfterItWasDeclared_IsRejectedWhenTheCallersTemplateIsGenerated()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user")
            .WithAdminConsent(api, "access_as_user");

        // The scope was enabled when it was declared, so the declaration was accepted.
        Assert.Contains("access_as_user", web.Resource.GetBicepTemplateString());

        api.Resource.Scopes[0].IsEnabled = false;

        var template = Assert.Throws<InvalidOperationException>(web.Resource.GetBicepTemplateString);
        Assert.Contains("'entra-web' declares permissions to the scope 'access_as_user' of 'entra-api'", template.Message);
        Assert.Contains("disabled", template.Message);
        var validation = Assert.Throws<InvalidOperationException>(web.Resource.ValidateApiAccess);
        Assert.Equal(template.Message, validation.Message);
    }

    [Fact]
    public void ScopeDisabledAfterItWasPreAuthorized_IsRejectedWhenTheApisTemplateIsGenerated()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web");
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithPreAuthorizedApplication(web, "access_as_user");

        Assert.Contains("preAuthorizedApplications", api.Resource.GetBicepTemplateString());

        api.Resource.Scopes[0].IsEnabled = false;

        var template = Assert.Throws<InvalidOperationException>(api.Resource.GetBicepTemplateString);
        Assert.Contains("'entra-api' pre-authorizes 'entra-web' for the scope 'access_as_user'", template.Message);
        Assert.Contains("disabled", template.Message);
        Assert.Throws<InvalidOperationException>(api.Resource.ValidateApiAccess);
    }

    [Fact]
    public void ScopeDisabledAfterItWasDeclared_IsRejectedForAnExistingApi()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingApi = builder.AddExistingEntraApplication("existing-api", TenantId, ClientId)
            .WithApplicationIdUri("api://contoso-items")
            .WithScope("items.read", "Read items.", scope => scope.Id = Guid.NewGuid());
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(existingApi, "items.read");

        web.Resource.ValidateApiAccess();

        existingApi.Resource.Scopes[0].IsEnabled = false;

        var validation = Assert.Throws<InvalidOperationException>(web.Resource.ValidateApiAccess);
        Assert.Contains("'entra-web' declares permissions to the scope 'items.read' of 'existing-api'", validation.Message);
    }

    [Fact]
    public void WithApiPermissions_DeduplicatesPermissions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"))
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("user.read"), EntraIdApiPermission.Delegated("User.Read", new Guid("e1fe6dd8-ba31-4d61-89e7-88639da4683d")));

        var access = Assert.Single(web.Resource.ApiAccess);
        var permission = Assert.Single(access.Declared);
        Assert.Equal("User.Read", permission.Name);
    }

    [Fact]
    public void WithApiPermissions_DelegatedAndApplicationPermissionsWithTheSameNameStayDistinct()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(
                EntraIdApi.MicrosoftGraph,
                EntraIdApiPermission.Delegated("User.Read.All"),
                EntraIdApiPermission.Application("User.Read.All"));

        var declared = Assert.Single(web.Resource.ApiAccess).Declared;
        Assert.Equal(2, declared.Count);
        Assert.Equal(Guid.Parse("a154be20-db9c-4678-8ab7-66f6cc099a59"), Assert.Single(declared, p => p.Type == EntraIdPermissionType.Delegated).Id);
        Assert.Equal(Guid.Parse("df021288-bdef-4463-88db-98f22de89214"), Assert.Single(declared, p => p.Type == EntraIdPermissionType.Application).Id);
    }

    [Fact]
    public void WithApiPermissions_RejectsUnknownPermissionsAndMismatchedIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web");

        var unknown = Assert.Throws<ArgumentException>(() =>
            web.WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("Not.A.Permission")));
        Assert.Contains("Not.A.Permission", unknown.Message);
        Assert.Contains("User.Read", unknown.Message);

        // The application permission exists only as a delegated one in the catalog's ARM entry.
        Assert.Throws<ArgumentException>(() =>
            web.WithApiPermissions(EntraIdApi.AzureResourceManager, EntraIdApiPermission.Application("user_impersonation")));

        var mismatch = Assert.Throws<ArgumentException>(() =>
            web.WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read", Guid.NewGuid())));
        Assert.Contains("e1fe6dd8-ba31-4d61-89e7-88639da4683d", mismatch.Message);

        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(EntraIdApi.MicrosoftGraph));
        Assert.Throws<ArgumentException>(() => EntraIdApiPermission.Delegated("User.Read", Guid.Empty));
        Assert.Empty(web.Resource.ApiAccess);
    }

    [Fact]
    public async Task WithApiPermissions_AcceptsPermissionsOutsideTheCatalogById()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var mailSend = Guid.Parse("b633e1c5-b582-4048-a93e-9f11b44c7e96");
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Application("Mail.Send", mailSend))
            .WithApiPermissions(
                EntraIdApi.FromApplicationId("Contoso API", "99998888-aaaa-7777-bbbb-6666cccc5555", "api://contoso"),
                EntraIdApiPermission.Delegated("Contoso.Read", Guid.Parse("33334444-aaaa-5555-bbbb-6666cccc7777")))
            .WithAdminConsent(
                EntraIdApi.FromApplicationId("Contoso API", "99998888-aaaa-7777-bbbb-6666cccc5555", "api://contoso"),
                EntraIdApiPermission.Delegated("Contoso.Read", Guid.Parse("33334444-aaaa-5555-bbbb-6666cccc7777")));

        await Verify(web.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void EntraIdApi_FromApplicationId_RequiresIdsAndAGuid()
    {
        var custom = EntraIdApi.FromApplicationId("Contoso API", "99998888-AAAA-7777-bbbb-6666cccc5555", "api://contoso");

        Assert.Equal("99998888-aaaa-7777-bbbb-6666cccc5555", custom.ApplicationId);
        Assert.Throws<ArgumentException>(() => EntraIdApi.FromApplicationId("Contoso API", "not-a-guid", "api://contoso"));

        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web");
        var exception = Assert.Throws<ArgumentException>(() => web.WithApiPermissions(custom, EntraIdApiPermission.Delegated("Contoso.Read")));
        Assert.Contains("Contoso.Read", exception.Message);
    }

    [Fact]
    public void WellKnownApis_HaveStableApplicationIds()
    {
        Assert.Equal("00000003-0000-0000-c000-000000000000", EntraIdApi.MicrosoftGraph.ApplicationId);
        Assert.Equal("797f4846-ba00-4fd7-ba43-dac1f8f63013", EntraIdApi.AzureResourceManager.ApplicationId);
        Assert.Equal("e406a681-f3d4-42a8-90b6-c2b029497af1", EntraIdApi.AzureStorage.ApplicationId);
    }

    [Fact]
    public void WithAdminConsent_RequiresTheSamePermissionToBeDeclaredFirst()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"));

        Assert.Throws<InvalidOperationException>(() => web.WithAdminConsent(api, "access_as_user"));
        Assert.Throws<InvalidOperationException>(() => web.WithAdminConsent(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read.All")));

        // Declaring a delegated permission does not declare the application permission with the same name.
        Assert.Throws<InvalidOperationException>(() => web.WithAdminConsent(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Application("Mail.Read")));
        Assert.Empty(Assert.Single(web.Resource.ApiAccess).Consented);
    }

    [Fact]
    public async Task WithPreAuthorizedApplication_WritesClientIdsAndScopeIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web");
        var existingClient = builder.AddExistingEntraApplication("existing-client", TenantId, ClientId);
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithScope("other", "Another scope.")
            .WithPreAuthorizedApplication(web, "access_as_user")
            .WithPreAuthorizedApplication(web, "other")
            .WithPreAuthorizedApplication(existingClient, "access_as_user");

        Assert.Equal(2, api.Resource.PreAuthorizedClients.Count);
        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void WithPreAuthorizedApplication_ValidatesScopes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web");
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithScope("retired", "Retired.", scope => scope.IsEnabled = false);

        Assert.Throws<ArgumentException>(() => api.WithPreAuthorizedApplication(web));
        Assert.Throws<ArgumentException>(() => api.WithPreAuthorizedApplication(web, "unknown"));
        Assert.Throws<ArgumentException>(() => api.WithPreAuthorizedApplication(web, "retired"));
        Assert.Empty(api.Resource.PreAuthorizedClients);
    }

    [Fact]
    public void PreAuthorizationAndDeclarationBetweenTheSameRegistrationsCannotBothBeUsed()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web").WithApiPermissions(api, "access_as_user");

        var exception = Assert.Throws<InvalidOperationException>(() => api.WithPreAuthorizedApplication(web, "access_as_user"));
        Assert.Contains("not both", exception.Message);
        Assert.Empty(api.Resource.PreAuthorizedClients);

        var other = builder.AddEntraIdApplication("other-api").WithScope("access_as_user", ScopeDescription);
        var otherWeb = builder.AddEntraIdApplication("other-web");
        other.WithPreAuthorizedApplication(otherWeb, "access_as_user");
        Assert.Throws<InvalidOperationException>(() => otherWeb.WithApiPermissions(other, "access_as_user"));
        Assert.Empty(otherWeb.Resource.ApiAccess);
    }

    [Fact]
    public void DependencyCyclesThroughOtherRegistrationsAreRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var a = builder.AddEntraIdApplication("a").WithScope("scope", ScopeDescription);
        var b = builder.AddEntraIdApplication("b").WithScope("scope", ScopeDescription);
        var c = builder.AddEntraIdApplication("c").WithScope("scope", ScopeDescription);
        a.WithApiPermissions(b, "scope");
        b.WithApiPermissions(c, "scope");

        Assert.Throws<InvalidOperationException>(() => c.WithApiPermissions(a, "scope"));
        Assert.Throws<InvalidOperationException>(() => c.WithPreAuthorizedApplication(a, "scope"));
    }

    [Fact]
    public async Task ExistingRegistrationsDoNotCreateDependencyCycles()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingWeb = builder.AddExistingEntraApplication("existing-web", TenantId, ClientId);
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithPreAuthorizedApplication(existingWeb, "access_as_user");

        // The existing registration deploys nothing, so it waits for nothing and the API's module can depend on its client ID.
        existingWeb.WithApiPermissions(api, "access_as_user");
        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void GetScopeUri_BuildsTheFullScopeFromTheApplicationIdUri()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var created = builder.AddEntraIdApplication("created-api").WithScope("access_as_user", ScopeDescription);
        var custom = builder.AddEntraIdApplication("custom-api").WithApplicationIdUri("api://contoso-orders").WithScope("orders.read", "Read orders.");
        var existing = builder.AddExistingEntraApplication("existing-api", TenantId, ClientId).WithApplicationIdUri("api://existing");
        existing.WithScope("items.read", "Read items.", scope => scope.Id = Guid.NewGuid());
        var existingWithoutUri = builder.AddExistingEntraApplication("no-uri-api", TenantId, ClientId);
        existingWithoutUri.WithScope("items.read", "Read items.", scope => scope.Id = Guid.NewGuid());

        Assert.Equal("{created-api.outputs.applicationIdUri}/access_as_user", created.Resource.GetScopeUri("access_as_user").ValueExpression);
        Assert.Equal("api://contoso-orders/orders.read", custom.Resource.GetScopeUri("orders.read").ValueExpression);
        Assert.Equal("api://existing/items.read", existing.Resource.GetScopeUri("items.read").ValueExpression);
        Assert.Throws<ArgumentException>(() => created.Resource.GetScopeUri("unknown"));
        Assert.Throws<InvalidOperationException>(() => existingWithoutUri.Resource.GetScopeUri("items.read"));
    }

    [Fact]
    public async Task Publish_WiresApiOutputsIntoTheCallerModuleAndPreAuthorizedClientsIntoTheApiModule()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, workspace.Path);
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user")
            .WithAdminConsent(api, "access_as_user")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"));
        var daemon = builder.AddEntraIdApplication("entra-daemon");
        api.WithPreAuthorizedApplication(daemon, "access_as_user");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environment = model.Resources.OfType<AzureEnvironmentResource>().Single();
        var options = app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>().Value;
        await using var step = await new TestPipelineActivityReporter(outputHelper).CreateStepAsync("Publishing Entra registrations");
        var context = new AzurePublishingContext(
            workspace.Path, options, app.Services, NullLogger<AzurePublishingContext>.Instance, step);

        await context.WriteModelAsync(model, environment);

        await Verify(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "entra-api", "entra-api.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "entra-web", "entra-web.bicep")), "bicep");
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithReference_FlowsDeclaredApiScopesToDownstreamApis(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web").WithApiPermissions(api, "access_as_user");
        foreach (var (entra, prefix) in new[] { (api, "api"), (web, "web") })
        {
            entra.Resource.Outputs["tenantId"] = TenantId;
            entra.Resource.Outputs["clientId"] = ClientId;
            entra.Resource.Outputs["applicationIdUri"] = $"api://{prefix}-{ClientId}";
            entra.Resource.ProvisioningTaskCompletionSource!.SetResult();
        }

        builder.AddResource(new ProjectResource("apiservice")).WithReference(api);
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, operation, TestServiceProvider.Instance);

        Assert.Equal(
            operation == DistributedApplicationOperation.Run ? $"api://api-{ClientId}/access_as_user" : "{entra-api.outputs.applicationIdUri}/access_as_user",
            Assert.Single(env, e => e.Key.StartsWith("DownstreamApis__", StringComparison.Ordinal)).Value);
        Assert.Equal("DownstreamApis__apiservice__Scopes__0", Assert.Single(env, e => e.Key.StartsWith("DownstreamApis__", StringComparison.Ordinal)).Key);
        Assert.DoesNotContain("AzureAd__Scopes", env.Keys);
    }

    [Fact]
    public async Task WithReference_FlowsFirstPartyApiPermissionsAsDelegatedAndAppOnlyEntries()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(
                EntraIdApi.MicrosoftGraph,
                EntraIdApiPermission.Delegated("openid"),
                EntraIdApiPermission.Delegated("User.Read"),
                EntraIdApiPermission.Delegated("Mail.Read"),
                EntraIdApiPermission.Application("User.Read.All"))
            .WithApiPermissions(EntraIdApi.AzureStorage, EntraIdApiPermission.Delegated("user_impersonation"));
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);
        var container = builder.AddContainer("worker", "myimage").WithReference(web, connectionName: "ENTRA");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        var containerEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["DownstreamApis__MicrosoftGraph__Scopes__0"] = "https://graph.microsoft.com/User.Read",
            ["DownstreamApis__MicrosoftGraph__Scopes__1"] = "https://graph.microsoft.com/Mail.Read",
            ["DownstreamApis__MicrosoftGraph__BaseUrl"] = "https://graph.microsoft.com/v1.0",
            ["DownstreamApis__MicrosoftGraphAppOnly__Scopes__0"] = "https://graph.microsoft.com/.default",
            ["DownstreamApis__MicrosoftGraphAppOnly__BaseUrl"] = "https://graph.microsoft.com/v1.0",
            ["DownstreamApis__MicrosoftGraphAppOnly__RequestAppToken"] = "true",
            ["DownstreamApis__AzureStorage__Scopes__0"] = "https://storage.azure.com/user_impersonation"
        }, env.Where(e => e.Key.StartsWith("DownstreamApis__", StringComparison.Ordinal)).ToDictionary());
        Assert.Equal(new Dictionary<string, string>
        {
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_SCOPES_0"] = "https://graph.microsoft.com/User.Read",
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_SCOPES_1"] = "https://graph.microsoft.com/Mail.Read",
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_BASE_URL"] = "https://graph.microsoft.com/v1.0",
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_APP_ONLY_SCOPES_0"] = "https://graph.microsoft.com/.default",
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_APP_ONLY_BASE_URL"] = "https://graph.microsoft.com/v1.0",
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_APP_ONLY_REQUEST_APP_TOKEN"] = "true",
            ["ENTRA_DOWNSTREAM_APIS_AZURE_STORAGE_SCOPES_0"] = "https://storage.azure.com/user_impersonation"
        }, containerEnv.Where(e => e.Key.StartsWith("ENTRA_DOWNSTREAM", StringComparison.Ordinal)).ToDictionary());
    }

    [Fact]
    public async Task WithReference_FlowsScopesOfExistingApisUsingTheirConfiguredApplicationIdUri()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var orders = builder.AddExistingEntraApplication("orders-entra", TenantId, ClientId)
            .WithApplicationIdUri("api://contoso-orders")
            .WithScope("orders.read", "Read orders.", scope => scope.Id = Guid.NewGuid());
        var items = builder.AddExistingEntraApplication("items-entra", TenantId, ClientId)
            .WithApplicationIdUri("https://contoso.onmicrosoft.com/items")
            .WithScope("items.read", "Read items.", scope => scope.Id = Guid.NewGuid());
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(orders, "orders.read")
            .WithApiPermissions(items, "items.read");
        web.Resource.Outputs["tenantId"] = TenantId;
        web.Resource.Outputs["clientId"] = ClientId;
        web.Resource.ProvisioningTaskCompletionSource!.SetResult();
        builder.AddResource(new ProjectResource("orders-svc")).WithReference(orders);
        builder.AddResource(new ProjectResource("items-svc")).WithReference(items);
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        // The consumer's name is used as is, like the resource name in service discovery.
        Assert.Equal("api://contoso-orders/orders.read", env["DownstreamApis__orders-svc__Scopes__0"]);
        Assert.Equal("https://contoso.onmicrosoft.com/items/items.read", env["DownstreamApis__items-svc__Scopes__0"]);
    }

    [Fact]
    public async Task WithReference_RejectsAnExistingApiWithoutAnApplicationIdUri()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingApi = builder.AddExistingEntraApplication("without-uri", TenantId, ClientId)
            .WithScope("items.read", "Read items.", scope => scope.Id = Guid.NewGuid());
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(existingApi, "items.read");

        // An existing registration can use any URI, so a made-up api://{clientId} would only fail later, as a token error.
        // Nothing references the client yet, so there is nothing to configure and nothing to reject.
        web.Resource.ValidateApiAccess();

        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var validation = Assert.Throws<InvalidOperationException>(web.Resource.ValidateApiAccess);
        Assert.Contains("'entra-web'", validation.Message);
        Assert.Contains("WithApplicationIdUri on 'without-uri'", validation.Message);
        var environment = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));
        Assert.Equal(validation.Message, environment.Message);

        // Setting the URI, in any order, resolves it.
        existingApi.WithApplicationIdUri("api://contoso-items");
        web.Resource.ValidateApiAccess();
    }

    [Fact]
    public async Task ValidateApiAccess_RunsBeforeStartForCreatedAndExistingRegistrationsThatDeclarePermissions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("api-entra").WithScope("access_as_user", ScopeDescription);
        var created = builder.AddEntraIdApplication("created-web");
        var existing = builder.AddExistingEntraApplication("existing-web", TenantId, ClientId);
        var existingWithoutDeclarations = builder.AddExistingEntraApplication("existing-other", TenantId, ClientId);

        // A registration that declares nothing has nothing to validate, and an existing one keeps no pipeline steps.
        Assert.Empty(existing.Resource.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Empty(existingWithoutDeclarations.Resource.Annotations.OfType<PipelineStepAnnotation>());

        created.WithApiPermissions(api, "access_as_user");
        existing.WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"));
        existing.WithApiPermissions(EntraIdApi.AzureStorage, EntraIdApiPermission.Delegated("user_impersonation"));

        foreach (var resource in new[] { created.Resource, existing.Resource })
        {
            var annotation = Assert.Single(resource.Annotations.OfType<PipelineStepAnnotation>(), IsApiAccessValidation);
            var step = Assert.Single(await annotation.CreateStepsAsync(new PipelineStepFactoryContext { PipelineContext = null!, Resource = resource }));
            Assert.Equal([WellKnownPipelineSteps.BeforeStart], step.RequiredBySteps);

            bool IsApiAccessValidation(PipelineStepAnnotation a) =>
                a.CreateStepsAsync(new PipelineStepFactoryContext { PipelineContext = null!, Resource = resource }).GetAwaiter().GetResult()
                    .Any(s => s.Name == $"validate-{resource.Name}-api-access");
        }
    }
    [Fact]
    public void ValidateApiAccess_RejectsTwoApiRegistrationsReferencedByTheSameResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var orders = builder.AddEntraIdApplication("orders-entra").WithScope("orders.read", "Read orders.");
        var items = builder.AddEntraIdApplication("items-entra").WithScope("items.read", "Read items.");

        // One project validates tokens for both registrations.
        builder.AddResource(new ProjectResource("gateway"))
            .WithReference(orders, connectionName: "Orders")
            .WithReference(items, connectionName: "Items");

        var web = builder.AddEntraIdApplication("web-entra")
            .WithApiPermissions(orders, "orders.read")
            .WithApiPermissions(items, "items.read");

        // Nothing references the client yet, so there are no entries to collide.
        web.Resource.ValidateApiAccess();

        builder.AddResource(new ProjectResource("web")).WithReference(web);

        var exception = Assert.Throws<InvalidOperationException>(web.Resource.ValidateApiAccess);
        Assert.Equal(
            "'web-entra' declares permissions to the registrations 'orders-entra' and 'items-entra', and both would be configured as the " +
            "DownstreamApis entry 'gateway', the name of a resource that references both. An entry holds the scopes of one API, so the consumer " +
            "couldn't tell them apart, and one token request can't cover both. Reference 'orders-entra' and 'items-entra' from different resources.",
            exception.Message);
    }

    [Fact]
    public async Task ValidateApiAccess_ReferencingTheApisFromDifferentResourcesIsAllowed()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var orders = builder.AddExistingEntraApplication("orders-entra", TenantId, ClientId)
            .WithApplicationIdUri("api://orders").WithScope("orders.read", "Read orders.", s => s.Id = Guid.NewGuid());
        var items = builder.AddExistingEntraApplication("items-entra", TenantId, ClientId)
            .WithApplicationIdUri("api://items").WithScope("items.read", "Read items.", s => s.Id = Guid.NewGuid());
        builder.AddResource(new ProjectResource("orders-svc")).WithReference(orders);
        builder.AddResource(new ProjectResource("items-svc")).WithReference(items);
        var web = builder.AddExistingEntraApplication("web-entra", TenantId, ClientId)
            .WithApiPermissions(orders, "orders.read")
            .WithApiPermissions(items, "items.read");
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        web.Resource.ValidateApiAccess();
        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("api://orders/orders.read", env["DownstreamApis__orders-svc__Scopes__0"]);
        Assert.Equal("api://items/items.read", env["DownstreamApis__items-svc__Scopes__0"]);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("relative/path")]
    [InlineData("api://contoso?x=1")]
    public void EntraIdApi_FromApplicationId_RejectsInvalidResourceUris(string uri)
    {
        Assert.Throws<ArgumentException>(() => EntraIdApi.FromApplicationId("Contoso API", "99998888-aaaa-7777-bbbb-6666cccc5555", uri));
    }

    [Fact]
    public async Task WithReference_FirstPartyApiScopesUseTheConfiguredResourceUriNotTheApplicationId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var contoso = EntraIdApi.FromApplicationId("Contoso API", "99998888-aaaa-7777-bbbb-6666cccc5555", "https://contoso.onmicrosoft.com/orders");
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(contoso, EntraIdApiPermission.Delegated("Orders.Read", Guid.NewGuid()), EntraIdApiPermission.Application("Orders.Sync", Guid.NewGuid()));
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("https://contoso.onmicrosoft.com/orders/Orders.Read", env["DownstreamApis__ContosoAPI__Scopes__0"]);
        Assert.Equal("https://contoso.onmicrosoft.com/orders/.default", env["DownstreamApis__ContosoAPIAppOnly__Scopes__0"]);
        Assert.DoesNotContain(env.Values, v => v.Contains("99998888", StringComparison.Ordinal));
    }
    [Fact]
    public async Task WithReference_FlowsOnlyEnabledExposedScopesToTheApi()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddExistingEntraApplication("entra-api", TenantId, ClientId)
            .WithScope("access_as_user", ScopeDescription, scope => scope.Id = Guid.NewGuid())
            .WithScope("retired", "Retired.", scope => { scope.Id = Guid.NewGuid(); scope.IsEnabled = false; })
            .WithScope("other", "Another scope.", scope => scope.Id = Guid.NewGuid());
        var project = builder.AddResource(new ProjectResource("api")).WithReference(api);
        var container = builder.AddContainer("worker", "myimage").WithReference(api);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        var containerEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("access_as_user other", env["AzureAd__Scopes"]);
        Assert.Equal("access_as_user other", containerEnv["ENTRA_API_SCOPES"]);
        Assert.DoesNotContain(env.Keys, k => k.StartsWith("DownstreamApis__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithReference_WithoutDeclarationsOrScopesAddsNoApiConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId);
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(["AzureAd__ClientId", "AzureAd__Instance", "AzureAd__TenantId"], env.Keys.Order());
    }

    [Fact]
    public async Task WithReference_RejectsApisThatShareADownstreamName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = EntraIdApi.FromApplicationId("My API", "99998888-aaaa-7777-bbbb-6666cccc5555", "api://my-api");
        var second = EntraIdApi.FromApplicationId("MyAPI", "11110000-aaaa-7777-bbbb-6666cccc5555", "api://my-api-2");
        var permission = EntraIdApiPermission.Delegated("Read", Guid.NewGuid());
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(first, permission)
            .WithApiPermissions(second, permission);
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));
        Assert.Contains("EntraIdApi.FromApplicationId", exception.Message);
    }

    [Fact]
    public async Task WithReference_FirstPartyApisWithDifferentDisplayNamesGetSeparateEntries()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = EntraIdApi.FromApplicationId("My API", "99998888-aaaa-7777-bbbb-6666cccc5555", "api://my-api");
        var second = EntraIdApi.FromApplicationId("Other API", "11110000-aaaa-7777-bbbb-6666cccc5555", "https://contoso.onmicrosoft.com/other/");
        var permission = EntraIdApiPermission.Delegated("Read", Guid.NewGuid());
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(first, permission)
            .WithApiPermissions(second, permission, EntraIdApiPermission.Application("Read.All", Guid.NewGuid()));
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("api://my-api/Read", env["DownstreamApis__MyAPI__Scopes__0"]);
        // The trailing slash is part of the resource identifier. It separates a permission from the identifier, and ".default" follows
        // another slash, as in "https://management.azure.com//.default".
        Assert.Equal("https://contoso.onmicrosoft.com/other/Read", env["DownstreamApis__OtherAPI__Scopes__0"]);
        Assert.Equal("https://contoso.onmicrosoft.com/other//.default", env["DownstreamApis__OtherAPIAppOnly__Scopes__0"]);
    }
    [Fact]
    public async Task WithReference_NamesDownstreamApisAfterTheResourcesThatReferenceTheApi()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiEntra = builder.AddExistingEntraApplication("apiservice-entra", TenantId, ClientId)
            .WithApplicationIdUri(ApiUri)
            .WithScope("access_as_user", ScopeDescription, scope => scope.Id = Guid.NewGuid());
        var webEntra = builder.AddExistingEntraApplication("webfrontend-entra", TenantId, ClientId)
            .WithApiPermissions(apiEntra, "access_as_user");

        // The consumers are read when the environment is resolved, so the order of the calls doesn't matter.
        var webFrontend = builder.AddResource(new ProjectResource("webfrontend")).WithReference(webEntra);
        builder.AddResource(new ProjectResource("apiservice")).WithReference(apiEntra);
        builder.AddResource(new ProjectResource("apiservice2")).WithReference(apiEntra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(webFrontend.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(
            ["DownstreamApis__apiservice2__Scopes__0", "DownstreamApis__apiservice__Scopes__0"],
            env.Keys.Where(k => k.StartsWith("DownstreamApis__", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WithReference_FallsBackToTheApiRegistrationNameWhenNothingReferencesIt()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiEntra = builder.AddExistingEntraApplication("apiservice-entra", TenantId, ClientId)
            .WithApplicationIdUri(ApiUri)
            .WithScope("access_as_user", ScopeDescription, scope => scope.Id = Guid.NewGuid());
        var webEntra = builder.AddExistingEntraApplication("webfrontend-entra", TenantId, ClientId)
            .WithApiPermissions(apiEntra, "access_as_user");
        var webFrontend = builder.AddResource(new ProjectResource("webfrontend")).WithReference(webEntra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(webFrontend.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"{ApiUri}/access_as_user", env["DownstreamApis__apiservice_entra__Scopes__0"]);
    }

    [Theory]
    [InlineData("MyDownstreamApis", "MyDownstreamApis__apiservice__Scopes__0")]
    [InlineData("Section:MyApis", "Section__MyApis__apiservice__Scopes__0")]
    public async Task WithReference_UsesTheDownstreamApisPrefix(string prefix, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiEntra = builder.AddExistingEntraApplication("apiservice-entra", TenantId, ClientId)
            .WithApplicationIdUri(ApiUri)
            .WithScope("access_as_user", ScopeDescription, scope => scope.Id = Guid.NewGuid());
        var webEntra = builder.AddExistingEntraApplication("webfrontend-entra", TenantId, ClientId)
            .WithApiPermissions(apiEntra, "access_as_user");
        builder.AddResource(new ProjectResource("apiservice")).WithReference(apiEntra);
        var webFrontend = builder.AddResource(new ProjectResource("webfrontend"))
            .WithReference(webEntra, connectionName: null, downstreamApisPrefix: prefix);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(webFrontend.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"{ApiUri}/access_as_user", env[expected]);
        Assert.Single(env.Keys, k => k.Contains("Apis", StringComparison.Ordinal));
        Assert.Equal("AzureAd__ClientId", Assert.Single(env.Keys, k => k.EndsWith("ClientId", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WithReference_UsesTheDownstreamApisPrefixForOtherLanguages()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiEntra = builder.AddExistingEntraApplication("apiservice-entra", TenantId, ClientId)
            .WithApplicationIdUri(ApiUri)
            .WithScope("access_as_user", ScopeDescription, scope => scope.Id = Guid.NewGuid());
        var webEntra = builder.AddExistingEntraApplication("webfrontend-entra", TenantId, ClientId)
            .WithApiPermissions(apiEntra, "access_as_user");
        builder.AddContainer("api-container", "myimage").WithReference(apiEntra);
        var worker = builder.AddContainer("worker", "myimage").WithReference(webEntra, "ENTRA", "MyApis");
        var defaultWorker = builder.AddContainer("default-worker", "myimage").WithReference(webEntra, "ENTRA");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(worker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        var defaultEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(defaultWorker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"{ApiUri}/access_as_user", env["ENTRA_MY_APIS_API_CONTAINER_SCOPES_0"]);
        Assert.Equal($"{ApiUri}/access_as_user", defaultEnv["ENTRA_DOWNSTREAM_APIS_API_CONTAINER_SCOPES_0"]);
    }

    [Fact]
    public void WithReference_RejectsAnEmptyDownstreamApisPrefix()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var webEntra = builder.AddExistingEntraApplication("webfrontend-entra", TenantId, ClientId);
        var project = builder.AddResource(new ProjectResource("webfrontend"));

        Assert.Throws<ArgumentException>(() => project.WithReference(webEntra, connectionName: null, downstreamApisPrefix: ""));
        Assert.Throws<ArgumentException>(() => project.WithReference(webEntra, connectionName: null, downstreamApisPrefix: " "));
    }

    [Fact]
    public async Task BeforeStart_FailsWhenTwoApiRegistrationsAreReferencedByTheSameResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var orders = builder.AddExistingEntraApplication("orders-entra", TenantId, ClientId)
            .WithApplicationIdUri("api://orders").WithScope("orders.read", "Read orders.", s => s.Id = Guid.NewGuid());
        var items = builder.AddExistingEntraApplication("items-entra", TenantId, ClientId)
            .WithApplicationIdUri("api://items").WithScope("items.read", "Read items.", s => s.Id = Guid.NewGuid());
        builder.AddResource(new ProjectResource("gateway")).WithReference(orders, "Orders").WithReference(items, "Items");
        var web = builder.AddExistingEntraApplication("web-entra", TenantId, ClientId)
            .WithApiPermissions(orders, "orders.read")
            .WithApiPermissions(items, "items.read");
        builder.AddResource(new ProjectResource("web")).WithReference(web);

        using var app = builder.Build();

        // The model is rejected before any resource starts, not when a consumer later resolves its environment.
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("validate-web-entra-api-access", exception.ToString());
        Assert.Contains("DownstreamApis entry 'gateway'", exception.ToString());
    }

    [Fact]
    public async Task BeforeStart_FailsWhenAnExistingApiHasNoApplicationIdUri()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddExistingEntraApplication("items-entra", TenantId, ClientId)
            .WithScope("items.read", "Read items.", s => s.Id = Guid.NewGuid());
        var web = builder.AddExistingEntraApplication("web-entra", TenantId, ClientId)
            .WithApiPermissions(api, "items.read");
        builder.AddResource(new ProjectResource("web")).WithReference(web);

        using var app = builder.Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("WithApplicationIdUri on 'items-entra'", exception.ToString());
    }

    [Fact]
    public async Task BeforeStart_SucceedsWhenTheDeclaredApisAreValid()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddExistingEntraApplication("items-entra", TenantId, ClientId)
            .WithApplicationIdUri("api://items").WithScope("items.read", "Read items.", s => s.Id = Guid.NewGuid());
        builder.AddResource(new ProjectResource("items-svc")).WithReference(api);
        var web = builder.AddExistingEntraApplication("web-entra", TenantId, ClientId)
            .WithApiPermissions(api, "items.read")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"));
        builder.AddResource(new ProjectResource("web")).WithReference(web);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    [Fact]
    public async Task Symbols_ApisWhoseNamesDifferOnlyByHyphenOrCaseKeepSeparateParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var hyphenated = builder.AddEntraIdApplication("orders-api").WithScope("read", "Read");
        var camel = builder.AddEntraIdApplication("ordersApi").WithScope("read", "Read");
        var web = builder.AddEntraIdApplication("web")
            .WithApiPermissions(hyphenated, "read")
            .WithApiPermissions(camel, "read")
            .WithAdminConsent(hyphenated, "read")
            .WithAdminConsent(camel, "read");

        var bicep = web.Resource.GetBicepTemplateString();

        // Both used to be named ordersApiClientId, which bound the parameter twice and wired both requirements to the last API.
        Assert.Equal("orders-api", ((BicepOutputReference)web.Resource.Parameters["orders_apiClientId"]!).Resource.Name);
        Assert.Equal("ordersApi", ((BicepOutputReference)web.Resource.Parameters["ordersApiClientId"]!).Resource.Name);
        Assert.Equal("orders-api", ((BicepOutputReference)web.Resource.Parameters["orders_apiServicePrincipalId"]!).Resource.Name);
        Assert.Equal("ordersApi", ((BicepOutputReference)web.Resource.Parameters["ordersApiServicePrincipalId"]!).Resource.Name);
        AssertSymbolsAreUniqueValidIdentifiers(bicep);
        Assert.Equal(bicep, web.Resource.GetBicepTemplateString());
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task Symbols_CustomApisWithTheSameDisplayNameKeepSeparateSymbols()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var one = EntraIdApi.FromApplicationId("Contoso API", "99998888-aaaa-7777-bbbb-6666cccc5555", "api://one");
        var two = EntraIdApi.FromApplicationId("Contoso API", "11110000-aaaa-7777-bbbb-6666cccc5555", "api://two");
        var read = EntraIdApiPermission.Delegated("Read", Guid.Parse("33334444-aaaa-5555-bbbb-6666cccc7777"));
        var web = builder.AddEntraIdApplication("web")
            .WithApiPermissions(one, read)
            .WithApiPermissions(two, read)
            .WithAdminConsent(one, read)
            .WithAdminConsent(two, read);

        var bicep = web.Resource.GetBicepTemplateString();

        // Two grants and two service principal lookups with the same symbol would not compile.
        AssertSymbolsAreUniqueValidIdentifiers(bicep);
        Assert.Contains("contosoAPIDelegatedGrant ", bicep);
        Assert.Contains("contosoAPIDelegatedGrant_2 ", bicep);
        await Verify(bicep, "bicep");
    }

    [Fact]
    public void Symbols_RoleAssignmentsWhoseNamesWouldRunTogetherStayDistinct()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var a = EntraIdApi.FromApplicationId("a", "99998888-aaaa-7777-bbbb-6666cccc5555", "api://a");
        var aB = EntraIdApi.FromApplicationId("aB", "11110000-aaaa-7777-bbbb-6666cccc5555", "api://ab");
        var bc = EntraIdApiPermission.Application("BC", Guid.Parse("33334444-aaaa-5555-bbbb-6666cccc7777"));
        var c = EntraIdApiPermission.Application("C", Guid.Parse("44445555-aaaa-5555-bbbb-6666cccc7777"));
        var web = builder.AddEntraIdApplication("web")
            .WithApiPermissions(a, bc)
            .WithApiPermissions(aB, c)
            .WithAdminConsent(a, bc)
            .WithAdminConsent(aB, c);

        var bicep = web.Resource.GetBicepTemplateString();

        // "a" + "BC" and "aB" + "C" both read aBCRoleAssignment.
        AssertSymbolsAreUniqueValidIdentifiers(bicep);
        Assert.Contains("aBCRoleAssignment ", bicep);
        Assert.Contains("aBCRoleAssignment_2 ", bicep);
    }

    [Fact]
    public void Symbols_AreValidWhenADisplayNameDoesNotStartWithALetter()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var digits = EntraIdApi.FromApplicationId("2FA API", "99998888-aaaa-7777-bbbb-6666cccc5555", "api://2fa");
        var punctuation = EntraIdApi.FromApplicationId("---", "11110000-aaaa-7777-bbbb-6666cccc5555", "api://dashes");
        var read = EntraIdApiPermission.Delegated("Read", Guid.Parse("33334444-aaaa-5555-bbbb-6666cccc7777"));
        var web = builder.AddEntraIdApplication("web")
            .WithApiPermissions(digits, read)
            .WithApiPermissions(punctuation, read)
            .WithAdminConsent(digits, read)
            .WithAdminConsent(punctuation, read);

        var bicep = web.Resource.GetBicepTemplateString();

        AssertSymbolsAreUniqueValidIdentifiers(bicep);
        Assert.Contains("api2FAAPIDelegatedGrant ", bicep);
    }

    [Fact]
    public void Symbols_DoNotCollideWithParametersFromTheAppHost()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("orders-api").WithScope("read", "Read");

        // A redirect URI parameter whose symbol is exactly the one that the API's client ID would take: a resource name can't contain
        // an underscore, so "orders-apiClientId" is the spelling that normalizes to "orders_apiClientId".
        var web = builder.AddEntraIdApplication("web")
            .WithRedirectUris(builder.AddParameter("orders-apiClientId"))
            .WithApiPermissions(api, "read");

        var bicep = web.Resource.GetBicepTemplateString();

        AssertSymbolsAreUniqueValidIdentifiers(bicep);
        Assert.IsType<ParameterResource>(web.Resource.Parameters["orders_apiClientId"]);
        Assert.Equal("orders-api", ((BicepOutputReference)web.Resource.Parameters["orders_apiClientId_2"]!).Resource.Name);
    }

    [Fact]
    public void Symbols_TheSecondDeclarationDoesNotCollideWithAParameterFromTheAppHost()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // The second declaration of an API registration is the resource "applicationIdentity" unless the template already has that name.
        var api = builder.AddEntraIdApplication("api")
            .WithRedirectUris(builder.AddParameter("applicationIdentity"))
            .WithScope("read", "Read");

        var bicep = api.Resource.GetBicepTemplateString();

        AssertSymbolsAreUniqueValidIdentifiers(bicep);
        Assert.Contains("param applicationIdentity string", bicep);
        Assert.Contains("resource applicationIdentity_2 'Microsoft.Graph/applications@v1.0'", bicep);
    }

    [Fact]
    public void Symbols_SameRegistrationAsApiAndPreAuthorizedClientSharesOneParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var shared = builder.AddExistingEntraApplication("shared", builder.AddParameter("shared-tenant-id"), builder.AddParameter("shared-client-id"))
            .WithApplicationIdUri("api://shared")
            .WithScope("read", "Read", scope => scope.Id = Guid.NewGuid());
        var registration = builder.AddEntraIdApplication("registration")
            .WithScope("read", "Read")
            .WithPreAuthorizedApplication(shared, "read")
            .WithApiPermissions(shared, "read");

        var bicep = registration.Resource.GetBicepTemplateString();

        // The registration needs the same client ID as an API it calls and as a client it pre-authorizes: one parameter, one binding.
        Assert.Single(bicep.Split('\n'), line => line.StartsWith("param shared", StringComparison.Ordinal));
        Assert.Single(registration.Resource.Parameters, p => p.Key.StartsWith("shared", StringComparison.Ordinal));
        AssertSymbolsAreUniqueValidIdentifiers(bicep);
    }
    private static void AssertSymbolsAreUniqueValidIdentifiers(string bicep)
    {
        // Parameters and resources share one case-insensitive namespace in a template.
        var symbols = System.Text.RegularExpressions.Regex.Matches(bicep, @"^(?:param|resource) (\S+)", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(symbols);
        Assert.Equal(symbols.Length, symbols.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(symbols, symbol => Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", symbol));
    }

    private static (IResourceBuilder<EntraIdApplicationResource> First, IResourceBuilder<EntraIdApplicationResource> Second) AddTwoClientsOfOneApi(IDistributedApplicationBuilder builder)
    {
        var api = builder.AddExistingEntraApplication("apiservice-entra", TenantId, ClientId)
            .WithApplicationIdUri(ApiUri)
            .WithScope("read", "Read", scope => scope.Id = Guid.NewGuid())
            .WithScope("write", "Write", scope => scope.Id = Guid.NewGuid());
        builder.AddResource(new ProjectResource("apiservice")).WithReference(api);

        var first = builder.AddExistingEntraApplication("web-a", TenantId, ClientId).WithApiPermissions(api, "read", "write");
        var second = builder.AddExistingEntraApplication("web-b", TenantId, ClientId).WithApiPermissions(api, "read");
        return (first, second);
    }

    [Fact]
    public async Task WithReference_RejectsTwoRegistrationsThatWriteTheSameDownstreamApisKeys()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var (first, second) = AddTwoClientsOfOneApi(builder);

        // For a .NET program the DownstreamApis section doesn't depend on the connection name, so both would write
        // DownstreamApis__apiservice__Scopes__0, and web-a's Scopes__1 would survive in web-b's configuration.
        var frontend = builder.AddResource(new ProjectResource("frontend"))
            .WithReference(first, "AzureAdA")
            .WithReference(second, "AzureAdB");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(frontend.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));

        Assert.Equal(
            "'frontend' references the Entra ID applications 'web-a' and 'web-b', and both declare permissions to an API that is configured as " +
            "the DownstreamApis entry 'apiservice' under 'DownstreamApis', so their scopes would be mixed. Pass a different downstreamApisPrefix to WithReference for one of them.",
            exception.Message);
        Assert.Equal(exception.Message, Assert.Throws<InvalidOperationException>(first.Resource.ValidateApiAccess).Message);
    }

    [Fact]
    public async Task WithReference_RejectsReferencesWhoseEncodedPrefixesAreTheSame()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var (first, second) = AddTwoClientsOfOneApi(builder);

        // A non-.NET resource receives upper snake case names, so the prefixes "A"/"B-C" and "A_B"/"C" differ as written but
        // both produce A_B_C_APISERVICE_SCOPES_0.
        var worker = builder.AddContainer("worker", "myimage")
            .WithReference(first, "A", "B-C")
            .WithReference(second, "A_B", "C");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(worker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));

        Assert.Contains("downstreamApisPrefix", exception.Message);
    }

    private static IResourceBuilder<EntraIdApplicationResource> AddClientOfTwoApisNamedAfterTheirConsumers(IDistributedApplicationBuilder builder)
    {
        var firstApi = builder.AddExistingEntraApplication("first-api", TenantId, ClientId)
            .WithApplicationIdUri("api://first")
            .WithScope("read", "Read", scope => scope.Id = Guid.NewGuid());
        var secondApi = builder.AddExistingEntraApplication("second-api", TenantId, ClientId)
            .WithApplicationIdUri("api://second")
            .WithScope("read", "Read", scope => scope.Id = Guid.NewGuid());

        // The entries are named after the resources that reference the APIs. As configuration keys they are different, but as
        // environment variables of a non-.NET resource they are both ORDERS_API.
        builder.AddResource(new ProjectResource("orders-api")).WithReference(firstApi);
        builder.AddResource(new ProjectResource("ordersApi")).WithReference(secondApi);
        return builder.AddExistingEntraApplication("web", TenantId, ClientId)
            .WithApiPermissions(firstApi, "read")
            .WithApiPermissions(secondApi, "read");
    }

    [Fact]
    public async Task WithReference_KeepsEntriesApartWhoseNamesOnlyDifferInCaseOrPunctuationForDotnetResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = AddClientOfTwoApisNamedAfterTheirConsumers(builder);
        var frontend = builder.AddResource(new ProjectResource("frontend")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(frontend.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("api://first/read", env["DownstreamApis__orders-api__Scopes__0"]);
        Assert.Equal("api://second/read", env["DownstreamApis__ordersApi__Scopes__0"]);
    }

    [Fact]
    public async Task WithReference_RejectsEntriesWhoseEncodedNamesAreTheSameForNonDotnetResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = AddClientOfTwoApisNamedAfterTheirConsumers(builder);
        var worker = builder.AddContainer("worker", "myimage").WithReference(web);

        // The environment would otherwise be built from duplicate keys.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(worker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));

        Assert.Contains("'orders-api'", exception.Message);
        Assert.Contains("'ordersApi'", exception.Message);
    }

    [Fact]
    public async Task WithReference_ADifferentDownstreamApisPrefixKeepsTheTwoRegistrationsApart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var (first, second) = AddTwoClientsOfOneApi(builder);
        var frontend = builder.AddResource(new ProjectResource("frontend"))
            .WithReference(first, "AzureAdA")
            .WithReference(second, "AzureAdB", downstreamApisPrefix: "DownstreamApisB");

        first.Resource.ValidateApiAccess();
        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(frontend.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"{ApiUri}/read", env["DownstreamApis__apiservice__Scopes__0"]);
        Assert.Equal($"{ApiUri}/write", env["DownstreamApis__apiservice__Scopes__1"]);
        Assert.Equal($"{ApiUri}/read", env["DownstreamApisB__apiservice__Scopes__0"]);
        Assert.DoesNotContain("DownstreamApisB__apiservice__Scopes__1", env.Keys);
    }

    [Fact]
    public async Task WithReference_RegistrationsOnANonDotnetResourceUseTheirOwnKeys()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var (first, second) = AddTwoClientsOfOneApi(builder);

        // Other languages prefix the entries with the connection name, so each reference has its own keys.
        var worker = builder.AddContainer("worker", "myimage").WithReference(first, "A").WithReference(second, "B");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(worker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"{ApiUri}/write", env["A_DOWNSTREAM_APIS_APISERVICE_SCOPES_1"]);
        Assert.DoesNotContain("B_DOWNSTREAM_APIS_APISERVICE_SCOPES_1", env.Keys);
    }

    [Fact]
    public async Task WithReference_RegistrationsOnANonDotnetResourceWithTheSameConnectionNameClash()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var (first, second) = AddTwoClientsOfOneApi(builder);

        // The same connection name for both makes the keys the same again.
        var worker = builder.AddContainer("worker", "myimage").WithReference(first, "A").WithReference(second, "A");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(worker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));
    }
    [Fact]
    public async Task WithReference_ReferencingOneRegistrationTwiceIsNotAClash()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var (first, _) = AddTwoClientsOfOneApi(builder);
        var frontend = builder.AddResource(new ProjectResource("frontend"))
            .WithReference(first)
            .WithReference(first, "AzureAdAgain");

        first.Resource.ValidateApiAccess();
        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(frontend.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"{ApiUri}/write", env["DownstreamApis__apiservice__Scopes__1"]);
    }

    [Fact]
    public async Task BeforeStart_FailsWhenAResourceReferencesTwoRegistrationsThatShareDownstreamApisKeys()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var (first, second) = AddTwoClientsOfOneApi(builder);
        builder.AddResource(new ProjectResource("frontend")).WithReference(first, "AzureAdA").WithReference(second, "AzureAdB");

        using var app = builder.Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("downstreamApisPrefix", exception.ToString());
    }

    [Fact]
    public void ScopeId_ChangedAfterDeclaringThePermissionIsUsedByBothTheCallerAndTheApi()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithScope("other", "Another scope.");
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user")
            .WithAdminConsent(api, "access_as_user");
        var derivedId = api.Resource.GetScopeId(api.Resource.Scopes[0]).ToString("D");

        // Adopting an existing scope by its ID is a later call than the declaration in an AppHost that configures in several places.
        var adoptedId = Guid.Parse("11112222-aaaa-3333-bbbb-4444cccc5555");
        api.Resource.Scopes[0].Id = adoptedId;

        var apiBicep = api.Resource.GetBicepTemplateString();
        var webBicep = web.Resource.GetBicepTemplateString();

        // The caller used to keep the ID that was derived when it declared the permission, so it would request a scope that doesn't exist.
        Assert.Contains("id: '11112222-aaaa-3333-bbbb-4444cccc5555'", apiBicep);
        Assert.Contains("id: '11112222-aaaa-3333-bbbb-4444cccc5555'", webBicep);
        Assert.DoesNotContain(derivedId, apiBicep);
        Assert.DoesNotContain(derivedId, webBicep);
    }

    [Fact]
    public void ScopeId_ChangedAfterDeclaringThePermissionIsUsedForAnExistingApi()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var firstId = Guid.Parse("11112222-aaaa-3333-bbbb-4444cccc5555");
        var secondId = Guid.Parse("22223333-aaaa-4444-bbbb-5555cccc6666");
        var existingApi = builder.AddExistingEntraApplication("existing-api", TenantId, ClientId)
            .WithApplicationIdUri("api://contoso-orders")
            .WithScope("orders.read", "Read orders.", scope => scope.Id = firstId);
        var web = builder.AddEntraIdApplication("entra-web").WithApiPermissions(existingApi, "orders.read");

        existingApi.Resource.Scopes[0].Id = secondId;

        var webBicep = web.Resource.GetBicepTemplateString();
        Assert.Contains($"id: '{secondId:D}'", webBicep);
        Assert.DoesNotContain(firstId.ToString("D"), webBicep);
    }

    [Fact]
    public void ScopeId_AnExistingScopeThatLosesItsIdIsReportedWhenTheTemplateIsGenerated()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingApi = builder.AddExistingEntraApplication("existing-api", TenantId, ClientId)
            .WithApplicationIdUri("api://contoso-orders")
            .WithScope("orders.read", "Read orders.", scope => scope.Id = Guid.NewGuid());
        var web = builder.AddEntraIdApplication("entra-web").WithApiPermissions(existingApi, "orders.read");

        existingApi.Resource.Scopes[0].Id = null;

        var exception = Assert.Throws<InvalidOperationException>(web.Resource.GetBicepTemplateString);
        Assert.Contains("EntraIdScope.Id", exception.Message);
    }

    [Fact]
    public void ScopeId_DeclaringTheSameScopeTwiceStaysOnePermission()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user")
            .WithApiPermissions(api, "access_as_user")
            .WithAdminConsent(api, "access_as_user")
            .WithAdminConsent(api, "access_as_user");

        var access = Assert.Single(web.Resource.ApiAccess);
        Assert.Single(access.Declared);
        Assert.Single(access.Consented);
    }
}