// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Text;
using System.Text.Json;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Eventing;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class BrowserLogsBuilderExtensionsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void WithBrowserLogs_CreatesChildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var browserLogsResource = Assert.Single(appModel.Resources.OfType<BrowserLogsResource>());
        Assert.Equal("web-browser-logs", browserLogsResource.Name);
        Assert.Equal(web.Resource.Name, browserLogsResource.ParentResource.Name);
        Assert.Equal("chrome", browserLogsResource.Browser);
        Assert.Null(browserLogsResource.Profile);
        Assert.Contains(browserLogsResource.Annotations.OfType<NameValidationPolicyAnnotation>(), static annotation => annotation == NameValidationPolicyAnnotation.None);

        Assert.True(browserLogsResource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        var parentRelationship = Assert.Single(relationships, relationship => relationship.Type == "Parent");
        Assert.Equal(web.Resource.Name, parentRelationship.Resource.Name);

        var command = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName);
        Assert.Equal(CommandStrings.OpenTrackedBrowserName, command.DisplayName);
        Assert.Equal(CommandStrings.OpenTrackedBrowserDescription, command.DisplayDescription);

        var snapshot = browserLogsResource.Annotations.OfType<ResourceSnapshotAnnotation>().Single().InitialSnapshot;
        Assert.Equal(BrowserLogsBuilderExtensions.BrowserResourceType, snapshot.ResourceType);
        Assert.NotNull(snapshot.CreationTimeStamp);
        Assert.Contains(snapshot.Properties, property => property.Name == CustomResourceKnownProperties.Source && Equals(property.Value, "web"));
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.BrowserPropertyName && Equals(property.Value, "chrome"));
        Assert.DoesNotContain(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.ProfilePropertyName);
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName && Equals(property.Value, 0));
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.ActiveSessionsPropertyName && Equals(property.Value, "None"));
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.BrowserSessionsPropertyName && Equals(property.Value, "[]"));
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.TotalSessionsLaunchedPropertyName && Equals(property.Value, 0));
        Assert.Empty(snapshot.HealthReports);
    }

    [Fact]
    public void WithBrowserLogs_UsesResourceSpecificConfigurationWhenArgumentsAreOmitted()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "msedge";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = "Default";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:web:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:web:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = "Profile 1";

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        var browserLogsResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>());

        Assert.Equal("chrome", browserLogsResource.Browser);
        Assert.Equal("Profile 1", browserLogsResource.Profile);

        var snapshot = browserLogsResource.Annotations.OfType<ResourceSnapshotAnnotation>().Single().InitialSnapshot;
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.BrowserPropertyName && Equals(property.Value, "chrome"));
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.ProfilePropertyName && Equals(property.Value, "Profile 1"));
    }

    [Fact]
    public void GetDefaultBrowser_PrefersChromeWhenInstalled()
    {
        var browser = BrowserLogsBuilderExtensions.GetDefaultBrowser(browser =>
            browser switch
            {
                "chrome" => "/resolved/chrome",
                "msedge" => "/resolved/edge",
                _ => null
            });

        Assert.Equal("chrome", browser);
    }

    [Fact]
    public void GetDefaultBrowser_FallsBackToEdgeWhenChromeIsMissing()
    {
        var browser = BrowserLogsBuilderExtensions.GetDefaultBrowser(browser =>
            browser switch
            {
                "msedge" => "/resolved/edge",
                _ => null
            });

        Assert.Equal("msedge", browser);
    }

    [Fact]
    public void GetDefaultBrowser_FallsBackToChromeWhenKnownBrowsersAreMissing()
    {
        var browser = BrowserLogsBuilderExtensions.GetDefaultBrowser(static _ => null);

        Assert.Equal("chrome", browser);
    }

    [Fact]
    public void WithBrowserLogs_UsesDetectedDefaultBrowserWhenConfigurationIsMissing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        var browserLogsResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>());

        Assert.Equal(BrowserLogsBuilderExtensions.GetDefaultBrowser(BrowserLogsRunningSession.TryResolveBrowserExecutable), browserLogsResource.Browser);
        Assert.Null(browserLogsResource.Profile);
    }

    [Fact]
    public void WithBrowserLogs_ExplicitArgumentsOverrideConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = "Profile 1";

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "msedge", profile: "Default");

        using var app = builder.Build();
        var browserLogsResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>());

        Assert.Equal("msedge", browserLogsResource.Browser);
        Assert.Equal("Default", browserLogsResource.Profile);
    }

    [Fact]
    public async Task WithBrowserLogs_CommandStartsTrackedSession()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionManager = new FakeBrowserLogsSessionManager();
        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sessionManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

        Assert.True(result.Success);

        var call = Assert.Single(sessionManager.Calls);
        Assert.Same(browserLogsResource, call.Resource);
        Assert.Equal(browserLogsResource.Name, call.ResourceName);
        Assert.Equal("chrome", call.Settings.Browser);
        Assert.Null(call.Settings.Profile);
        Assert.Equal(new Uri("http://localhost:8080", UriKind.Absolute), call.Url);
    }

    [Fact]
    public async Task WithBrowserLogs_CommandUsesLatestConfiguredSettingsAndRefreshesProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = "Default";

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "msedge";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = null;

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

        Assert.True(result.Success);

        var launchSettings = Assert.Single(sessionFactory.Settings);
        Assert.Equal("msedge", launchSettings.Browser);
        Assert.Null(launchSettings.Profile);

        var runningEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserPropertyName, "msedge") &&
                !resourceEvent.Snapshot.Properties.Any(property => property.Name == BrowserLogsBuilderExtensions.ProfilePropertyName)).DefaultTimeout();

        var session = Assert.Single(GetBrowserSessions(runningEvent.Snapshot));
        Assert.Equal("msedge", session.Browser);
        Assert.Null(session.Profile);
    }

    [Fact]
    public async Task WithBrowserLogs_CommandRefreshesBrowserExecutablePropertyWhenRelaunchFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();

        var firstResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.True(firstResult.Success);

        await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, "/fake/browser-1")).DefaultTimeout();

        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var tempBrowserPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "tracked-browser.exe" : "tracked-browser");
            await File.WriteAllTextAsync(tempBrowserPath, string.Empty);

            builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = tempBrowserPath;
            sessionFactory.NextStartException = new InvalidOperationException("Launch failed.");

            var secondResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

            Assert.False(secondResult.Success);
            Assert.Equal("Launch failed.", secondResult.Message);

            var failedEvent = await app.ResourceNotifications.WaitForResourceAsync(
                browserLogsResource.Name,
                resourceEvent =>
                    resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                    HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserPropertyName, tempBrowserPath) &&
                    HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, tempBrowserPath) &&
                    HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 1)).DefaultTimeout();

            Assert.Collection(
                GetBrowserSessions(failedEvent.Snapshot),
                session =>
                {
                    Assert.Equal("session-0001", session.SessionId);
                    Assert.Equal("/fake/browser-1", session.BrowserExecutable);
                });
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WithBrowserLogs_CommandRemovesStaleBrowserExecutablePropertyWhenBrowserCannotBeResolved()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();

        var firstResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.True(firstResult.Success);

        await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, "/fake/browser-1")).DefaultTimeout();

        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "missing-browser";
        sessionFactory.NextStartException = new InvalidOperationException("Launch failed.");

        var secondResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

        Assert.False(secondResult.Success);

        var failedEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserPropertyName, "missing-browser") &&
                !resourceEvent.Snapshot.Properties.Any(property => property.Name == BrowserLogsBuilderExtensions.BrowserExecutablePropertyName) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 1)).DefaultTimeout();

        Assert.Collection(
            GetBrowserSessions(failedEvent.Snapshot),
            session =>
            {
                Assert.Equal("session-0001", session.SessionId);
                Assert.Equal("/fake/browser-1", session.BrowserExecutable);
            });
    }

    [Fact]
    public async Task WithBrowserLogs_CommandFailsWhenEndpointIsMissing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionManager = new FakeBrowserLogsSessionManager();
        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sessionManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("Resource 'web' does not have an HTTP or HTTPS endpoint. Browser logs require an endpoint to navigate to.", result.Message);
        Assert.Empty(sessionManager.Calls);
    }

    [Fact]
    public async Task WithBrowserLogs_CommandBecomesEnabledWhenParentReady()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = KnownResourceStates.NotStarted,
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var initialEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent => resourceEvent.Snapshot.Commands.Any(command =>
                command.Name == BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName &&
                command.State == ResourceCommandState.Disabled)).DefaultTimeout();

        Assert.Equal(ResourceCommandState.Disabled, initialEvent.Snapshot.Commands.Single(command => command.Name == BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).State);

        await app.ResourceNotifications.PublishUpdateAsync(web.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        await eventing.PublishAsync(new ResourceReadyEvent(web.Resource, app.Services)).DefaultTimeout();

        var enabledEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent => resourceEvent.Snapshot.Commands.Any(command =>
                command.Name == BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName &&
                command.State == ResourceCommandState.Enabled)).DefaultTimeout();

        Assert.Equal(ResourceCommandState.Enabled, enabledEvent.Snapshot.Commands.Single(command => command.Name == BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).State);
    }

    [Fact]
    public async Task WithBrowserLogs_CommandTracksMultipleSessionsWithUniqueIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome", profile: "Default");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();

        var firstResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.True(firstResult.Success);

        var firstSession = Assert.Single(sessionFactory.Sessions);
        Assert.Equal("session-0001", firstSession.SessionId);

        var firstRunningEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 1) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, "session-0001 (PID 1001)") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.TotalSessionsLaunchedPropertyName, 1) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastSessionPropertyName, "session-0001") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, "/fake/browser-1") &&
                resourceEvent.Snapshot.HealthReports.Any(report => report.Name == "session-0001" && report.Status == HealthStatus.Healthy)).DefaultTimeout();

        Assert.Single(firstRunningEvent.Snapshot.HealthReports);
        Assert.Collection(
            GetBrowserSessions(firstRunningEvent.Snapshot),
            session =>
            {
                Assert.Equal("session-0001", session.SessionId);
                Assert.Equal("chrome", session.Browser);
                Assert.Equal("/fake/browser-1", session.BrowserExecutable);
                Assert.Equal(1001, session.ProcessId);
                Assert.Equal("Default", session.Profile);
                Assert.Equal("http://localhost:8080/", session.TargetUrl);
                Assert.Equal("ws://127.0.0.1:9001/devtools/browser/browser-1", session.CdpEndpoint);
                Assert.Equal("ws://127.0.0.1:9001/devtools/page/target-1", session.PageCdpEndpoint);
                Assert.Equal("target-1", session.TargetId);
            });
        Assert.Equal(0, firstSession.StopCallCount);

        var secondResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.True(secondResult.Success);

        Assert.Equal(2, sessionFactory.Sessions.Count);
        var secondSession = sessionFactory.Sessions[1];
        Assert.Equal("session-0002", secondSession.SessionId);

        var secondRunningEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 2) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, "session-0001 (PID 1001), session-0002 (PID 1002)") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.TotalSessionsLaunchedPropertyName, 2) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastSessionPropertyName, "session-0002") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, "/fake/browser-2") &&
                resourceEvent.Snapshot.HealthReports.Any(report => report.Name == "session-0001" && report.Status == HealthStatus.Healthy) &&
                resourceEvent.Snapshot.HealthReports.Any(report => report.Name == "session-0002" && report.Status == HealthStatus.Healthy)).DefaultTimeout();

        Assert.Equal(2, secondRunningEvent.Snapshot.HealthReports.Length);
        Assert.Collection(
            GetBrowserSessions(secondRunningEvent.Snapshot),
            session =>
            {
                Assert.Equal("session-0001", session.SessionId);
                Assert.Equal("ws://127.0.0.1:9001/devtools/browser/browser-1", session.CdpEndpoint);
                Assert.Equal("ws://127.0.0.1:9001/devtools/page/target-1", session.PageCdpEndpoint);
            },
            session =>
            {
                Assert.Equal("session-0002", session.SessionId);
                Assert.Equal("ws://127.0.0.1:9002/devtools/browser/browser-2", session.CdpEndpoint);
                Assert.Equal("ws://127.0.0.1:9002/devtools/page/target-2", session.PageCdpEndpoint);
                Assert.Equal("target-2", session.TargetId);
            });
        Assert.Equal(0, firstSession.StopCallCount);

        await firstSession.CompleteAsync(exitCode: 0);

        var firstCompletedEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 1) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, "session-0002 (PID 1002)") &&
                resourceEvent.Snapshot.HealthReports.Length == 1 &&
                resourceEvent.Snapshot.HealthReports[0].Name == "session-0002").DefaultTimeout();

        Assert.Equal("session-0002", firstCompletedEvent.Snapshot.HealthReports[0].Name);
        Assert.Collection(
            GetBrowserSessions(firstCompletedEvent.Snapshot),
            session => Assert.Equal("session-0002", session.SessionId));

        await secondSession.CompleteAsync(exitCode: 0);

        var allCompletedEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Finished &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 0) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, "None") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.TotalSessionsLaunchedPropertyName, 2) &&
                resourceEvent.Snapshot.HealthReports.IsEmpty).DefaultTimeout();

        Assert.Equal(KnownResourceStates.Finished, allCompletedEvent.Snapshot.State?.Text);
        Assert.Empty(GetBrowserSessions(allCompletedEvent.Snapshot));
    }

    [Fact]
    public async Task WithBrowserLogs_DisposeWaitsForCompletionObservers()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        var app = builder.Build();
        var disposed = false;

        try
        {
            await app.StartAsync();

            var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
            var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
            Assert.True(result.Success);

            var session = Assert.Single(sessionFactory.Sessions);
            session.PauseCompletionObserver();

            var disposeTask = app.DisposeAsync().AsTask();

            await session.CompletionObserverStarted.DefaultTimeout();
            Assert.False(disposeTask.IsCompleted);

            session.ResumeCompletionObserver();
            await disposeTask.DefaultTimeout();
            disposed = true;
        }
        finally
        {
            if (!disposed)
            {
                await app.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task BrowserEventLogger_LogsSuccessfulNetworkRequests()
    {
        var resourceLoggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var resourceLogger = resourceLoggerService.GetLogger("web-browser-logs");
        var eventLogger = new BrowserEventLogger("session-0001", resourceLogger);
        var logs = await CaptureLogsAsync(resourceLoggerService, "web-browser-logs", () =>
        {
            eventLogger.HandleEvent(ParseProtocolEvent("""
                {
                  "method": "Network.requestWillBeSent",
                  "sessionId": "target-session-1",
                  "params": {
                    "requestId": "request-1",
                    "timestamp": 1.5,
                    "type": "Fetch",
                    "request": {
                      "url": "https://example.test/api/todos",
                      "method": "GET"
                    }
                  }
                }
                """));
            eventLogger.HandleEvent(ParseProtocolEvent("""
                {
                  "method": "Network.responseReceived",
                  "sessionId": "target-session-1",
                  "params": {
                    "requestId": "request-1",
                    "timestamp": 1.6,
                    "type": "Fetch",
                    "response": {
                      "url": "https://example.test/api/todos",
                      "status": 200,
                      "statusText": "OK",
                      "fromDiskCache": false,
                      "fromServiceWorker": false
                    }
                  }
                }
                """));
            eventLogger.HandleEvent(ParseProtocolEvent("""
                {
                  "method": "Network.loadingFinished",
                  "sessionId": "target-session-1",
                  "params": {
                    "requestId": "request-1",
                    "timestamp": 1.75,
                    "encodedDataLength": 1024
                  }
                }
                """));
        });
        var log = Assert.Single(logs);

        Assert.Equal("2000-12-29T20:59:59.0000000Z [session-0001] [network.fetch] GET https://example.test/api/todos -> 200 OK (250 ms, 1024 B)", log.Content);
    }

    [Fact]
    public async Task BrowserEventLogger_LogsFailedNetworkRequests()
    {
        var resourceLoggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var resourceLogger = resourceLoggerService.GetLogger("web-browser-logs");
        var eventLogger = new BrowserEventLogger("session-0002", resourceLogger);
        var logs = await CaptureLogsAsync(resourceLoggerService, "web-browser-logs", () =>
        {
            eventLogger.HandleEvent(ParseProtocolEvent("""
                {
                  "method": "Network.requestWillBeSent",
                  "sessionId": "target-session-2",
                  "params": {
                    "requestId": "request-2",
                    "timestamp": 5.0,
                    "type": "Document",
                    "request": {
                      "url": "https://127.0.0.1:1/browser-network-failure",
                      "method": "GET"
                    }
                  }
                }
                """));
            eventLogger.HandleEvent(ParseProtocolEvent("""
                {
                  "method": "Network.loadingFailed",
                  "sessionId": "target-session-2",
                  "params": {
                    "requestId": "request-2",
                    "timestamp": 5.15,
                    "errorText": "net::ERR_CONNECTION_REFUSED",
                    "canceled": false
                  }
                }
                """));
        });
        var log = Assert.Single(logs);

        Assert.Equal("2000-12-29T20:59:59.0000000Z [session-0002] [network.document] GET https://127.0.0.1:1/browser-network-failure failed: net::ERR_CONNECTION_REFUSED (150 ms)", log.Content);
    }

    private sealed class TestHttpResource(string name) : Resource(name), IResourceWithEndpoints;

    private static bool HasProperty(CustomResourceSnapshot snapshot, string name, object expectedValue) =>
        snapshot.Properties.Any(property => property.Name == name && Equals(property.Value, expectedValue));

    private static IReadOnlyList<BrowserSessionPropertyValue> GetBrowserSessions(CustomResourceSnapshot snapshot)
    {
        var property = snapshot.Properties.Single(property => property.Name == BrowserLogsBuilderExtensions.BrowserSessionsPropertyName);
        var value = Assert.IsType<string>(property.Value);
        return JsonSerializer.Deserialize<List<BrowserSessionPropertyValue>>(value, BrowserSessionPropertyJsonOptions)
            ?? throw new InvalidOperationException("Expected browser session property JSON.");
    }

    private static BrowserLogsProtocolEvent ParseProtocolEvent(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        return BrowserLogsProtocol.ParseEvent(BrowserLogsProtocol.ParseMessageHeader(payload), payload)
            ?? throw new InvalidOperationException("Expected a browser protocol event frame.");
    }

    private static Task<IReadOnlyList<LogLine>> CaptureLogsAsync(ResourceLoggerService resourceLoggerService, string resourceName, Action writeLogs) =>
        ConsoleLoggingTestHelpers.CaptureLogsAsync(resourceLoggerService, resourceName, targetLogCount: 1, writeLogs);

    private sealed class FakeBrowserLogsSessionManager : IBrowserLogsSessionManager
    {
        public List<SessionStartCall> Calls { get; } = [];

        public Task StartSessionAsync(BrowserLogsResource resource, BrowserLogsSettings settings, string resourceName, Uri url, CancellationToken cancellationToken)
        {
            Calls.Add(new SessionStartCall(resource, settings, resourceName, url));
            return Task.CompletedTask;
        }
    }

    private sealed record SessionStartCall(BrowserLogsResource Resource, BrowserLogsSettings Settings, string ResourceName, Uri Url);

    private sealed class FakeBrowserLogsRunningSessionFactory : IBrowserLogsRunningSessionFactory
    {
        public List<FakeBrowserLogsRunningSession> Sessions { get; } = [];
        public List<BrowserLogsSettings> Settings { get; } = [];
        public Exception? NextStartException { get; set; }

        public Task<IBrowserLogsRunningSession> StartSessionAsync(
            BrowserLogsSettings settings,
            string resourceName,
            Uri url,
            string sessionId,
            ILogger resourceLogger,
            CancellationToken cancellationToken)
        {
            Settings.Add(settings);

            if (NextStartException is { } exception)
            {
                NextStartException = null;
                return Task.FromException<IBrowserLogsRunningSession>(exception);
            }

            var session = new FakeBrowserLogsRunningSession(
                sessionId,
                $"/fake/browser-{Sessions.Count + 1}",
                processId: 1001 + Sessions.Count,
                startedAt: DateTime.UtcNow);

            Sessions.Add(session);

            return Task.FromResult<IBrowserLogsRunningSession>(session);
        }
    }

    private sealed class FakeBrowserLogsRunningSession(
        string sessionId,
        string browserExecutable,
        int processId,
        DateTime startedAt) : IBrowserLogsRunningSession
    {
        private TaskCompletionSource<object?> _completionObserverGate = CreateSignaledTaskCompletionSource();
        private readonly TaskCompletionSource<(int ExitCode, Exception? Error)> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _completionObserverTask;

        public string SessionId { get; } = sessionId;

        public string BrowserExecutable { get; } = browserExecutable;

        public Uri BrowserDebugEndpoint { get; } = new($"ws://127.0.0.1:{processId + 8000}/devtools/browser/browser-{processId - 1000}");

        public int ProcessId { get; } = processId;

        public DateTime StartedAt { get; } = startedAt;

        public string TargetId { get; } = $"target-{processId - 1000}";

        public int StopCallCount { get; private set; }

        public Task CompletionObserverStarted => CompletionObserverStartedSource.Task;

        private TaskCompletionSource<object?> CompletionObserverStartedSource { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartCompletionObserver(Func<int, Exception?, Task> onCompleted)
        {
            _completionObserverTask = ObserveCompletionAsync(onCompleted);
            return _completionObserverTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCallCount++;
            _completionSource.TrySetResult((0, null));
            return Task.CompletedTask;
        }

        public async Task CompleteAsync(int exitCode, Exception? error = null)
        {
            _completionSource.TrySetResult((exitCode, error));
            await (_completionObserverTask ?? Task.CompletedTask);
        }

        public void PauseCompletionObserver()
        {
            CompletionObserverStartedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _completionObserverGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ResumeCompletionObserver()
        {
            _completionObserverGate.TrySetResult(null);
        }

        private async Task ObserveCompletionAsync(Func<int, Exception?, Task> onCompleted)
        {
            var (exitCode, error) = await _completionSource.Task;
            CompletionObserverStartedSource.TrySetResult(null);
            await _completionObserverGate.Task;
            await onCompleted(exitCode, error);
        }

        private static TaskCompletionSource<object?> CreateSignaledTaskCompletionSource()
        {
            var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.TrySetResult(null);
            return source;
        }
    }

    private static JsonSerializerOptions BrowserSessionPropertyJsonOptions { get; } = new(JsonSerializerDefaults.Web);

    private sealed record BrowserSessionPropertyValue(
        string SessionId,
        string Browser,
        string BrowserExecutable,
        int ProcessId,
        string? Profile,
        DateTime StartedAt,
        string TargetUrl,
        string CdpEndpoint,
        string PageCdpEndpoint,
        string TargetId);
}

#pragma warning restore ASPIREBROWSERLOGS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
