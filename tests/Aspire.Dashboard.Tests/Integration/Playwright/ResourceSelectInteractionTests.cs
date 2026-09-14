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
public sealed class ResourceSelectInteractionTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public ResourceSelectInteractionTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task PointerSelection_ClosesDropdownAndSupportsConsecutiveChanges()
    {
        var repository = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            CreateResourceLogs("resource-a"),
            CreateResourceLogs("resource-b")
        });

        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/structuredlogs");

            var dropdown = page.Locator("fluent-field.resource-list fluent-dropdown");
            var control = dropdown.Locator("button[slot='control']");

            await SelectResourceAsync("resource-a");
            await SelectResourceAsync("resource-b");

            async Task SelectResourceAsync(string resourceName)
            {
                await control.ClickAsync();
                await dropdown.Locator($"fluent-option[text='{resourceName}']").ClickAsync();

                await Assertions.Expect(control).ToHaveTextAsync(resourceName);
                await Assertions.Expect(control).ToHaveAttributeAsync("aria-expanded", "false");
                await page.WaitForURLAsync($"**/structuredlogs/resource/{resourceName}");
            }
        });

        static ResourceLogs CreateResourceLogs(string resourceName)
        {
            return new ResourceLogs
            {
                Resource = CreateResource(name: resourceName, instanceId: resourceName),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords = { CreateLogRecord(message: resourceName) }
                    }
                }
            };
        }
    }
}