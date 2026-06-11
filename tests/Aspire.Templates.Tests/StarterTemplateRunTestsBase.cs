// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using Xunit;
using Aspire.Hosting.Redis;
using System.Net.Http.Json;
using Aspire.TestUtilities;

namespace Aspire.Templates.Tests;

public abstract class StarterTemplateRunTestsBase<T> : TemplateTestsBase, IClassFixture<T> where T : TemplateAppFixture
{
    protected readonly T _testFixture;
    protected bool HasRedisCache;
    protected virtual int DashboardResourcesWaitTimeoutSecs => 120;

    public StarterTemplateRunTestsBase(T fixture, ITestOutputHelper testOutput)
        : base(testOutput)
    {
        _testFixture = fixture;
    }

    [Fact]
    [RequiresFeature(TestFeature.Playwright)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourcesShowUpOnDashboard()
    {
        await using var context = await CreateNewBrowserContextAsync();
        await CheckDashboardHasResourcesAsync(
            await _testFixture.Project!.OpenDashboardPageAsync(context),
            GetExpectedResources(_testFixture.Project!, hasRedisCache: HasRedisCache),
            timeoutSecs: DashboardResourcesWaitTimeoutSecs,
            logPath: _testFixture.Project.LogPath);
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("https://")]
    [RequiresFeature(TestFeature.Playwright)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task WebFrontendWorks(string urlPrefix)
    {
        await using var context = await CreateNewBrowserContextAsync();
        var resourceRows = await CheckDashboardHasResourcesAsync(
            await _testFixture.Project!.OpenDashboardPageAsync(context),
            GetExpectedResources(_testFixture.Project!, hasRedisCache: HasRedisCache),
            timeoutSecs: DashboardResourcesWaitTimeoutSecs,
            logPath: _testFixture.Project.LogPath);

        string url = _testFixture.Project.InfoTable["webfrontend"].Endpoints
            .First(e => e.Uri.StartsWith(urlPrefix))
            .Uri;
        await CheckWebFrontendWorksAsync(context, url, _testOutput, _testFixture.Project.LogPath, hasRedisCache: HasRedisCache);
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("https://")]
    [RequiresFeature(TestFeature.Playwright)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [Trait("category", "basic-build")]
    public async Task ApiServiceWorks(string urlPrefix)
    {
        await using var context = await CreateNewBrowserContextAsync();
        var resourceRows = await CheckDashboardHasResourcesAsync(
            await _testFixture.Project!.OpenDashboardPageAsync(context),
            GetExpectedResources(_testFixture.Project!, hasRedisCache: HasRedisCache),
            timeoutSecs: DashboardResourcesWaitTimeoutSecs,
            logPath: _testFixture.Project.LogPath);

        string url = _testFixture.Project.InfoTable["apiservice"].Endpoints
            .First(e => e.Uri.StartsWith(urlPrefix))
            .Uri;
        await CheckApiServiceWorksAsync(url, _testOutput, _testFixture.Project.LogPath);
    }

    public static async Task CheckApiServiceWorksAsync(string url, ITestOutputHelper testOutput, string logPath)
    {
        var uri = new UriBuilder(url) { Path = "weatherforecast" }.Uri;

        using var httpClient = new HttpClient();
        var response = await httpClient.GetFromJsonAsync<WeatherForecast[]>(uri);

        Assert.NotNull(response);
        Assert.Equal(5, response.Length);
    }

    public static async Task CheckWebFrontendWorksAsync(IBrowserContext context, string url, ITestOutputHelper testOutput, string logPath, bool hasRedisCache = false)
    {
        var pageWrapper = await context.NewPageWithLoggingAsync(testOutput);

        try
        {
            // Enabling routing disables the http cache
            await pageWrapper.Page.RouteAsync("**", async route => await route.ContinueAsync());
            await pageWrapper.Page.GotoAsync(url);

            await pageWrapper.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Weather" }).ClickAsync();

            var tableLoc = pageWrapper.Page.Locator("//table[//thead/tr/th/text()='Date']");
            await Expect(tableLoc).ToBeVisibleAsync();

            if (hasRedisCache)
            {
                // Compare weather data after refreshes
                var firstLoadText = string.Join(',', (await GetAndValidateCellTexts(tableLoc)).SelectMany(r => r));
                await Task.Delay(10_000);

                await pageWrapper.Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load });

                var secondLoadText = string.Join(',', (await GetAndValidateCellTexts(tableLoc)).SelectMany(r => r));
                Assert.NotEqual(firstLoadText, secondLoadText);
            }
        }
        catch (Exception ex)
        {
            testOutput.WriteLine($"Error: {ex}");
            string screenshotPath = Path.Combine(logPath, "webfrontend-fail.png");
            await pageWrapper.Page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
            throw;
        }

        static async Task<List<string[]>> GetAndValidateCellTexts(ILocator tableLoc)
        {
            List<string[]> cellTexts = [];
            var rowsLoc = tableLoc.Locator("//tbody/tr");
            foreach (var row in await rowsLoc.AllAsync())
            {
                var texts = (await row.Locator("//td").AllAsync())
                    .Select(cell => cell.InnerHTMLAsync())
                    .Select(t => t.Result)
                    .ToArray();
                cellTexts.Add(texts);
            }

            foreach (var row in cellTexts)
            {
                Assert.Collection(row,
                    r => Assert.True(DateTime.TryParse(r, out _)),
                    r => Assert.True(int.TryParse(r, out var actualTempC) && actualTempC >= -20 && actualTempC <= 55),
                    r => Assert.True(int.TryParse(r, out var actualTempF) && actualTempF >= -5 && actualTempF <= 133),
                    r => Assert.Contains(r, new HashSet<string> { "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching" }));
            }

            return cellTexts;
        }
    }

    public static List<ResourceRow> GetExpectedResources(AspireProject project, bool hasRedisCache)
    {
        var expectedResources = new List<ResourceRow>
        {
            new(Type: "Project",
                Name: "apiservice",
                State: "Running",
                SourceContains: $"{project.Id}.ApiService.csproj"),

            new(Type: "Project",
                Name: "webfrontend",
                State: "Running",
                SourceContains: $"{project.Id}.Web.csproj")
        };

        if (hasRedisCache)
        {
            expectedResources.Add(
                new ResourceRow(Type: "Container",
                                Name: "cache",
                                State: "Running",
                                SourceContains: $"{RedisContainerImageTags.Registry}/{RedisContainerImageTags.Image}:{RedisContainerImageTags.Tag}"));
        }

        return expectedResources;
    }
}

public sealed record ResourceRow(string Type, string Name, string State, string SourceContains);

public sealed record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
