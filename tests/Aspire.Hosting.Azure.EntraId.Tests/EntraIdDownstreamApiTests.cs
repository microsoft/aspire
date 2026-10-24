// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Security.Claims;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdDownstreamApiTests
{
    private const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    private const string WebClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";
    private const string ApiClientId = "99998888-aaaa-2222-bbbb-3333cccc4444";

    [Fact]
    public async Task WithDownstreamApi_RunMode_DotNetProgram_UsesServiceDiscoveryAndQualifiedScopes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = AddApi(builder);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, api, scopes: ["access_as_user", "Files.Read"]);

        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Run);

        // Only the downstream API and the service discovery settings are added. The API's own registration settings
        // aren't copied to the caller.
        Assert.Equal(new Dictionary<string, string>
        {
            ["DownstreamApis__api__BaseUrl"] = "https+http://api",
            ["DownstreamApis__api__Scopes__0"] = $"api://{ApiClientId}/access_as_user",
            ["DownstreamApis__api__Scopes__1"] = $"api://{ApiClientId}/Files.Read",
            ["services__api__https__0"] = "https://localhost:7081"
        }, env);
    }

    [Fact]
    public async Task WithDownstreamApi_PublishMode_DotNetProgram_UsesTheEndpointInsteadOfServiceDiscovery()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = AddApi(builder);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, api, scopes: ["access_as_user"]);

        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal(new Dictionary<string, string>
        {
            ["DownstreamApis__api__BaseUrl"] = "{api.bindings.https.url}",
            ["DownstreamApis__api__Scopes__0"] = $"api://{ApiClientId}/access_as_user"
        }, env);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, "https://localhost:7081")]
    [InlineData(DistributedApplicationOperation.Publish, "{api.bindings.https.url}")]
    public async Task WithDownstreamApi_NonDotNetResource_UsesSnakeCaseNamesAndTheEndpoint(
        DistributedApplicationOperation operation,
        string expectedBaseUrl)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = AddApi(builder);
        var node = builder.AddExecutable("node", "node", ".")
            .WithDownstreamApi(apiRegistration, api, scopes: ["access_as_user"]);

        var env = await GetEnvironmentAsync(node.Resource, operation);

        Assert.Equal(new Dictionary<string, string>
        {
            ["DOWNSTREAM_APIS_API_BASE_URL"] = expectedBaseUrl,
            ["DOWNSTREAM_APIS_API_SCOPES_0"] = $"api://{ApiClientId}/access_as_user"
        }, env);
    }

    [Fact]
    public async Task WithDownstreamApi_PrefersHttpsEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = builder.AddResource(new ProjectResource("api"))
            .WithHttpEndpoint(targetPort: 8081, name: "http")
            .WithHttpsEndpoint(targetPort: 8080, name: "https");
        var node = builder.AddExecutable("node", "node", ".")
            .WithDownstreamApi(apiRegistration, api, scopes: ["access_as_user"]);

        var env = await GetEnvironmentAsync(node.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal("{api.bindings.https.url}", env["DOWNSTREAM_APIS_API_BASE_URL"]);
    }

    [Fact]
    public async Task WithDownstreamApi_FallsBackToHttpEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = builder.AddResource(new ProjectResource("api")).WithHttpEndpoint(targetPort: 8081, name: "http");
        var node = builder.AddExecutable("node", "node", ".")
            .WithDownstreamApi(apiRegistration, api, scopes: ["access_as_user"]);

        var env = await GetEnvironmentAsync(node.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal("{api.bindings.http.url}", env["DOWNSTREAM_APIS_API_BASE_URL"]);
    }

    [Fact]
    public async Task WithDownstreamApi_ServiceWithoutHttpEndpoint_FailsWhenResolved()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = builder.AddResource(new ProjectResource("api"));
        var node = builder.AddExecutable("node", "node", ".")
            .WithDownstreamApi(apiRegistration, api, scopes: ["access_as_user"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetEnvironmentAsync(node.Resource, DistributedApplicationOperation.Publish));

        Assert.Contains("'api' has no HTTP or HTTPS endpoint", exception.Message);
    }

    [Fact]
    public async Task WithDownstreamApi_CustomName_SetsTheEntryName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = AddApi(builder);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, api, scopes: ["access_as_user"], name: "WeatherApi");

        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Run);

        // The entry name is what the application passes to IDownstreamApi. Service discovery still uses the service name.
        Assert.Equal("https+http://api", env["DownstreamApis__WeatherApi__BaseUrl"]);
        Assert.Equal($"api://{ApiClientId}/access_as_user", env["DownstreamApis__WeatherApi__Scopes__0"]);
        Assert.Equal("https://localhost:7081", env["services__api__https__0"]);
    }

    [Fact]
    public async Task WithDownstreamApi_MultipleApis_AreConfiguredIndependently()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var weatherRegistration = builder.AddExistingEntraApplication("entra-weather", TenantId, ApiClientId);
        var ordersRegistration = builder.AddExistingEntraApplication("entra-orders", TenantId, WebClientId)
            .WithApplicationIdUri("https://contoso.example/orders");
        var weather = AddApi(builder, "weather", 7081);
        var orders = AddApi(builder, "orders", 7082);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(weatherRegistration, weather, scopes: ["forecast.read"])
            .WithDownstreamApi(ordersRegistration, orders, scopes: ["orders.write"]);

        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Run);

        Assert.Equal("https+http://weather", env["DownstreamApis__weather__BaseUrl"]);
        Assert.Equal($"api://{ApiClientId}/forecast.read", env["DownstreamApis__weather__Scopes__0"]);
        Assert.Equal("https+http://orders", env["DownstreamApis__orders__BaseUrl"]);
        Assert.Equal("https://contoso.example/orders/orders.write", env["DownstreamApis__orders__Scopes__0"]);
    }

    [Fact]
    public async Task WithDownstreamApi_DoesNotEmitTheApiRegistrationSettingsOrUnrelatedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var webRegistration = builder.AddExistingEntraApplication("entra-web", TenantId, WebClientId);
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = AddApi(builder);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithReference(webRegistration)
            .WithDownstreamApi(apiRegistration, api, scopes: ["access_as_user"]);

        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Run);

        // The caller's identity comes from its own WithReference. The API registration's client ID appears only in scopes.
        Assert.Equal(WebClientId, env["AzureAd__ClientId"]);
        Assert.DoesNotContain(env, entry => entry.Key.StartsWith("AzureAd__", StringComparison.Ordinal) && entry.Value == ApiClientId);
        Assert.DoesNotContain(env, entry => entry.Key.Contains("RequestAppToken", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("access_as_user", "api://99998888-aaaa-2222-bbbb-3333cccc4444/access_as_user")]
    [InlineData(".default", "api://99998888-aaaa-2222-bbbb-3333cccc4444/.default")]
    [InlineData("api://99998888-aaaa-2222-bbbb-3333cccc4444/access_as_user", "api://99998888-aaaa-2222-bbbb-3333cccc4444/access_as_user")]
    [InlineData("api://99998888-aaaa-2222-bbbb-3333cccc4444/.default", "api://99998888-aaaa-2222-bbbb-3333cccc4444/.default")]
    [InlineData("https://graph.microsoft.com/User.Read", "https://graph.microsoft.com/User.Read")]
    [InlineData("openid", "openid")]
    [InlineData("offline_access", "offline_access")]
    public async Task WithDownstreamApi_Scopes_AreQualifiedOnlyWhenTheyHaveNoResource(string scope, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: [scope]);

        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Run);

        Assert.Equal(expected, env["DownstreamApis__api__Scopes__0"]);
    }

    [Fact]
    public async Task WithDownstreamApi_DefaultScope_DoesNotRequestAnAppToken()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: [".default"]);

        var configuration = await GetConfigurationAsync(web.Resource, DistributedApplicationOperation.Run);
        var options = configuration.GetSection("DownstreamApis:api").Get<DownstreamApiOptions>(binder => binder.ErrorOnUnknownConfiguration = true);

        // ".default" alone is a delegated request for the permissions already granted to the user. Only the application
        // can ask for an app-only token, by setting RequestAppToken, which this method never does.
        Assert.NotNull(options);
        Assert.False(options.RequestAppToken);
        Assert.Equal([$"api://{ApiClientId}/.default"], options.Scopes);
    }

    [Theory]
    [InlineData("api://custom-api", "api://custom-api/access_as_user")]
    [InlineData("api://custom-api/", "api://custom-api/access_as_user")]
    [InlineData("https://contoso.onmicrosoft.com/my-api", "https://contoso.onmicrosoft.com/my-api/access_as_user")]
    public async Task WithDownstreamApi_ExplicitApplicationIdUri_QualifiesScopes(string applicationIdUri, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId)
            .WithApplicationIdUri(applicationIdUri);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user"]);

        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Run);

        Assert.Equal(expected, env["DownstreamApis__api__Scopes__0"]);
    }

    [Fact]
    public async Task WithDownstreamApi_CreatedRegistrationWithScope_PublishUsesTheProvisionedApplicationIdUri()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apiRegistration = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", "Access the API as the signed-in user.");
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user"]);

        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Publish);

        // The registration's own application ID URI is used, so a custom URI or default set during provisioning is honored.
        Assert.Equal("{entra-api.outputs.applicationIdUri}/access_as_user", env["DownstreamApis__api__Scopes__0"]);
    }

    [Fact]
    public async Task WithDownstreamApi_CreatedRegistrationWithCustomApplicationIdUri_UsesTheCustomUri()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apiRegistration = builder.AddEntraIdApplication("entra-api")
            .WithApplicationIdUri("api://contoso-orders");
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user"]);

        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal("api://contoso-orders/access_as_user", env["DownstreamApis__api__Scopes__0"]);
    }
    [Theory]
    [InlineData(DistributedApplicationOperation.Run, $"api://{ApiClientId}/access_as_user")]
    [InlineData(DistributedApplicationOperation.Publish, "api://{entra-api-client-id.value}/access_as_user")]
    public async Task WithDownstreamApi_ClientIdParameter_StaysAReference(
        DistributedApplicationOperation operation,
        string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var tenant = builder.AddParameter("entra-tenant-id", TenantId);
        var clientId = builder.AddParameter("entra-api-client-id", ApiClientId);
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", tenant, clientId);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user"]);

        var env = await GetEnvironmentAsync(web.Resource, operation);

        Assert.Equal(expected, env["DownstreamApis__api__Scopes__0"]);
    }

    [Fact]
    public async Task WithDownstreamApi_CreatedRegistration_PublishUsesTheOutputReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apiRegistration = builder.AddEntraIdApplication("entra-api");
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user"]);

        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal("api://{entra-api.outputs.clientId}/access_as_user", env["DownstreamApis__api__Scopes__0"]);
    }

    [Fact]
    public async Task WithDownstreamApi_CreatedRegistration_RunWaitsForTheProvisionedClientId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddEntraIdApplication("entra-api");
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user"]);

        using var app = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var environmentTask = EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            web.Resource, DistributedApplicationOperation.Run, app.Services).AsTask();

        // The scope must not be built from an empty client ID before provisioning finishes.
        Assert.False(environmentTask.IsCompleted);
        apiRegistration.Resource.Outputs["clientId"] = ApiClientId;
        apiRegistration.Resource.ProvisioningTaskCompletionSource!.SetResult();

        var env = await environmentTask.WaitAsync(cts.Token);
        Assert.Equal($"api://{ApiClientId}/access_as_user", env["DownstreamApis__api__Scopes__0"]);
    }

    [Fact]
    public async Task WithDownstreamApi_CreatedRegistration_ApplicationIdUriDoesNotNeedProvisioning()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddEntraIdApplication("entra-api")
            .WithApplicationIdUri("api://my-api");
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var env = await GetEnvironmentAsync(web.Resource, DistributedApplicationOperation.Run).WaitAsync(cts.Token);

        Assert.Equal("api://my-api/access_as_user", env["DownstreamApis__api__Scopes__0"]);
    }

    [Fact]
    public void WithDownstreamApi_AddsReferenceRelationships()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = AddApi(builder);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, api, scopes: ["access_as_user"]);

        var relationships = web.Resource.Annotations.OfType<ResourceRelationshipAnnotation>().ToList();
        Assert.Contains(relationships, r => r.Type == "Reference" && ReferenceEquals(r.Resource, apiRegistration.Resource));
        Assert.Contains(relationships, r => r.Type == "Reference" && ReferenceEquals(r.Resource, api.Resource));
    }

    [Fact]
    public void WithDownstreamApi_DoesNotAddImplicitWaits()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddEntraIdApplication("entra-api");
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user"]);

        Assert.Empty(web.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public void WithDownstreamApi_ReturnsTheSameBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var web = builder.AddResource(new ProjectResource("web"));

        Assert.Same(web, web.WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user"]));
    }

    [Fact]
    public void WithDownstreamApi_ValidatesArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = AddApi(builder);
        var web = builder.AddResource(new ProjectResource("web"));

        Assert.Throws<ArgumentNullException>("builder", () => EntraIdResourceExtensions.WithDownstreamApi<ProjectResource>(null!, apiRegistration, api, ["a"]));
        Assert.Throws<ArgumentNullException>("apiRegistration", () => web.WithDownstreamApi(null!, api, ["a"]));
        Assert.Throws<ArgumentNullException>("apiService", () => web.WithDownstreamApi(apiRegistration, null!, ["a"]));
        Assert.Throws<ArgumentNullException>("scopes", () => web.WithDownstreamApi(apiRegistration, api, null!));
        Assert.Throws<ArgumentException>("scopes", () => web.WithDownstreamApi(apiRegistration, api, []));
        Assert.Throws<ArgumentException>("scopes", () => web.WithDownstreamApi(apiRegistration, api, [""]));
        Assert.Throws<ArgumentException>("scopes", () => web.WithDownstreamApi(apiRegistration, api, ["  "]));
        Assert.Throws<ArgumentException>("scopes", () => web.WithDownstreamApi(apiRegistration, api, ["a b"]));
        Assert.Throws<ArgumentException>("name", () => web.WithDownstreamApi(apiRegistration, api, ["a"], name: ""));
        Assert.Throws<ArgumentException>("name", () => web.WithDownstreamApi(apiRegistration, api, ["a"], name: "Weather:Api"));
    }

    [Fact]
    public void WithDownstreamApi_DuplicateName_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var api = AddApi(builder);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, api, scopes: ["access_as_user"]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => web.WithDownstreamApi(apiRegistration, api, scopes: ["other"]));

        Assert.Contains("already has a downstream API named 'api'", exception.Message);

        // A distinct name for the same service is allowed, for example to request different scopes.
        web.WithDownstreamApi(apiRegistration, api, scopes: ["other"], name: "api-other");
    }

    [Fact]
    public void CreateApplicationIdUriExpression_WithoutClientIdOrUri_Throws()
    {
        var resource = new EntraIdApplicationResource("entra-api");
        resource.SetExistingApplication();

        var exception = Assert.Throws<InvalidOperationException>(resource.CreateApplicationIdUriExpression);

        Assert.Contains("has no application ID URI or client ID", exception.Message);
    }

    [Fact]
    public async Task WithDownstreamApi_BindsToMicrosoftIdentityWebDownstreamApiOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user", "api://other/.default"]);

        var configuration = await GetConfigurationAsync(web.Resource, DistributedApplicationOperation.Run);

        // Strict binding catches a misspelled key instead of silently ignoring it.
        var options = configuration.GetSection("DownstreamApis:api")
            .Get<DownstreamApiOptions>(binder => binder.ErrorOnUnknownConfiguration = true);
        Assert.NotNull(options);
        Assert.Equal("https+http://api", options.BaseUrl);
        Assert.Equal([$"api://{ApiClientId}/access_as_user", "api://other/.default"], options.Scopes);
    }

    [Fact]
    public async Task WithDownstreamApi_MicrosoftIdentityWebNamedHttpClientResolvesServiceDiscovery()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiRegistration = builder.AddExistingEntraApplication("entra-api", TenantId, ApiClientId);
        var web = builder.AddResource(new ProjectResource("web"))
            .WithDownstreamApi(apiRegistration, AddApi(builder), scopes: ["access_as_user"]);
        var configuration = await GetConfigurationAsync(web.Resource, DistributedApplicationOperation.Run);

        var authorization = new RecordingAuthorizationHeaderProvider();
        var transport = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IAuthorizationHeaderProvider>(authorization);
        services.AddDownstreamApis(configuration.GetSection("DownstreamApis"));

        // The Aspire service defaults add service discovery to every HttpClient, including the named client that
        // Microsoft.Identity.Web creates for each downstream API.
        services.AddServiceDiscovery();
        services.ConfigureHttpClientDefaults(http =>
        {
            http.AddServiceDiscovery();
            http.ConfigurePrimaryHttpMessageHandler(() => transport);
        });
        using var provider = services.BuildServiceProvider();

        var downstreamApi = provider.GetRequiredService<IDownstreamApi>();
        using var response = await downstreamApi.CallApiForUserAsync("api", options => options.RelativePath = "weatherforecast");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(new Uri("https://localhost:7081/weatherforecast"), request);
        Assert.Equal([$"api://{ApiClientId}/access_as_user"], authorization.Scopes);
    }

    private static IResourceBuilder<ProjectResource> AddApi(IDistributedApplicationBuilder builder, string name = "api", int port = 7081)
        => builder.AddResource(new ProjectResource(name))
            .WithHttpsEndpoint(targetPort: 8080, name: "https")
            .WithEndpoint("https", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", port));

    private static async Task<Dictionary<string, string>> GetEnvironmentAsync(IResource resource, DistributedApplicationOperation operation)
        => await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource, operation, TestServiceProvider.Instance);

    private static async Task<IConfigurationRoot> GetConfigurationAsync(IResource resource, DistributedApplicationOperation operation)
    {
        var environment = await GetEnvironmentAsync(resource, operation);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(environment.Select(entry =>
                new KeyValuePair<string, string?>(entry.Key.Replace("__", ":", StringComparison.Ordinal), entry.Value)))
            .Build();
    }

    private sealed class RecordingAuthorizationHeaderProvider : IAuthorizationHeaderProvider
    {
        public IReadOnlyList<string> Scopes { get; private set; } = [];

        public Task<string> CreateAuthorizationHeaderForUserAsync(
            IEnumerable<string> scopes,
            AuthorizationHeaderProviderOptions? downstreamApiOptions = null,
            ClaimsPrincipal? claimsPrincipal = null,
            CancellationToken cancellationToken = default)
        {
            Scopes = [.. scopes];
            return Task.FromResult("Bearer test-token");
        }

        public Task<string> CreateAuthorizationHeaderForAppAsync(
            string scopes,
            AuthorizationHeaderProviderOptions? downstreamApiOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> CreateAuthorizationHeaderAsync(
            IEnumerable<string> scopes,
            AuthorizationHeaderProviderOptions? downstreamApiOptions = null,
            ClaimsPrincipal? claimsPrincipal = null,
            CancellationToken cancellationToken = default)
        {
            Scopes = [.. scopes];
            return Task.FromResult("Bearer test-token");
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
