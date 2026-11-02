// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourceOverviewEndpointTests : PlaywrightTestsBase<ResourceOverviewEndpointTests.EndpointResourcesFixture>
{
    private const string PublicUrl = "https://localhost:7443/catalog?api-version=sample&token=example";
    private const string Mask = "\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf";
    private const string MaskedPublicUrl = "https://localhost:7443/catalog?api-version=" + Mask + "&token=" + Mask;
    private const string InternalUrl = "http://catalog:8080/catalog?token=example";
    private const string MaskedInternalUrl = "http://catalog:8080/catalog?token=" + Mask;
    private const string TcpUrl = "tcp://localhost:6379/?token=example";
    private const string MaskedTcpUrl = "tcp://localhost:6379/?token=" + Mask;
    private static readonly string s_longAddress = "https://localhost:7443/" + new string('a', 160) + "/openapi";
    private static readonly string s_longUrl = s_longAddress + "?token=example";
    private static readonly string s_maskedLongUrl = s_longAddress + "?token=" + Mask;

    public ResourceOverviewEndpointTests(EndpointResourcesFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(1440, 900)]
    public async Task Endpoints_ShowMaskedAddressesAndFullTooltipsOnHoverAndKeyboardFocus(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync("/resources/catalog", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });

            var card = page.Locator("[aria-labelledby='overview-endpoints-title']");
            var rows = card.Locator(".overview-endpoint");
            await Assertions.Expect(rows).ToHaveCountAsync(5, new() { Timeout = 30_000 });

            var catalog = rows.Filter(new() { Has = page.GetByText("Catalog API", new() { Exact = true }) });
            await Assertions.Expect(catalog.Locator(".overview-list-label")).ToHaveTextAsync("https");
            var link = catalog.GetByRole(AriaRole.Link, new() { Name = MaskedPublicUrl, Exact = true });
            await Assertions.Expect(link).ToBeVisibleAsync();
            await Assertions.Expect(link).ToHaveAttributeAsync("href", PublicUrl);
            await Assertions.Expect(link).ToHaveAttributeAsync("target", "_blank");
            await Assertions.Expect(link).ToHaveAttributeAsync("rel", "noreferrer noopener");
            await Assertions.Expect(link).ToHaveAttributeAsync("data-tooltip", MaskedPublicUrl);
            await Assertions.Expect(catalog.Locator(".overview-endpoint-address")).ToHaveTextAsync([MaskedPublicUrl, MaskedInternalUrl]);

            var tooltip = page.Locator(".dashboard-tooltip");
            await link.ScrollIntoViewIfNeededAsync();
            await link.HoverAsync();
            await Assertions.Expect(tooltip).ToBeVisibleAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(MaskedPublicUrl);
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(tooltip).ToHaveCountAsync(0);
            var secondary = catalog.Locator("span.overview-endpoint-address");
            await secondary.ScrollIntoViewIfNeededAsync();
            await link.FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(secondary).ToBeFocusedAsync();
            await Assertions.Expect(tooltip).ToBeVisibleAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(MaskedInternalUrl);
            await page.Keyboard.PressAsync("Escape");

            var tcp = rows.Filter(new() { Has = page.GetByText("Cache connection", new() { Exact = true }) });
            await Assertions.Expect(tcp.Locator(".overview-endpoint-address")).ToHaveTextAsync(MaskedTcpUrl);
            await Assertions.Expect(tcp.GetByRole(AriaRole.Link)).ToHaveCountAsync(0);
            await Assertions.Expect(tcp.Locator(".overview-badge")).ToHaveTextAsync("Internal");
            await tcp.Locator(".overview-endpoint-address").ScrollIntoViewIfNeededAsync();
            await tcp.Locator(".overview-endpoint-address").FocusAsync();
            await Assertions.Expect(tooltip).ToBeVisibleAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(MaskedTcpUrl);
            await page.Keyboard.PressAsync("Escape");
            await page.Mouse.MoveAsync(0, 0);

            var docs = rows.Filter(new() { Has = page.GetByText("Documentation", new() { Exact = true }) });
            await Assertions.Expect(docs.Locator(".overview-list-label")).ToHaveTextAsync("Documentation");
            await Assertions.Expect(docs.GetByRole(AriaRole.Link)).ToHaveTextAsync("https://localhost:7443/docs");

            var plain = rows.Filter(new() { Has = page.GetByText("http", new() { Exact = true }) });
            await Assertions.Expect(plain.GetByRole(AriaRole.Link)).ToHaveTextAsync("http://localhost:5080");

            await card.ScrollIntoViewIfNeededAsync();
            await card.Locator(".overview-list").EvaluateAsync("el => el.scrollTop = 0");
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"endpoints-compact-{width}x{height}.png") });

            var longAddress = card.GetByRole(AriaRole.Link, new() { Name = s_maskedLongUrl, Exact = true });
            await Assertions.Expect(longAddress).ToHaveAttributeAsync("href", s_longUrl);
            await longAddress.ScrollIntoViewIfNeededAsync();
            await Assertions.Expect(longAddress).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"endpoints-compact-long-{width}x{height}.png") });

            // Ellipsis clips only the displayed address. The complete masked value remains available through
            // the shared tooltip on hover and keyboard focus, while the link retains the original destination.
            Assert.True(await card.EvaluateAsync<bool>("card => card.scrollWidth <= card.clientWidth"));
            foreach (var address in await card.Locator(".overview-endpoint-address").AllAsync())
            {
                Assert.True(await address.EvaluateAsync<bool>(
                    "el => { const range = document.createRange(); range.selectNodeContents(el); return new Set([...range.getClientRects()].map(rect => Math.round(rect.top))).size === 1; }"));
            }
            Assert.True(await longAddress.EvaluateAsync<bool>("el => el.scrollWidth > el.clientWidth"));
            Assert.True(await page.Locator(".resource-overview").EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth"));
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));

            await longAddress.HoverAsync();
            await Assertions.Expect(tooltip).ToBeVisibleAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(s_maskedLongUrl);
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"endpoints-compact-hover-{width}x{height}.png") });
            await page.Mouse.MoveAsync(0, 0);
            await page.Keyboard.PressAsync("Escape");
            await longAddress.FocusAsync();
            await Assertions.Expect(longAddress).ToBeFocusedAsync();
            await Assertions.Expect(tooltip).ToBeVisibleAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(s_maskedLongUrl);
            await Assertions.Expect(longAddress).ToHaveAttributeAsync("href", s_longUrl);
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"endpoints-compact-focus-{width}x{height}.png") });
        });
    }

    public sealed class EndpointResourcesFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel>? Resources =>
        [
            ModelTestHelpers.CreateResource(resourceName: "catalog", resourceType: "Project", state: KnownResourceState.Running, urls:
            [
                new("https", new Uri(PublicUrl), false, false, new("Catalog API", 5)),
                new("https", new Uri(InternalUrl), true, false, new("Catalog API", 5)),
                new("openapi", new Uri(s_longUrl), false, false, new("OpenAPI document", 4)),
                new(null, new Uri("https://localhost:7443/docs"), false, false, new("Documentation", 3)),
                new("http", new Uri("http://localhost:5080"), false, false, UrlDisplayPropertiesViewModel.Empty),
                new("tcp", new Uri(TcpUrl), true, false, new("Cache connection", 0))
            ])
        ];
    }
}
