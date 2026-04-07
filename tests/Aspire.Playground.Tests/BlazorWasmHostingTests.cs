// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using Aspire.Templates.Tests;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SamplesIntegrationTests.Infrastructure;
using Xunit;

namespace Aspire.Playground.Tests;

[RequiresFeature(TestFeature.Docker)]
public class BlazorWasmHostingTests(ITestOutputHelper testOutput)
{
    [Fact]
    public async Task HostedBlazorWasm_ServesAppAndWeatherApi()
    {
        await using var app = await CreateAppAsync(typeof(Projects.AspireWithBlazorHosted_AppHost));

        using var blazorClient = AppHostTests.CreateHttpClientWithResilience(app, "blazorapp");

        var homeResponse = await blazorClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        var homeContent = await homeResponse.Content.ReadAsStringAsync();
        Assert.Contains("blazor.web.js", homeContent);

        var configResponse = await blazorClient.GetAsync("/_blazor/_configuration");
        Assert.Equal(HttpStatusCode.OK, configResponse.StatusCode);

        var proxyApiResponse = await blazorClient.GetAsync("/_api/weatherapi/weatherforecast");
        Assert.Equal(HttpStatusCode.OK, proxyApiResponse.StatusCode);
        var proxyApiContent = await proxyApiResponse.Content.ReadAsStringAsync();
        Assert.Contains("temperatureC", proxyApiContent, StringComparison.OrdinalIgnoreCase);

        using var weatherApiClient = AppHostTests.CreateHttpClientWithResilience(app, "weatherapi");
        var directApiResponse = await weatherApiClient.GetAsync("/weatherforecast");
        Assert.Equal(HttpStatusCode.OK, directApiResponse.StatusCode);

        app.EnsureNoErrorsLogged();
        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Playwright)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task HostedBlazorWasm_BrowserRendersWeatherAndSendsTelemetry()
    {
        await using var app = await CreateAppAsync(typeof(Projects.AspireWithBlazorHosted_AppHost));

        var baseUrl = app.GetEndpoint("blazorapp").ToString().TrimEnd('/');

        PlaywrightProvider.DetectAndSetInstalledPlaywrightDependenciesPath();
        await using var browser = await PlaywrightProvider.CreateBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });

        // Track OTLP requests from the WASM client
        var otlpRequests = new ConcurrentBag<string>();
        await context.RouteAsync("**/_otlp/v1/**", async route =>
        {
            otlpRequests.Add(route.Request.Url);
            testOutput.WriteLine($"[otlp-intercept] {route.Request.Method} {route.Request.Url}");
            await route.ContinueAsync();
        });

        var page = await context.NewPageAsync();
        page.Console += (_, e) => testOutput.WriteLine($"[browser-console] {e.Text}");
        page.PageError += (_, e) => testOutput.WriteLine($"[browser-error] {e}");

        await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Navigate to the weather page and verify data renders
        await page.GotoAsync($"{baseUrl}/weather", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var tableLocator = page.Locator("table");
        await Assertions.Expect(tableLocator).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        var rows = tableLocator.Locator("tbody tr");
        await Assertions.Expect(rows).Not.ToHaveCountAsync(0);

        // Wait up to 60s for all three OTLP signal types to arrive (metrics may take longest)
        await WaitForOtlpSignalsAsync(page, otlpRequests);

        Assert.Contains(otlpRequests, url => url.Contains("/_otlp/v1/traces"));
        Assert.Contains(otlpRequests, url => url.Contains("/_otlp/v1/logs"));
        Assert.Contains(otlpRequests, url => url.Contains("/_otlp/v1/metrics"));

        await page.CloseAsync();
        await app.StopAsync();
    }

    [Fact]
    public async Task StandaloneBlazorWasm_GatewayServesAppAndWeatherApi()
    {
        await using var app = await CreateAppAsync(typeof(Projects.AspireWithBlazorStandalone_AppHost));

        using var gatewayClient = AppHostTests.CreateHttpClientWithResilience(app, "gateway");

        var homeResponse = await gatewayClient.GetAsync("/app/");
        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        var homeContent = await homeResponse.Content.ReadAsStringAsync();
        Assert.Contains("blazor.webassembly.js", homeContent);

        var configResponse = await gatewayClient.GetAsync("/app/_blazor/_configuration");
        Assert.Equal(HttpStatusCode.OK, configResponse.StatusCode);

        var apiResponse = await gatewayClient.GetAsync("/app/_api/weatherapi/weatherforecast");
        Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);
        var apiContent = await apiResponse.Content.ReadAsStringAsync();
        Assert.Contains("temperatureC", apiContent, StringComparison.OrdinalIgnoreCase);

        using var weatherApiClient = AppHostTests.CreateHttpClientWithResilience(app, "weatherapi");
        var directApiResponse = await weatherApiClient.GetAsync("/weatherforecast");
        Assert.Equal(HttpStatusCode.OK, directApiResponse.StatusCode);

        app.EnsureNoErrorsLogged();
        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Playwright)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task StandaloneBlazorWasm_BrowserRendersWeatherAndSendsTelemetry()
    {
        await using var app = await CreateAppAsync(typeof(Projects.AspireWithBlazorStandalone_AppHost));

        var baseUrl = app.GetEndpoint("gateway").ToString().TrimEnd('/');

        PlaywrightProvider.DetectAndSetInstalledPlaywrightDependenciesPath();
        await using var browser = await PlaywrightProvider.CreateBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });

        var otlpRequests = new ConcurrentBag<string>();
        await context.RouteAsync("**/_otlp/v1/**", async route =>
        {
            otlpRequests.Add(route.Request.Url);
            testOutput.WriteLine($"[otlp-intercept] {route.Request.Method} {route.Request.Url}");
            await route.ContinueAsync();
        });

        var page = await context.NewPageAsync();
        page.Console += (_, e) => testOutput.WriteLine($"[browser-console] {e.Text}");
        page.PageError += (_, e) => testOutput.WriteLine($"[browser-error] {e}");

        await page.GotoAsync($"{baseUrl}/app/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.GotoAsync($"{baseUrl}/app/weather", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var tableLocator = page.Locator("table");
        await Assertions.Expect(tableLocator).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        var rows = tableLocator.Locator("tbody tr");
        await Assertions.Expect(rows).Not.ToHaveCountAsync(0);

        // Wait up to 60s for all three OTLP signal types to arrive (metrics may take longest)
        await WaitForOtlpSignalsAsync(page, otlpRequests);

        Assert.Contains(otlpRequests, url => url.Contains("/_otlp/v1/traces"));
        Assert.Contains(otlpRequests, url => url.Contains("/_otlp/v1/logs"));
        Assert.Contains(otlpRequests, url => url.Contains("/_otlp/v1/metrics"));

        await page.CloseAsync();
        await app.StopAsync();
    }

    private static async Task WaitForOtlpSignalsAsync(IPage page, ConcurrentBag<string> otlpRequests)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var hasTraces = otlpRequests.Any(url => url.Contains("/_otlp/v1/traces"));
            var hasLogs = otlpRequests.Any(url => url.Contains("/_otlp/v1/logs"));
            var hasMetrics = otlpRequests.Any(url => url.Contains("/_otlp/v1/metrics"));
            if (hasTraces && hasLogs && hasMetrics)
            {
                return;
            }

            await page.WaitForTimeoutAsync(1_000);
        }
    }

    private async Task<Aspire.Hosting.DistributedApplication> CreateAppAsync(Type appHostType)
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync(appHostType);
        builder.WithRandomParameterValues();
        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddSimpleConsole(configure => configure.SingleLine = true);
            logging.AddXunit(testOutput);
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter("Aspire", LogLevel.Trace);
        });

        var app = await builder.BuildAsync();
        await app.StartAsync();
        await app.WaitForResources().WaitAsync(TimeSpan.FromMinutes(5));
        return app;
    }
}
