// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdInitializationTests
{
    // Placeholder IDs from the Microsoft Learn documentation. They are well-formed but identify nothing.
    private const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    private const string ClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";

    [Fact]
    public async Task Initialize_ValidApplication_IsRunningWithLinksAndProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal(new UrlSnapshot[]
        {
            new("OpenID Config", $"https://login.microsoftonline.com/{TenantId}/v2.0/.well-known/openid-configuration", IsInternal: false)
            {
                DisplayProperties = new("OpenID configuration")
            },
            new("Azure Portal", $"https://portal.azure.com/#@{TenantId}/view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{ClientId}/isMSAApp~/false", IsInternal: false)
            {
                DisplayProperties = new("App registration")
            }
        }, snapshot.Urls);
        Assert.Equal(new ResourcePropertySnapshot[]
        {
            new("entra.tenant.id", TenantId) { DisplayName = "Tenant ID", IsHighlighted = true, SortOrder = 0 },
            new("entra.client.id", ClientId) { DisplayName = "Client ID", IsHighlighted = true, SortOrder = 1 },
            new("entra.signin.audience", "AzureADMyOrg") { DisplayName = "Sign-in audience", IsHighlighted = true, SortOrder = 2 },
            new("entra.instance", "https://login.microsoftonline.com/") { DisplayName = "Instance", IsHighlighted = true, SortOrder = 3 }
        }, snapshot.Properties);
    }

    [Fact]
    public async Task Initialize_WithCredentials_ListsTheirSourceTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithClientSecret(builder.AddParameter("EntraClientSecret", "super-secret", secret: true))
            .WithFicMsi();

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal(
            new ResourcePropertySnapshot("entra.credentials", "ClientSecret, SignedAssertionFromManagedIdentity")
            {
                DisplayName = "Credentials",
                IsHighlighted = true,
                SortOrder = 4
            },
            Assert.Single(snapshot.Properties, p => p.Name == "entra.credentials"));
    }

    [Fact]
    public async Task Initialize_OtherSignInAudience_LinksDiscoveryDocumentForKeyword()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs);

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal(new UrlSnapshot[]
        {
            new("OpenID Config", "https://login.microsoftonline.com/organizations/v2.0/.well-known/openid-configuration", IsInternal: false)
            {
                DisplayProperties = new("OpenID configuration")
            },
            new("Azure Portal", $"https://portal.azure.com/#@{TenantId}/view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{ClientId}/isMSAApp~/false", IsInternal: false)
            {
                DisplayProperties = new("App registration")
            }
        }, snapshot.Urls);
        Assert.Equal(
            new ResourcePropertySnapshot("entra.signin.audience", "AzureADMultipleOrgs")
            {
                DisplayName = "Sign-in audience",
                IsHighlighted = true,
                SortOrder = 2
            },
            Assert.Single(snapshot.Properties, p => p.Name == "entra.signin.audience"));
    }

    [Fact]
    public async Task Initialize_InstanceWithoutKnownPortal_OmitsPortalLink()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .WithInstance("https://login.contoso.example/");

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal(new UrlSnapshot[]
        {
            new("OpenID Config", $"https://login.contoso.example/{TenantId}/v2.0/.well-known/openid-configuration", IsInternal: false)
            {
                DisplayProperties = new("OpenID configuration")
            }
        }, snapshot.Urls);
    }

    [Fact]
    public async Task Initialize_SecretParameter_IsSensitiveProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(
                tenantId: builder.AddParameter("EntraTenantId", TenantId),
                clientId: builder.AddParameter("EntraClientId", ClientId, secret: true));

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal(new ResourcePropertySnapshot[]
        {
            new("entra.tenant.id", TenantId) { DisplayName = "Tenant ID", IsHighlighted = true, IsSensitive = false, SortOrder = 0 },
            new("entra.client.id", ClientId) { DisplayName = "Client ID", IsHighlighted = true, IsSensitive = true, SortOrder = 1 }
        }, snapshot.Properties.Take(2));
    }

    [Fact]
    public async Task Initialize_InvalidParameterValue_FailsWithoutLoggingTheValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(
                tenantId: builder.AddParameter("EntraTenantId", "secret-tenant-name", secret: true),
                clientId: builder.AddParameter("EntraClientId", ClientId));

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        Assert.Empty(snapshot.Urls);
        Assert.Empty(snapshot.Properties);
        Assert.Equal(new (string, bool)[]
        {
            ("Parameter 'EntraTenantId' does not contain a valid tenant ID. Expected the directory (tenant) ID shown on the app registration's Overview page, such as 'aaaabbbb-0000-cccc-1111-dddd2222eeee', or a domain name such as 'contoso.onmicrosoft.com'.", true)
        }, await ReadLogsAsync(app, entra.Resource));
    }

    [Fact]
    public async Task Initialize_TenantParameterIsSignInKeyword_ExplainsHowToChooseTheAudience()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(
                tenantId: builder.AddParameter("EntraTenantId", "organizations"),
                clientId: builder.AddParameter("EntraClientId", ClientId));

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        Assert.Equal(new (string, bool)[]
        {
            ("Parameter 'EntraTenantId' does not contain a valid tenant ID. The keywords 'organizations', 'common' and 'consumers' choose who can sign in; they do not identify a tenant. Use the ID of the tenant where the app is registered, and call WithSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs) instead.", true)
        }, await ReadLogsAsync(app, entra.Resource));
    }

    [Fact]
    public async Task Initialize_ParameterWithoutValue_FailsAndLogsTheException()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(
                tenantId: builder.AddParameter("EntraTenantId"),
                clientId: builder.AddParameter("EntraClientId", ClientId));

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        var log = Assert.Single(await ReadLogsAsync(app, entra.Resource));
        Assert.True(log.IsErrorMessage);
        Assert.StartsWith(
            $"Failed to get the value of the tenant ID parameter 'EntraTenantId'.\n{typeof(MissingParameterValueException).FullName}: ",
            log.Message);
    }

    [Fact]
    public async Task Initialize_WithoutExistingApplication_ReportsMissingConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api");

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        Assert.Equal(new (string, bool)[]
        {
            ("The app registration is not fully configured. Call AsExistingApplication with the registration's tenant ID and client ID.", true)
        }, await ReadLogsAsync(app, entra.Resource));
    }

    [Fact]
    public async Task Initialize_InvalidSettings_LogsEveryProblemBeforeFailing()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api")
            .WithCredential(new EntraIdStoreCertificateCredential { StorePath = "CurrentUser/My" });

        // The properties are public, so code that sets them directly bypasses the validation in the builder methods.
        entra.Resource.TenantId = "my-tenant";
        entra.Resource.ClientId = "my-client";
        entra.Resource.Instance = "http://login.microsoftonline.com/";
        entra.Resource.SignInAudience = (EntraIdSignInAudience)42;

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        Assert.Equal(new (string, bool)[]
        {
            ("The tenant ID 'my-tenant' is not valid. Expected the directory (tenant) ID shown on the app registration's Overview page, such as 'aaaabbbb-0000-cccc-1111-dddd2222eeee', or a domain name such as 'contoso.onmicrosoft.com'.", true),
            ("The client ID 'my-client' is not valid. Expected the application (client) ID shown on the app registration's Overview page, such as '00001111-aaaa-2222-bbbb-3333cccc4444'.", true),
            ("The instance 'http://login.microsoftonline.com/' is not valid. Expected an absolute HTTPS URL with no query string or fragment, such as 'https://login.microsoftonline.com/'.", true),
            ("The sign-in audience '42' is not a valid EntraIdSignInAudience value.", true),
            ("Client credential 0 is not valid. Either Thumbprint or DistinguishedName must be set on EntraIdStoreCertificateCredential.", true)
        }, await ReadLogsAsync(app, entra.Resource));
    }

    [Fact]
    public async Task Initialize_KeepsUrlsAndPropertiesPublishedEarlier()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId);

        using var app = builder.Build();

        // The orchestrator publishes URLs added with WithUrl and the parent relationship before raising the event.
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(entra.Resource, snapshot => snapshot with
        {
            Urls = [new UrlSnapshot("docs", "https://contoso.example/docs", IsInternal: false)],
            Properties = [new ResourcePropertySnapshot("custom", "value")]
        });

        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal(new[] { "docs", "OpenID Config", "Azure Portal" }, snapshot.Urls.Select(u => u.Name));
        Assert.Equal(
            new[] { "custom", "entra.tenant.id", "entra.client.id", "entra.signin.audience", "entra.instance" },
            snapshot.Properties.Select(p => p.Name));
    }

    [Fact]
    public async Task Initialize_RunsBeforeResourceStartedCallbacks()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        EntraIdApplicationResource? startedResource = null;
        var entra = builder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .OnBeforeResourceStarted((resource, _, _) =>
            {
                startedResource = resource;
                return Task.CompletedTask;
            });

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Same(entra.Resource, startedResource);
    }

    [Fact]
    public async Task Initialize_BeforeResourceStartedCallbackThrows_ReportsFailedToStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-api")
            .AsExistingApplication(tenantId: TenantId, clientId: ClientId)
            .OnBeforeResourceStarted((_, _, _) => throw new InvalidOperationException("The callback failed."));

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, entra.Resource);

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        Assert.Empty(snapshot.Urls);
        var log = Assert.Single(await ReadLogsAsync(app, entra.Resource));
        Assert.True(log.IsErrorMessage);
        Assert.StartsWith(
            $"A BeforeResourceStartedEvent subscriber failed.\n{typeof(InvalidOperationException).FullName}: The callback failed.",
            log.Message);
    }

    // The orchestrator raises InitializeResourceEvent when the app starts. Raising it directly runs the resource's
    // initialization without starting the orchestrator or DCP. The event's subscribers run one after another by
    // default, so initialization has finished when PublishAsync returns.
    private static async Task<CustomResourceSnapshot> InitializeAsync(DistributedApplication app, IResource resource)
    {
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();

        await eventing.PublishAsync(new InitializeResourceEvent(resource, eventing, loggerService, notifications, app.Services));

        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        return resourceEvent.Snapshot;
    }

    // Reads the lines logged to the resource's console. Each line has the form
    //   2026-10-02T18:34:22.1234567Z The tenant ID 'my-tenant' is not valid. Expected ...
    // The timestamp contains no spaces, so the message starts after the first space. A message logged with an exception
    // stays one line, with "\n" and the exception's ToString() appended to it.
    private static async Task<(string Message, bool IsErrorMessage)[]> ReadLogsAsync(DistributedApplication app, IResource resource)
    {
        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // A watch first returns everything logged so far as one batch, then waits for new lines. Only call this after
        // something was logged, because otherwise the first read waits until the timeout.
        await using var batches = loggerService.WatchAsync(resource).GetAsyncEnumerator(cts.Token);
        Assert.True(await batches.MoveNextAsync());

        return [.. batches.Current.Select(line => (line.Content[(line.Content.IndexOf(' ') + 1)..], line.IsErrorMessage))];
    }
}
