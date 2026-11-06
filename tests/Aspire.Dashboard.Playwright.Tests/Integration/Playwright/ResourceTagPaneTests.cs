// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourceTagPaneTests : PlaywrightTestsBase<ResourceTagPaneTests.TaggedResourcesFixture>
{
    private const string Tag = "backend";

    public ResourceTagPaneTests(TaggedResourcesFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TagHeader_SelectsTaggedResourcesAndChevronTogglesGroup()
    {
        var tagStore = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ResourceTagStore>();
        tagStore.AddTag("tag-a", Tag);
        tagStore.AddTag("tag-c", Tag);

        try
        {
            await RunTestAsync(async page =>
            {
                await page.SetViewportSizeAsync(1280, 900);
                await page.GotoAsync("/resources/tag-b?pane=tags");

                var pane = page.Locator(".resource-pane");
                var title = page.Locator("h1.resource-title");
                var tagGroup = pane.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = Tag, Exact = true });
                var tagLink = tagGroup.Locator(".resource-tag-link");
                await Assertions.Expect(title).ToHaveTextAsync("tag-b");

                await tagLink.ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"/resources\?resource=tag-a&resource=tag-c$"));
                await Assertions.Expect(title).ToHaveTextAsync(string.Format(CultureInfo.CurrentCulture, Resources.Layout.ResourceHeaderSelectedResources, 2));
                await Assertions.Expect(tagLink).ToHaveAttributeAsync("aria-current", "true");
                await Assertions.Expect(tagGroup.Locator(".resource-row a[aria-current='true']")).ToHaveCountAsync(2);

                // The chevron only expands and collapses the group; the selection stays the same.
                var toggle = tagGroup.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = string.Format(CultureInfo.CurrentCulture, Resources.Layout.ResourcePaneTagGroupToggle, Tag) });
                await toggle.ClickAsync();
                await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
                await Assertions.Expect(tagGroup.Locator(".resource-row")).ToHaveCountAsync(0);
                await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"/resources\?resource=tag-a&resource=tag-c$"));
                await Assertions.Expect(title).ToHaveTextAsync(string.Format(CultureInfo.CurrentCulture, Resources.Layout.ResourceHeaderSelectedResources, 2));

                await toggle.ClickAsync();
                await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
                await Assertions.Expect(tagGroup.Locator(".resource-row")).ToHaveCountAsync(2);

                // Selecting a single resource of the tag no longer selects the tag.
                await tagGroup.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "tag-a" }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources/tag-a$"));
                await Assertions.Expect(tagLink).Not.ToHaveAttributeAsync("aria-current", "true");

                var untaggedGroup = pane.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = Resources.Layout.ResourcePaneUntaggedGroup, Exact = true });
                await untaggedGroup.Locator(".resource-tag-link").ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources/tag-b$"));
                await Assertions.Expect(title).ToHaveTextAsync("tag-b");
            });
        }
        finally
        {
            tagStore.RemoveTag("tag-a", Tag);
            tagStore.RemoveTag("tag-c", Tag);
        }
    }

    public sealed class TaggedResourcesFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel>? Resources =>
        [
            ModelTestHelpers.CreateResource(resourceName: "tag-a", state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(resourceName: "tag-b", state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(resourceName: "tag-c", state: KnownResourceState.Running)
        ];
    }
}
