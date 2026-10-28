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
    [InlineData("/resources", "/legacy/resources")]
    [InlineData("/consolelogs", "/legacy/consolelogs")]
    [InlineData("/structuredlogs", "/legacy/structuredlogs")]
    [InlineData("/traces", "/legacy/traces")]
    [InlineData("/metrics", "/legacy/metrics")]
    [InlineData("/does-not-exist", "/legacy/does-not-exist")]
    [InlineData("/legacyish", "/legacy/legacyish")]
    public Task PreviousDashboard_RedirectsModernRoutesWithoutMixingLayouts(string modernPath, string expectedLegacyPath)
        => AssertExperienceRouteAsync("legacy", modernPath, expectedLegacyPath, expectLegacy: true);

    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("/legacy/resources", "/resources")]
    [InlineData("/legacy/consolelogs", "/consolelogs")]
    [InlineData("/legacy/structuredlogs", "/structuredlogs")]
    [InlineData("/legacy/traces", "/traces")]
    [InlineData("/legacy/metrics", "/metrics")]
    [InlineData("/legacy/does-not-exist", "/does-not-exist")]
    [InlineData("/legacyish", "/legacyish")]
    public Task NewDashboard_RedirectsLegacyRoutesWithoutMixingLayouts(string legacyPath, string expectedModernPath)
        => AssertExperienceRouteAsync("modern", legacyPath, expectedModernPath, expectLegacy: false);

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
            await page.GotoAsync(requestedPath);
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == expectedPath);

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

    private static async Task AssertSingleExperienceLayoutAsync(IPage page, bool expectLegacy)
    {
        await Assertions.Expect(page.Locator(".layout")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".page-title-slot")).ToHaveCountAsync(expectLegacy ? 1 : 0);
        await Assertions.Expect(page.Locator(".header-context")).ToHaveCountAsync(expectLegacy ? 0 : 1);
    }
}
