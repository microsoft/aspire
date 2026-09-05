// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Google.Protobuf.Collections;
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
    public async Task Duration_UsesDeterminateProgressBars()
    {
        var repository = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        var startTime = DateTime.UnixEpoch;
        await repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
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
            await page.GotoAsync("/traces");

            var progressBars = page.Locator("fluent-progress-bar.duration-progress");
            await Assertions.Expect(progressBars).ToHaveCountAsync(2);
            await Assertions.Expect(page.Locator("fluent-spinner.duration-ring")).ToHaveCountAsync(0);

            var values = await progressBars.EvaluateAllAsync<int[]>(
                "elements => elements.map(element => Number(element.getAttribute('value')))");
            var attributesAreValid = await progressBars.EvaluateAllAsync<bool>(
                """
                elements => elements.every(element =>
                    element.getAttribute('min') === '0' &&
                    element.getAttribute('max') === '2000' &&
                    element.getAttribute('shape') === 'rounded')
                """);

            Assert.Collection(
                values.Order(),
                value => Assert.Equal(1000, value),
                value => Assert.Equal(2000, value));
            Assert.True(attributesAreValid);
        });
    }
}
