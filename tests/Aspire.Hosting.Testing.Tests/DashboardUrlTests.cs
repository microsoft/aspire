// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.TestUtilities;
using Aspire.Templates.Tests;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Playwright;
using Xunit;
using TestingResources = Aspire.Hosting.Testing.Properties.Resources;

namespace Aspire.Hosting.Testing.Tests;

public class DashboardUrlTests
{
    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task GetDashboardUrlAsyncAuthenticatesDashboardBrowser()
    {
        await using var builder = await CreateDashboardBuilderAsync();
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var dashboardUri = await app.GetDashboardUrlAsync(cancellationTokenSource.Token);

        Assert.True(dashboardUri.IsAbsoluteUri);
        Assert.Equal(Uri.UriSchemeHttp, dashboardUri.Scheme);
        Assert.True(dashboardUri.IsLoopback);
        Assert.InRange(dashboardUri.Port, 1, 65535);
        Assert.Equal("/login", dashboardUri.AbsolutePath);
        Assert.StartsWith("?t=", dashboardUri.Query, StringComparison.Ordinal);
        Assert.True(dashboardUri.Query.Length > 3);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TestConstants.LongTimeoutTimeSpan
        };
        using var loginResponse = await httpClient.GetAsync(dashboardUri, cancellationTokenSource.Token);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/", loginResponse.Headers.Location?.OriginalString);
        Assert.Single(
            loginResponse.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith(".Aspire.Dashboard.Auth", StringComparison.Ordinal));

