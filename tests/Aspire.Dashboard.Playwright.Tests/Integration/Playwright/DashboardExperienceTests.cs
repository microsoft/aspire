// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class DashboardExperienceTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public DashboardExperienceTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    // Pages that both experiences have keep their path, so links to them work in either experience. Other pages go
    // to the experience's root.
    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("/resources", "/legacy")]
    [InlineData("/consolelogs", "/legacy/consolelogs")]
    [InlineData("/structuredlogs", "/legacy/structuredlogs")]
    [InlineData("/traces", "/legacy/traces")]
    [InlineData("/metrics", "/legacy/metrics")]
    [InlineData("/does-not-exist", "/legacy")]
    [InlineData("/legacyish", "/legacy")]
    public Task PreviousDashboard_RedirectsModernRoutesToLegacyPages(string modernPath, string expectedPath)
        => AssertExperienceRouteAsync("legacy", modernPath, expectedPath, expectLegacy: true);

    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("/legacy/resources", "/resources")]
    [InlineData("/legacy/consolelogs", "/consolelogs")]
    [InlineData("/legacy/structuredlogs", "/structuredlogs")]
    [InlineData("/legacy/traces", "/traces")]
    [InlineData("/legacy/metrics", "/metrics")]
    [InlineData("/legacy/does-not-exist", "/resources")]
    public Task NewDashboard_RedirectsLegacyRoutesToModernPages(string legacyPath, string expectedPath)
        => AssertExperienceRouteAsync("modern", legacyPath, expectedPath, expectLegacy: false);

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CrossExperienceRedirect_KeepsSharedPageQueryAndDiscardsFragment()
    {
        await RunTestAsync(async page =>
        {
            // A shared page keeps its query string. The fragment is always discarded. The page can then rewrite its query,
            // for example dropping an unknown resource, so the redirect itself is checked for the query.
            await SetExperienceAsync(page, "legacy");
            Assert.Equal("/legacy/traces?resource=api#", await GetRedirectLocationAsync(page, "/traces?resource=api"));
            await NavigateAcrossExperienceAsync(page, "/traces?resource=api#details");
            await page.WaitForFunctionAsync("() => location.pathname === '/legacy/traces' && location.hash === ''");

            await SetExperienceAsync(page, "modern");
            Assert.Equal("/traces?resource=api#", await GetRedirectLocationAsync(page, "/legacy/traces?resource=api"));
            // The page is already at this URL without the fragment, where navigating would only change the fragment.
            await page.GotoAsync("about:blank");
            await NavigateAcrossExperienceAsync(page, "/legacy/traces?resource=api#details");
            await page.WaitForFunctionAsync("() => location.pathname === '/traces' && location.hash === ''");

            // Other pages go to the experience's root, without the query string.
            await SetExperienceAsync(page, "legacy");
            await NavigateAcrossExperienceAsync(page, "/resources?resource=api#details");
            await page.WaitForFunctionAsync(
                "() => location.pathname === '/legacy' && location.search === '' && location.hash === ''");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task PreviousDashboard_InternalNavigationRemainsInLegacyLayout()
    {
        await RunTestAsync(async page =>
        {
            await SetExperienceAsync(page, "legacy");
            await page.GotoAsync("/legacy/resources");
            await Assertions.Expect(page.Locator(".page-title-slot")).ToHaveCountAsync(1);

            var link = page.GetByRole(AriaRole.Link, new() { Name = Resources.Layout.NavMenuConsoleLogsTab, Exact = true });
            await link.ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/legacy/consolelogs");

            await AssertSingleExperienceLayoutAsync(page, expectLegacy: true);
        });
    }

    private async Task AssertExperienceRouteAsync(string experience, string requestedPath, string expectedPath, bool expectLegacy)
    {
        await RunTestAsync(async page =>
        {
            await SetExperienceAsync(page, experience);
            var response = await page.GotoAsync(requestedPath);
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == expectedPath);

            Assert.Equal(200, response?.Status);
            await AssertSingleExperienceLayoutAsync(page, expectLegacy);
        });
    }

    private async Task SetExperienceAsync(IPage page, string experience)
    {
        var baseUrl = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress();
        await page.Context.AddCookiesAsync([new Cookie
        {
            Name = "Aspire_Dashboard_Experience",
            Value = experience,
            Url = baseUrl
        }]);
    }

    private static async Task<string?> GetRedirectLocationAsync(IPage page, string url)
    {
        // The context's request API sends the page's cookies, including the selected experience. Only page requests,
        // which accept HTML, are redirected.
        var response = await page.Context.APIRequest.GetAsync(url, new()
        {
            MaxRedirects = 0,
            Headers = new Dictionary<string, string> { ["Accept"] = "text/html" }
        });
        Assert.Equal(302, response.Status);
        return response.Headers.GetValueOrDefault("location");
    }

    private static async Task NavigateAcrossExperienceAsync(IPage page, string url)
    {
        try
        {
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Commit });
        }
        catch (PlaywrightException exception) when (exception.Message.Contains("net::ERR_ABORTED", StringComparison.Ordinal))
        {
            // Chromium can report the source navigation as aborted when the server immediately redirects it
            // to the selected experience root. The destination assertion below verifies the completed redirect.
        }
    }

    private static async Task AssertSingleExperienceLayoutAsync(IPage page, bool expectLegacy)
    {
        await Assertions.Expect(page.Locator(".layout")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".page-title-slot")).ToHaveCountAsync(expectLegacy ? 1 : 0);
        await Assertions.Expect(page.Locator(".header-context")).ToHaveCountAsync(expectLegacy ? 0 : 1);
    }
}
