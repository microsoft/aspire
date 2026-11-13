// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Templates.Tests;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Playwright;
using Xunit;
using Xunit.Sdk;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class TemplateResourceAssertionsTests(TemplateResourceAssertionsTests.TemplateDashboardServerFixture fixture, ITestOutputHelper output)
    : PlaywrightTestsBase<TemplateResourceAssertionsTests.TemplateDashboardServerFixture>(fixture)
{
    private static readonly ResourceRow[] s_expectedResources =
    [
        new("Project", "TestResource", "Running", "C:/MyProjectPath/Project.csproj"),
        new("Container", "cache", "Running", "registry.example.test/redis:latest")
    ];

    // Templates and other app tests open the dashboard in its default density, compact, whose rows show the state
    // as a dot, so the helper must read the state at every density.
    [Theory]
    [InlineData("/resources", true)]
    [InlineData("/resources", false)]
    [InlineData("/graph", true)]
    [InlineData("/graph", false)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TemplateHelper_ValidatesSidebarAndDetailsWithoutCards(string path, bool useDefaultDensity)
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync(path);
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(2);
            var originalUrl = page.Url;

            foreach (var resource in s_expectedResources)
            {
                var row = page.Locator($".resource-row[data-resource-name='{resource.Name}']");
                await Assertions.Expect(DashboardResourceAssertions.RowState(row, resource.State)).ToBeVisibleAsync();
                await Assertions.Expect(DashboardResourceAssertions.RowState(row, "Finished")).ToHaveCountAsync(0);
            }

            var rows = await DashboardResourceAssertions.CheckResourcesAsync(new WrapperForIPage(page, output), s_expectedResources, output, timeoutSecs: 10);

            Assert.Equal(s_expectedResources.OrderBy(resource => resource.Name), rows.OrderBy(resource => resource.Name));
            Assert.Equal(originalUrl, page.Url);
        }, useDefaultDensity);
    }

    [Theory]
    [InlineData("Wrong.csproj")]
    [InlineData("redis:wrong-tag")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TemplateHelper_RejectsIncorrectSource(string incorrectSource)
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/resources");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(2);
            var expected = s_expectedResources.Select(resource => resource with
            {
                SourceContains = incorrectSource.EndsWith(".csproj", StringComparison.Ordinal) == (resource.Type == "Project")
                    ? incorrectSource
                    : resource.SourceContains
            }).ToArray();

            await Assert.ThrowsAsync<ContainsException>(() =>
                DashboardResourceAssertions.CheckResourcesAsync(new WrapperForIPage(page, output), expected, output, timeoutSecs: 10));
        });
    }

    public sealed class TemplateDashboardServerFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            MockDashboardClient.TestResource1,
            ModelTestHelpers.CreateResource("cache", state: KnownResourceState.Running, resourceType: KnownResourceTypes.Container,
                urls: [new UrlViewModel("http", new Uri("http://localhost:12345"), false, false, UrlDisplayPropertiesViewModel.Empty)],
                properties: new Dictionary<string, ResourcePropertyViewModel>
                {
                    [KnownProperties.Container.Image] = new(KnownProperties.Container.Image,
                        new Value { StringValue = "registry.example.test/redis:latest" },
                        isValueSensitive: false, knownProperty: null, sortOrder: 0, displayName: null, isHighlighted: false)
                })
        ];
    }
}
