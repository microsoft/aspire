// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Google.Protobuf.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenTelemetry.Proto.Metrics.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class MetricsFilterPopoverTests : PlaywrightTestsBase<DashboardServerFixture>
{
    private const string MeterName = "System.Runtime";
    private const string InstrumentName = "dotnet.gc.collections";
    private const string DimensionName = "gc.heap.generation";
    private static readonly string[] s_dimensionValues = ["gen0", "gen1", "gen2"];

    public MetricsFilterPopoverTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FilterPopover_TriggerNearViewportBottom_OpensAboveTriggerWithinViewport()
    {
        await AddGcCollectionsMetricAsync();

        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1280, 720);
            var filterButton = await OpenMetricAsync(page);

            // Place the trigger at the bottom of the viewport so there is not enough room below it.
            await filterButton.EvaluateAsync("element => element.scrollIntoView({ block: 'end', inline: 'nearest' })");
            var buttonBox = await filterButton.BoundingBoxAsync();
            Assert.NotNull(buttonBox);
            Assert.True(720 - (buttonBox.Y + buttonBox.Height) < 100,
                $"Precondition: the filter button should be near the viewport bottom but its bottom was at {buttonBox.Y + buttonBox.Height}.");

            var (dialogTop, dialogBottom, dialogRight, lastOptionBox) = await OpenPopoverAsync(page, filterButton);

            Assert.True(dialogTop >= 0 && dialogBottom <= 720,
                $"Filter popover should be within the viewport (0-720) but spans {dialogTop}-{dialogBottom}.");
            Assert.True(lastOptionBox.Y >= 0 && lastOptionBox.Y + lastOptionBox.Height <= 720,
                $"Last filter option should be within the viewport (0-720) but spans {lastOptionBox.Y}-{lastOptionBox.Y + lastOptionBox.Height}.");
            Assert.True(dialogBottom <= buttonBox.Y + 1,
                $"Filter popover should open above its trigger (trigger top {buttonBox.Y}) but its bottom is at {dialogBottom}.");
            Assert.InRange(dialogRight, buttonBox.X + buttonBox.Width - 1, buttonBox.X + buttonBox.Width + 1);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FilterPopover_RoomBelowTrigger_OpensBelowTriggerAlignedToTriggerEnd()
    {
        await AddGcCollectionsMetricAsync();

        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1280, 1600);
            var filterButton = await OpenMetricAsync(page);

            var buttonBox = await filterButton.BoundingBoxAsync();
            Assert.NotNull(buttonBox);
            Assert.True(1600 - (buttonBox.Y + buttonBox.Height) > 400,
                $"Precondition: the filter button should have room below it but its bottom was at {buttonBox.Y + buttonBox.Height}.");

            var (dialogTop, dialogBottom, dialogRight, _) = await OpenPopoverAsync(page, filterButton);

            Assert.True(dialogTop >= buttonBox.Y + buttonBox.Height - 1,
                $"Filter popover should open below its trigger (trigger bottom {buttonBox.Y + buttonBox.Height}) but its top is at {dialogTop}.");
            Assert.True(dialogBottom <= 1600, $"Filter popover bottom {dialogBottom} should be within the viewport.");
            Assert.InRange(dialogRight, buttonBox.X + buttonBox.Width - 1, buttonBox.X + buttonBox.Width + 1);
        });
    }

    private async Task AddGcCollectionsMetricAsync()
    {
        var repository = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        var startTime = DateTime.UtcNow.AddSeconds(-30);
        var scopeMetrics = new ScopeMetrics { Scope = CreateScope(MeterName) };
        foreach (var value in s_dimensionValues)
        {
            scopeMetrics.Metrics.Add(CreateSumMetric(InstrumentName, startTime, [KeyValuePair.Create(DimensionName, value)]));
        }

        var addContext = new AddContext();
        await repository.AddMetricsAsync(addContext, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: MockDashboardClient.TestResource1.Name, instanceId: MockDashboardClient.TestResource1.Uid),
                ScopeMetrics = { scopeMetrics }
            }
        });
        Assert.Equal(0, addContext.FailureCount);
    }

    private static async Task<ILocator> OpenMetricAsync(IPage page)
    {
        await page.GotoAsync($"/metrics/resource/{MockDashboardClient.TestResource1.Name}?meter={MeterName}&instrument={InstrumentName}&duration=5&view=Graph&pane=telemetry");

        var filterButton = page.Locator(".chart-filter-button").First;
        await Assertions.Expect(filterButton).ToBeVisibleAsync();
        return filterButton;
    }

    private static async Task<(double Top, double Bottom, double Right, LocatorBoundingBoxResult LastOption)> OpenPopoverAsync(IPage page, ILocator filterButton)
    {
        await filterButton.ClickAsync();

        var lastOption = page.Locator(".chart-filter-popover .aspire-checkbox").Filter(new() { HasText = s_dimensionValues[^1] }).First;
        await Assertions.Expect(lastOption).ToBeVisibleAsync();

        // The popover dialog renders in the top layer; measure it from its shadow part.
        var dialogBox = await page.Locator(".chart-filter-popover").First.EvaluateAsync<double[]>(
            """
            element => {
                const dialog = element.shadowRoot?.querySelector('[part~="dialog"]') ?? element;
                const rect = dialog.getBoundingClientRect();
                return [rect.top, rect.bottom, rect.right];
            }
            """);
        var optionBox = await lastOption.BoundingBoxAsync();
        Assert.NotNull(optionBox);

        return (dialogBox[0], dialogBox[1], dialogBox[2], optionBox);
    }
}
