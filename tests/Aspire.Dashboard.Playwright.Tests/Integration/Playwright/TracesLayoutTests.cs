// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class TracesLayoutTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public TracesLayoutTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Duration_UsesDeterminateProgressRings()
    {
        var repository = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        var startTime = DateTime.UtcNow.AddSeconds(-2);
        await repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: MockDashboardClient.TestResource1.Name, instanceId: MockDashboardClient.TestResource1.Uid),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "trace-1", spanId: "span-1", startTime: startTime, endTime: startTime.AddSeconds(1)),
                            CreateSpan(traceId: "trace-2", spanId: "span-2", startTime: startTime, endTime: startTime.AddSeconds(2))
                        }
                    }
                }
            }
        });

        await RunTestAsync(async page =>
        {
            await page.GotoAsync($"/traces/resource/{MockDashboardClient.TestResource1.Name}?pane=telemetry");

            var progressRings = page.Locator(".aspire-progress-ring.determinate.duration-ring");
            await Assertions.Expect(progressRings).ToHaveCountAsync(2);

            int[] values = [];
            var attributesAreValid = false;
            await AsyncTestHelpers.AssertIsTrueRetryAsync(async () =>
            {
                values = await progressRings.EvaluateAllAsync<int[]>(
                    "elements => elements.map(element => Number(element.getAttribute('aria-valuenow')))");
                attributesAreValid = await progressRings.EvaluateAllAsync<bool>(
                    """
                    elements => elements.length === 2 && elements.every(element =>
                        element.getAttribute('aria-valuemin') === '0' &&
                        element.getAttribute('aria-valuemax') === '2000' &&
                        element.getBoundingClientRect().width === 20 &&
                        element.getBoundingClientRect().height === 20)
                    """);
                return values.Length == 2 && attributesAreValid;
            }, "The two determinate duration rings should remain rendered with valid dimensions and ARIA values.");

            Assert.Collection(
                values.Order(),
                value => Assert.Equal(1000, value),
                value => Assert.Equal(2000, value));
            Assert.True(attributesAreValid);
        });
    }
}
