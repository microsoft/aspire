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

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Spans_HidesResourcesThatDoNotFitBehindCount()
    {
        var repository = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        var resourceNames = Enumerable.Range(1, 8).Select(i => $"resource-with-a-long-name-{i}").ToList();
        await repository.AddTracesAsync(new AddContext(), CreateTraceSpans("overflow-trace", resourceNames, firstSpan: 0));

        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1800, 900);
            await page.GotoAsync("/traces");
            // Other tests in this class add traces to the same dashboard, so find this trace's row by its resources.
            var tagLine = page.Locator("table.main-grid aspire-tag-overflow").Filter(new() { HasText = resourceNames[0] });
            await Assertions.Expect(tagLine).ToHaveCountAsync(1);

            var wide = await WaitForTagLineAsync(tagLine, state => state.Count is not null);
            AssertOverflowingTagLine(wide, resourceNames);

            // A narrower window narrows the column, so the line is measured again and hides more tags.
            await page.SetViewportSizeAsync(1000, 900);
            var narrow = await WaitForTagLineAsync(tagLine, state => state.Width < wide.Width - 1 && state.Count is not null && state.Hidden.Count(h => h) > wide.Hidden.Count(h => h));
            AssertOverflowingTagLine(narrow, resourceNames);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Spans_RemeasuresWhenMoreResourcesJoinARenderedTrace()
    {
        var repository = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        var resourceNames = Enumerable.Range(1, 8).Select(i => $"growing-trace-resource-{i}").ToList();
        await repository.AddTracesAsync(new AddContext(), CreateTraceSpans("growing-trace", resourceNames.Take(1), firstSpan: 0));

        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1800, 900);
            await page.GotoAsync("/traces");
            var tagLine = page.Locator("table.main-grid aspire-tag-overflow").Filter(new() { HasText = resourceNames[0] });
            await Assertions.Expect(tagLine).ToHaveCountAsync(1);
            var initial = await WaitForTagLineAsync(tagLine, state => state.Tags.Length == 1);
            Assert.Null(initial.Count);
            Assert.All(initial.Hidden, Assert.False);

            // Spans from more resources arrive while the trace's row is shown, at the same width. Its tags change, so
            // the line is measured again: the tags that no longer fit are hidden, counted and listed in the tooltip.
            await repository.AddTracesAsync(new AddContext(), CreateTraceSpans("growing-trace", resourceNames.Skip(1), firstSpan: 1));
            var grown = await WaitForTagLineAsync(tagLine, state => state.Tags.Length == resourceNames.Count && state.Count is not null);
            AssertOverflowingTagLine(grown, resourceNames);
        });
    }

    // One span per resource, all in the trace. The first span is the root and the others are its children.
    private static RepeatedField<ResourceSpans> CreateTraceSpans(string traceId, IEnumerable<string> resourceNames, int firstSpan)
    {
        var startTime = DateTime.UtcNow.AddSeconds(-2);
        var resourceSpans = new RepeatedField<ResourceSpans>();
        resourceSpans.AddRange(resourceNames.Select((name, i) => new ResourceSpans
        {
            Resource = CreateResource(name: name, instanceId: name),
            ScopeSpans =
            {
                new ScopeSpans
                {
                    Scope = CreateScope(),
                    Spans =
                    {
                        CreateSpan(
                            traceId: traceId,
                            spanId: $"span-{firstSpan + i}",
                            parentSpanId: firstSpan + i == 0 ? null : "span-0",
                            startTime: startTime,
                            endTime: startTime.AddSeconds(1))
                    }
                }
            }
        }));
        return resourceSpans;
    }

    // Every tag stays in the DOM, so screen readers still read them. Only the trailing tags that don't fit are hidden,
    // and the "+N" tag counts them and lists them in its tooltip.
    private static void AssertOverflowingTagLine(TagLineState state, IEnumerable<string> resourceNames)
    {
        Assert.Equal(resourceNames.Select(name => $"{name} (1)").Order(), state.Tags.Order());
        var hiddenCount = state.Hidden.Count(hidden => hidden);
        Assert.InRange(hiddenCount, 1, state.Tags.Length);
        Assert.All(state.Hidden.Skip(state.Hidden.Length - hiddenCount), Assert.True);
        Assert.All(state.Hidden.Take(state.Hidden.Length - hiddenCount), Assert.False);
        Assert.Equal($"+{hiddenCount}", state.Count);
        Assert.Equal(string.Join("\n", state.Tags.Skip(state.Tags.Length - hiddenCount)), state.Tooltip);
        Assert.True(state.Fits, "The visible tags and the count should fit in the line.");
    }

    private static async Task<TagLineState> WaitForTagLineAsync(ILocator tagLine, Func<TagLineState, bool> isReady)
    {
        TagLineState? state = null;
        await AsyncTestHelpers.AssertIsTrueRetryAsync(async () =>
        {
            state = await tagLine.EvaluateAsync<TagLineState>("""
                line => {
                    const more = line.querySelector(':scope > [data-tag-overflow-more]');
                    const tags = [...line.children].filter(child => child !== more);
                    const right = line.getBoundingClientRect().right;
                    const shown = [...tags.filter(tag => !tag.hasAttribute('data-tag-overflow-hidden')), ...(more.hasAttribute('data-count') ? [more] : [])];
                    return {
                        tags: tags.map(tag => tag.textContent.replace(/\s+/g, ' ').trim()),
                        hidden: tags.map(tag => tag.hasAttribute('data-tag-overflow-hidden')),
                        count: more.getAttribute('data-count'),
                        tooltip: more.getAttribute('data-tooltip'),
                        fits: shown.every(element => element.getBoundingClientRect().right <= right + 0.5),
                        width: line.getBoundingClientRect().width
                    };
                }
                """);
            return isReady(state);
        }, "The tag line should be measured for the current width.");

        return state!;
    }

    private sealed class TagLineState
    {
        public string[] Tags { get; set; } = [];

        public bool[] Hidden { get; set; } = [];

        public string? Count { get; set; }

        public string? Tooltip { get; set; }

        public bool Fits { get; set; }

        public double Width { get; set; }
    }
}
