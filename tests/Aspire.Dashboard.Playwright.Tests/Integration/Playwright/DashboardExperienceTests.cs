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

    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("/resources")]
    [InlineData("/consolelogs")]
    [InlineData("/structuredlogs")]
    [InlineData("/traces")]
    [InlineData("/metrics")]
    [InlineData("/does-not-exist")]
    [InlineData("/legacyish")]
    public Task PreviousDashboard_RedirectsModernRoutesToLegacyRoot(string modernPath)
        => AssertExperienceRouteAsync("legacy", modernPath, "/legacy", expectLegacy: true);

    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("/legacy/resources")]
    [InlineData("/legacy/consolelogs")]
    [InlineData("/legacy/structuredlogs")]
    [InlineData("/legacy/traces")]
    [InlineData("/legacy/metrics")]
    [InlineData("/legacy/does-not-exist")]
    public Task NewDashboard_RedirectsLegacyRoutesToModernRoot(string legacyPath)
        => AssertExperienceRouteAsync("modern", legacyPath, "/resources", expectLegacy: false);

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CrossExperienceRedirect_DiscardsQueryAndFragment()
    {
        await RunTestAsync(async page =>
        {
            await SetExperienceAsync(page, "legacy");
            await NavigateAcrossExperienceAsync(page, "/traces?resource=api#details");
            await page.WaitForFunctionAsync(
                "() => location.pathname === '/legacy' && location.search === '' && location.hash === ''");

            await SetExperienceAsync(page, "modern");
            await NavigateAcrossExperienceAsync(page, "/legacy/traces?resource=api#details");
            await page.WaitForFunctionAsync(
                "() => location.pathname === '/resources' && location.search === '' && location.hash === ''");
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
