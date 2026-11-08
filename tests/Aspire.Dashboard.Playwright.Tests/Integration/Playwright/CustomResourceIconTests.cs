// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public class CustomResourceIconTests(CustomResourceIconTests.CustomIconDashboardServerFixture fixture)
    : PlaywrightTestsBase<CustomResourceIconTests.CustomIconDashboardServerFixture>(fixture)
{
    private static readonly string s_iconData = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><circle cx="8" cy="8" r="7" fill="red"/></svg>"""));

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CustomIconLoadsInListAndUpdatesInGraph()
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/resources");
            var image = page.Locator($"img[src='{s_iconData}']").First;
            await Assertions.Expect(image).ToHaveAttributeAsync("src", s_iconData);
            await Assertions.Expect(image).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("data => Array.from(document.images).some(image => image.src === data && image.complete && image.naturalWidth > 0)", s_iconData);

            await ResourcePaneNavigation.OpenAllResourcesOverviewAsync(page);
            await page.GetByRole(AriaRole.Group, new() { Name = Dashboard.Resources.Resources.LayoutLabel, Exact = true })
                .GetByRole(AriaRole.Button, new() { Name = Dashboard.Resources.Layout.NavMenuGraphTab, Exact = true }).ClickAsync();
            var icon = page.Locator("[resource-name='app'] [data-resource-icon]");
            await Assertions.Expect(icon.Locator("image")).ToHaveAttributeAsync("href", s_iconData);
            await Assertions.Expect(icon.Locator("path")).ToHaveCountAsync(0);

            var secondData = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" fill="blue"/></svg>"""));
            await ((CustomIconDashboardServerFixture)DashboardServerFixture).PublishAsync(ModelTestHelpers.CreateResource("app", state: KnownResourceState.Running, iconData: secondData));
            await Assertions.Expect(icon.Locator("image")).ToHaveAttributeAsync("href", secondData);

            await ((CustomIconDashboardServerFixture)DashboardServerFixture).PublishAsync(ModelTestHelpers.CreateResource("app", state: KnownResourceState.Running, iconName: "Database"));
            await Assertions.Expect(icon.Locator("image")).ToHaveCountAsync(0);
            await Assertions.Expect(icon.Locator("path")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        });
    }

    public sealed class CustomIconDashboardServerFixture : DashboardServerFixture
    {
        private readonly ConcurrentBag<Channel<IReadOnlyList<ResourceViewModelChange>>> _updates = [];

        protected override IReadOnlyList<ResourceViewModel> Resources =>
            [ModelTestHelpers.CreateResource("app", state: KnownResourceState.Running, iconData: s_iconData)];

        protected override void ConfigureServices(IServiceCollection services)
        {
            var client = new MockDashboardClient(Resources, () =>
            {
                // Each page subscriber needs the complete update rather than a competing read.
                var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
                _updates.Add(channel);
                return channel;
            });
            services.AddSingleton<IDashboardClient>(client);
            services.AddSingleton<IRepositoryFactory>(services => new MockRepositoryFactory(services, client));
        }

        public Task PublishAsync(ResourceViewModel resource) => Task.WhenAll(_updates.Select(channel =>
            channel.Writer.WriteAsync([new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, resource)]).AsTask()));
    }
}
