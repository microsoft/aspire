// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Google.Protobuf.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class AspireMenuLayoutTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public AspireMenuLayoutTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task StructuredLogActions_TriggerRemainsVisibleAndMenuItemsSharePopup()
    {
        var repository = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "menu-resource", instanceId: "menu-resource"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords = { CreateLogRecord(message: "menu test") }
                    }
                }
            }
        });

        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1024, 400);
            await page.GotoAsync("/structuredlogs");

            var trigger = page.Locator(".grid-action-container > fluent-button[aria-label='Actions']").First;
            await Assertions.Expect(trigger).ToBeVisibleAsync();

            var cell = trigger.Locator("xpath=ancestor::td");
            var triggerBefore = await trigger.BoundingBoxAsync();
            var cellBefore = await cell.BoundingBoxAsync();
            Assert.NotNull(triggerBefore);
            Assert.NotNull(cellBefore);

            await trigger.ClickAsync();
            await Assertions.Expect(trigger).ToHaveAttributeAsync("aria-expanded", "true");

            var menuList = trigger.Locator("xpath=following-sibling::fluent-menu/fluent-menu-list");
            await Assertions.Expect(menuList).ToBeVisibleAsync();

            var values = await trigger.EvaluateAsync<double[]>(
                """
                trigger => {
                    const cell = trigger.closest('td');
                    const container = trigger.parentElement;
                    const menuList = container.querySelector('fluent-menu-list');
                    const items = [...menuList.querySelectorAll(':scope > fluent-menu-item')];
                    const triggerBounds = trigger.getBoundingClientRect();
                    const cellBounds = cell.getBoundingClientRect();
                    const containerBounds = container.getBoundingClientRect();
                    const itemBounds = items.map(item => item.getBoundingClientRect());
                    const menuListBounds = menuList.getBoundingClientRect();

                    return [
                        parseFloat(getComputedStyle(trigger).minWidth),
                        triggerBounds.top - cellBounds.top,
                        cellBounds.bottom - triggerBounds.bottom,
                        containerBounds.height - cellBounds.height,
                        items.length,
                        new Set(itemBounds.map(bounds => Math.round(bounds.left))).size,
                        Math.max(...itemBounds.map(bounds => bounds.right)) - Math.min(...itemBounds.map(bounds => bounds.right)),
                        menuListBounds.left,
                        window.innerWidth - menuListBounds.right,
                        menuListBounds.top,
                        window.innerHeight - menuListBounds.bottom
                    ];
                }
                """);

            Assert.Equal(32, values[0]);
            Assert.True(values[1] >= 0);
            Assert.True(values[2] >= 0);
            Assert.InRange(Math.Abs(values[3]), 0, 1);
            Assert.True(values[4] >= 2);
            Assert.Equal(1, values[5]);
            Assert.InRange(values[6], 0, 1);
            Assert.True(values[7] >= 0, $"Menu overflowed the left viewport edge by {-values[7]}px.");
            Assert.True(values[8] >= 0, $"Menu overflowed the right viewport edge by {-values[8]}px.");
            Assert.True(values[9] >= 0, $"Menu overflowed the top viewport edge by {-values[9]}px.");
            Assert.True(values[10] >= 0, $"Menu overflowed the bottom viewport edge by {-values[10]}px.");
        });
    }
}