        using var protectedResponse = await httpClient.GetAsync(
            new Uri(dashboardUri.GetLeftPart(UriPartial.Authority) + "/structuredlogs"),
            cancellationTokenSource.Token);
        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task GetDashboardUrlAsyncUsesConfiguredLocalhostTld()
    {
        const string DashboardHost = "dashboard.testing.localhost";

        await using var builder = await CreateDashboardBuilderAsync();
        builder.Configuration["ASPNETCORE_URLS"] = $"http://{DashboardHost}:0";
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var dashboardUri = await app.GetDashboardUrlAsync(cancellationTokenSource.Token);

        Assert.Equal(DashboardHost, dashboardUri.Host);
        Assert.Equal("/login", dashboardUri.AbsolutePath);
        Assert.StartsWith("?t=", dashboardUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.Playwright)]
    public async Task DashboardDisplaysResourceUpdatesWhileAppHostIsPausedAtBreakpoint()
    {
        const string resourceName = "dashboard-test-resource";
        const string initialState = "Dashboard test initial";
        const string updatedState = "Dashboard test updated";

        using var breakpoint = TestingAppHostEntryPointProbe.CreateBreakpoint();
        await using var builder = await CreateDashboardBuilderAsync($"--entry-point-breakpoint-probe={breakpoint.Id}");
        builder.AddExternalService(resourceName, "https://example.com/");
        await using var app = await builder.BuildAsync();

        try
        {
            await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

            using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
            var cancellationToken = cancellationTokenSource.Token;

            // The AppHost signals this immediately after app.StartAsync() and then waits for Continue(). This models a
            // debugger stopped on the next AppHost statement while hosted services, child resources, and the dashboard run.
            await breakpoint.Reached.WaitAsync(cancellationToken);

            var runningResourceEvent = await app.ResourceNotifications.WaitForResourceAsync(
                resourceName,
                resourceEvent => resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running,
                cancellationToken);

            await app.ResourceNotifications.PublishUpdateAsync(
                runningResourceEvent.Resource,
                snapshot => snapshot with { State = initialState });

            // GetDashboardUrlAsync is a dashboard-health barrier, not a resource-propagation barrier. The resource service
            // caches notifications for dashboard clients, so a browser that connects now receives this state as initial data.
            var dashboardUri = await app.GetDashboardUrlAsync(cancellationToken);

            PlaywrightProvider.DetectAndSetInstalledPlaywrightDependenciesPath();
            Assertions.SetDefaultExpectTimeout(TestConstants.LongTimeoutDuration);
            var browser = await PlaywrightProvider.CreateBrowserAsync();
            try
            {
                await using var context = await browser.NewContextAsync();
                var page = await context.NewPageAsync();
                await page.GotoAsync(dashboardUri.AbsoluteUri);

                var resourceRow = page.Locator("tr.resource-row").Filter(new() { HasText = resourceName });
                var stateCell = resourceRow.Locator(".state-column-cell");

                await Assertions.Expect(resourceRow).ToBeVisibleAsync();
                await Assertions.Expect(stateCell).ToContainTextAsync(initialState);

                var updatedResourceTask = app.ResourceNotifications.WaitForResourceAsync(
                    resourceName,
                    resourceEvent => resourceEvent.Snapshot.State?.Text == updatedState,
                    cancellationToken);

                await app.ResourceNotifications.PublishUpdateAsync(
                    runningResourceEvent.Resource,
                    snapshot => snapshot with { State = updatedState });
                await updatedResourceTask;

                // PublishUpdateAsync updates the application notification stream while the AppHost breakpoint remains held.
                // Dashboard consumption and browser rendering are asynchronous, so wait separately for the rendered change.
                await Assertions.Expect(stateCell).ToContainTextAsync(updatedState);
            }
            finally
            {
                await browser.CloseAsync();
            }
        }
        finally
        {
            // Release AppHost execution before the application is disposed; otherwise its entry point cannot finish cleanup.
            breakpoint.Continue();
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task GetDashboardUrlAsyncEncodesCustomBrowserToken()
    {
        const string BrowserToken = "browser&token#with+reserved";
        await using var builder = await CreateDashboardBuilderAsync();
        builder.Configuration["AppHost:BrowserToken"] = BrowserToken;
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var dashboardUri = await app.GetDashboardUrlAsync(cancellationTokenSource.Token);

        Assert.Equal($"?t={Uri.EscapeDataString(BrowserToken)}", dashboardUri.Query);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TestConstants.LongTimeoutTimeSpan
        };
        using var loginResponse = await httpClient.GetAsync(dashboardUri, cancellationTokenSource.Token);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/", loginResponse.Headers.Location?.OriginalString);
        Assert.Single(
            loginResponse.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith(".Aspire.Dashboard.Auth", StringComparison.Ordinal));
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task DashboardRejectsWrongCrossApplicationAndBogusLoginTokens()
    {
        await using var firstBuilder = await CreateDashboardBuilderAsync();
        await using var secondBuilder = await CreateDashboardBuilderAsync();
        await using var firstApp = await firstBuilder.BuildAsync();
        await using var secondApp = await secondBuilder.BuildAsync();

        await Task.WhenAll(firstApp.StartAsync(), secondApp.StartAsync()).WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var firstUrl = await firstApp.GetDashboardUrlAsync(cancellationTokenSource.Token);
        var secondUrl = await secondApp.GetDashboardUrlAsync(cancellationTokenSource.Token);
        var firstBaseUrl = firstUrl.GetLeftPart(UriPartial.Authority);
        Uri[] invalidLoginUrls =
        [
            new($"{firstBaseUrl}/login?t=wrong-token"),
            new(firstBaseUrl + secondUrl.PathAndQuery),
            new($"{firstBaseUrl}/login?t=%25")
        ];

        Assert.NotEqual(firstUrl.Port, secondUrl.Port);
        Assert.NotEqual(firstUrl.Query, secondUrl.Query);

        foreach (var invalidLoginUrl in invalidLoginUrls)
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = true
            };
            using var httpClient = new HttpClient(handler)
            {
                Timeout = TestConstants.LongTimeoutTimeSpan
            };
            using var response = await httpClient.GetAsync(invalidLoginUrl, cancellationTokenSource.Token);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/login", response.Headers.Location?.OriginalString);
            Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task GetDashboardUrlAsyncThrowsWhenDashboardIsDisabled()
    {
        var builder = DistributedApplicationTestingBuilder.Create();
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.GetDashboardUrlAsync(default));

        Assert.Equal(TestingResources.DashboardDisabledExceptionMessage, exception.Message);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task GetDashboardUrlAsyncReturnsBaseUrlWhenDashboardAllowsAnonymousAccess()
    {
        await using var builder = await CreateDashboardBuilderAsync();
        builder.Configuration["AppHost:BrowserToken"] = string.Empty;
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var dashboardUri = await app.GetDashboardUrlAsync(cancellationTokenSource.Token);

        Assert.True(dashboardUri.IsAbsoluteUri);
        Assert.Equal(Uri.UriSchemeHttp, dashboardUri.Scheme);
        Assert.True(dashboardUri.IsLoopback);
        Assert.InRange(dashboardUri.Port, 1, 65535);
        Assert.Equal("/", dashboardUri.AbsolutePath);
        Assert.Equal(string.Empty, dashboardUri.Query);

        using var httpClient = new HttpClient { Timeout = TestConstants.LongTimeoutTimeSpan };
        using var response = await httpClient.GetAsync(dashboardUri, cancellationTokenSource.Token);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetDashboardUrlAsyncThrowsBeforeApplicationStarts()
    {
        await using var builder = await CreateDashboardBuilderAsync();
        await using var app = await builder.BuildAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.GetDashboardUrlAsync(default));

        Assert.Equal(TestingResources.DashboardUrlApplicationNotStartedExceptionMessage, exception.Message);
    }

    [Fact]
    public async Task GetDashboardUrlAsyncThrowsInPublishMode()
    {
        var builder = DistributedApplicationTestingBuilder.Create(["--publisher", "manifest"]);
        await using var app = await builder.BuildAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.GetDashboardUrlAsync(default));

        Assert.Equal(TestingResources.DashboardUrlPublishModeExceptionMessage, exception.Message);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task GetDashboardUrlAsyncPreservesTerminalDashboardFailure()
    {
        var missingDashboardPath = Path.Combine(
            AppContext.BaseDirectory,
            "missing-dashboard",
            Guid.NewGuid().ToString("N"));
        var builder = DistributedApplicationTestingBuilder.Create(
            CreateDashboardOptions(),
            [$"DcpPublisher:DashboardPath={missingDashboardPath}"]);
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.GetDashboardUrlAsync(cancellationTokenSource.Token));

        Assert.Contains(KnownResourceNames.AspireDashboard, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task DashboardStartupSummaryDoesNotWriteTheBrowserTokenToAppHostLogs()
    {
        await using var builder = await CreateDashboardBuilderAsync();
        var logCollector = new FakeLogCollector();
        builder.Services.AddLogging(logging => logging.AddProvider(new FakeLoggerProvider(logCollector)));

        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var dashboardUri = await app.GetDashboardUrlAsync(cancellationTokenSource.Token);
        var token = dashboardUri.Query["?t=".Length..];
        Assert.NotEmpty(token);

        // Resource log forwarding is asynchronous. Wait for the child dashboard's summary, which is written after
        // its separate login URL line, so the assertion covers both the AppHost and dashboard-process output.
        while (!logCollector.GetSnapshot().Any(record =>
            record.Category?.EndsWith(".Resources.aspire-dashboard", StringComparison.Ordinal) == true &&
            GetLogText(record).Contains("Aspire Dashboard", StringComparison.Ordinal)))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationTokenSource.Token);
        }

        var written = string.Join(
            Environment.NewLine,
            logCollector.GetSnapshot().Select(GetLogText));

        Assert.False(
            written.Contains(token, StringComparison.Ordinal),
            "The dashboard browser token was written to AppHost logs.");
        Assert.Contains("Aspire Dashboard", written, StringComparison.Ordinal);
    }

    private static string GetLogText(FakeLogRecord record)
    {
        return record.Message + " " + record.StructuredState?.Aggregate(
            string.Empty,
            (accumulated, pair) => accumulated + " " + pair.Value);
    }

    private static Task<IDistributedApplicationTestingBuilder> CreateDashboardBuilderAsync(params string[] args)
    {
        return DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
            CreateDashboardOptions(),
            args);
    }

    private static DistributedApplicationTestingBuilderOptions CreateDashboardOptions()
    {
        return new()
        {
            EnableDashboard = true
        };
    }
}
