// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Diagnostics;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;
using TestingResources = Aspire.Hosting.Testing.Properties.Resources;

namespace Aspire.Hosting.Testing.Tests;

public class DashboardLoginUrlTests
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task GetDashboardLoginUrlAsyncAuthenticatesDashboardBrowser()
    {
        await using var builder = await CreateDashboardBuilderAsync();
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var dashboardUri = await app.GetDashboardLoginUrlAsync(cancellationTokenSource.Token);

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
    [RequiresFeature(TestFeature.Docker)]
    public async Task ConcurrentApplicationsUseDifferentDashboardLoginUrls()
    {
        await using var firstBuilder = await CreateDashboardBuilderAsync();
        await using var secondBuilder = await CreateDashboardBuilderAsync();
        await using var firstApp = await firstBuilder.BuildAsync();
        await using var secondApp = await secondBuilder.BuildAsync();

        await Task.WhenAll(firstApp.StartAsync(), secondApp.StartAsync()).WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var urls = await Task.WhenAll(
            firstApp.GetDashboardLoginUrlAsync(cancellationTokenSource.Token),
            secondApp.GetDashboardLoginUrlAsync(cancellationTokenSource.Token));

        Assert.Equal(Uri.UriSchemeHttp, urls[0].Scheme);
        Assert.Equal(Uri.UriSchemeHttp, urls[1].Scheme);
        Assert.NotEqual(urls[0].Port, urls[1].Port);
        Assert.NotEqual(urls[0].Query, urls[1].Query);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task DashboardRejectsWrongCrossApplicationAndBogusLoginTokens()
    {
        await using var firstBuilder = await CreateDashboardBuilderAsync();
        await using var secondBuilder = await CreateDashboardBuilderAsync();
        await using var firstApp = await firstBuilder.BuildAsync();
        await using var secondApp = await secondBuilder.BuildAsync();

        await Task.WhenAll(firstApp.StartAsync(), secondApp.StartAsync()).WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var firstUrl = await firstApp.GetDashboardLoginUrlAsync(cancellationTokenSource.Token);
        var secondUrl = await secondApp.GetDashboardLoginUrlAsync(cancellationTokenSource.Token);
        var firstBaseUrl = firstUrl.GetLeftPart(UriPartial.Authority);
        Uri[] invalidLoginUrls =
        [
            new($"{firstBaseUrl}/login?t=wrong-token"),
            new(firstBaseUrl + secondUrl.PathAndQuery),
            new($"{firstBaseUrl}/login?t=%25")
        ];

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
    [RequiresFeature(TestFeature.Docker)]
    public async Task GetDashboardLoginUrlAsyncThrowsWhenDashboardIsDisabled()
    {
        var builder = DistributedApplicationTestingBuilder.Create();
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.GetDashboardLoginUrlAsync(default));

        Assert.Equal(TestingResources.DashboardDisabledExceptionMessage, exception.Message);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task GetDashboardLoginUrlAsyncThrowsWhenDashboardAllowsAnonymousAccess()
    {
        await using var builder = await CreateDashboardBuilderAsync();
        builder.Configuration["AppHost:BrowserToken"] = string.Empty;
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.GetDashboardLoginUrlAsync(default));

        Assert.Equal(TestingResources.DashboardLoginUrlAnonymousExceptionMessage, exception.Message);
    }

    [Fact]
    public async Task GetDashboardLoginUrlAsyncThrowsBeforeApplicationStarts()
    {
        await using var builder = await CreateDashboardBuilderAsync();
        await using var app = await builder.BuildAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.GetDashboardLoginUrlAsync(default));

        Assert.Equal(TestingResources.DashboardLoginUrlApplicationNotStartedExceptionMessage, exception.Message);
    }

    [Fact]
    public async Task GetDashboardLoginUrlAsyncHonorsCancellationAfterDashboardWaitStarts()
    {
        var (app, notificationLogger) = await CreateApplicationWithBlockedDashboardAsync();
        await using var _ = app;
        using var cancellationTokenSource = new CancellationTokenSource();
        var loginUrlTask = app.GetDashboardLoginUrlAsync(cancellationTokenSource.Token);

        Assert.Contains(
            notificationLogger.Collector.GetSnapshot(),
            entry => entry.Message.Contains(
                $"Waiting for resource '{KnownResourceNames.AspireDashboard}'",
                StringComparison.Ordinal));
        Assert.False(loginUrlTask.IsCompleted);

        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loginUrlTask);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task GetDashboardLoginUrlAsyncThrowsAfterApplicationIsDisposed()
    {
        await using var builder = await CreateDashboardBuilderAsync();
        var app = await builder.BuildAsync();

        try
        {
            await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);
            _ = await app.GetDashboardLoginUrlAsync(default);
            await app.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => app.GetDashboardLoginUrlAsync(default));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetDashboardLoginUrlAsyncThrowsInPublishMode()
    {
        var builder = DistributedApplicationTestingBuilder.Create(["--publisher", "manifest"]);
        await using var app = await builder.BuildAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.GetDashboardLoginUrlAsync(default));

        Assert.Equal(TestingResources.DashboardLoginUrlPublishModeExceptionMessage, exception.Message);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task GetDashboardLoginUrlAsyncPreservesTerminalDashboardFailure()
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
            () => app.GetDashboardLoginUrlAsync(cancellationTokenSource.Token));

        Assert.Contains(KnownResourceNames.AspireDashboard, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task CanonicalDashboardLoginUrlEscapesBrowserToken()
    {
        const string browserToken = "token+with&reserved=value";
        await using var builder = await CreateDashboardBuilderAsync();
        builder.Configuration["AppHost:BrowserToken"] = browserToken;
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DashboardLoginUrlTests");
        var info = await DashboardUrlsHelper.GetDashboardConnectionInfoAsync(
            app.Services,
            logger,
            cancellationTokenSource.Token);
        Assert.NotNull(info.BaseUrlWithLoginToken);

        Assert.EndsWith(
            $"/login?t={Uri.EscapeDataString(browserToken)}",
            info.BaseUrlWithLoginToken,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task GetDashboardLoginUrlAsyncUsesConfiguredTargetHost()
    {
        await using var builder = await CreateDashboardBuilderAsync();
        builder.Configuration["ASPNETCORE_URLS"] = "http://aspire-dashboard.dev.localhost:0";
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var dashboardUri = await app.GetDashboardLoginUrlAsync(cancellationTokenSource.Token);

        Assert.Equal("aspire-dashboard.dev.localhost", dashboardUri.Host);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task GetDashboardLoginUrlAsyncPrefersCodespacesUrl()
    {
        await using var builder = await CreateDashboardBuilderAsync();
        builder.Configuration["CODESPACES"] = "true";
        builder.Configuration["CODESPACE_NAME"] = "test-codespace";
        builder.Configuration["GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN"] = "app.github.dev";
        await using var app = await builder.BuildAsync();
        await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var dashboardUri = await app.GetDashboardLoginUrlAsync(cancellationTokenSource.Token);

        Assert.StartsWith("test-codespace-", dashboardUri.Host, StringComparison.Ordinal);
        Assert.EndsWith(".app.github.dev", dashboardUri.Host, StringComparison.Ordinal);
    }

    private static Task<IDistributedApplicationTestingBuilder> CreateDashboardBuilderAsync(string[]? args = null)
    {
        return DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
            CreateDashboardOptions(),
            args ?? []);
    }

    private static DistributedApplicationTestingBuilderOptions CreateDashboardOptions()
    {
        return new()
        {
            EnableDashboard = true
        };
    }

    private static async Task<(DistributedApplication App, FakeLogger<ResourceNotificationService> NotificationLogger)>
        CreateApplicationWithBlockedDashboardAsync()
    {
        var dashboardResource = new ExecutableResource(KnownResourceNames.AspireDashboard, "dashboard", ".");
        var notificationLogger = new FakeLogger<ResourceNotificationService>();
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(new DistributedApplicationModel([dashboardResource]));
                services.AddSingleton(new DistributedApplicationOptions
                {
                    DisableDashboard = false
                });
                services.AddSingleton(serviceProvider =>
                    new DistributedApplicationExecutionContext(
                        new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
                        {
                            Services = serviceProvider
                        }));
                services.AddSingleton<ResourceLoggerService>();
                services.AddSingleton(serviceProvider =>
                    new ResourceNotificationService(
                        notificationLogger,
                        serviceProvider.GetRequiredService<IHostApplicationLifetime>(),
                        serviceProvider,
                        serviceProvider.GetRequiredService<ResourceLoggerService>()));
                services.AddSingleton(
                    new ProfilingTelemetry(new ConfigurationBuilder().Build()));
            })
            .Build();
        await host.StartAsync();

        return (new DistributedApplication(host), notificationLogger);
    }
}
