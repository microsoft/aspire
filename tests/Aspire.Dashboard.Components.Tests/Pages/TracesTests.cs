// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Controls.Grid;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class TracesTests : DashboardTestContext
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_FooterShowsCountAndPauseWarningInCorrectLocation(bool isDesktop)
    {
        SetupTracesServices();
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: false));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/traces");
        Services.GetRequiredService<PauseManager>().SetTracesPaused(true);

        var cut = Render<ResourcesLayout>(parameters => parameters
            .AddCascadingValue(viewport)
            .Add(p => p.Body, builder =>
            {
                builder.OpenComponent<Traces>(0);
                builder.CloseComponent();
            }));

        cut.WaitForAssertion(() =>
        {
            var footer = cut.Find(isDesktop ? ".resource-footer-page-status" : "footer[style='grid-area: footer']");
            Assert.Equal("Showing 0 traces", footer.QuerySelector(".result-count")!.TextContent.Trim());
            Assert.Single(footer.QuerySelectorAll(".pause-warning"));
            Assert.Single(cut.FindComponents<TotalItemsFooter>());
        });
    }

    [Theory]
    [InlineData(AspireDataType.StructuredLogs)]
    [InlineData(AspireDataType.Traces)]
    [InlineData(AspireDataType.Metrics)]
    public async Task EmptyState_HelpDisappearsAfterAnySignalAndStaysHiddenAfterClear(AspireDataType signal)
    {
        SetupTracesServices();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        Services.GetRequiredService<DimensionManager>().InvokeOnViewportInformationChanged(viewport);
        var cut = Render<Traces>(parameters => parameters.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".telemetry-help-button")));
        Assert.Equal(Resources.Traces.TracesNoTraces, cut.Find(".telemetry-empty-content > div").TextContent.Trim());

        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await TelemetryHelpSetupHelpers.AddSignalAsync(repository, signal);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".telemetry-help-button")));
        await repository.ClearStructuredLogsAsync();
        await repository.ClearTracesAsync();
        await repository.ClearMetricsAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".telemetry-empty-content"));
            Assert.Empty(cut.FindAll(".telemetry-help-button"));
        });
    }

    [Fact]
    public void Render_FocusesAccessibleScrollContainerOnInitialRender()
    {
        SetupTracesServices();

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = Render<Traces>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        var scrollContainer = cut.Find("#tracesScrollContainer");
        var scrollButton = Assert.Single(scrollContainer.QuerySelectorAll(":scope > aspire-scroll-to-bottom"));
        Assert.True(scrollButton.HasAttribute("hidden"));
        var controlsLoc = Services.GetRequiredService<IStringLocalizer<Dashboard.Resources.ControlsStrings>>();
        Assert.Equal(controlsLoc[nameof(Dashboard.Resources.ControlsStrings.ScrollToBottom)].Value, scrollButton.GetAttribute("data-scroll-to-bottom-label"));
        var grid = cut.FindComponent<AspireFluentDataGrid<TraceSummary>>();
        var loc = Services.GetRequiredService<IStringLocalizer<Dashboard.Resources.Traces>>();

        Assert.Equal("0", scrollContainer.GetAttribute("tabindex"));
        Assert.Equal("region", scrollContainer.GetAttribute("role"));
        Assert.Equal(loc[nameof(Dashboard.Resources.Traces.TracesHeader)].Value, scrollContainer.GetAttribute("aria-label"));
        Assert.Equal(VirtualizeAnchorMode.End, grid.Instance.AnchorMode);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 2 &&
                string.Equals(invocation.Arguments[0]?.ToString(), "tracesScrollContainer", StringComparison.Ordinal) &&
                string.Equals(invocation.Arguments[1]?.ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task ItemsProvider_ZeroCountRefresh_RendersUpdatedTrace()
    {
        SetupTracesServices();

        var timestamp = DateTime.UnixEpoch;
        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
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
                            CreateSpan(traceId: "trace-1", spanId: "span-1", startTime: timestamp, endTime: timestamp.AddSeconds(1)),
                            CreateSpan(traceId: "trace-2", spanId: "span-2", startTime: timestamp, endTime: timestamp.AddSeconds(1)),
                            CreateSpan(traceId: "trace-3", spanId: "span-3", startTime: timestamp, endTime: timestamp.AddSeconds(1)),
                            CreateSpan(traceId: "trace-4", spanId: "span-4", startTime: timestamp, endTime: timestamp.AddSeconds(1)),
                            CreateSpan(traceId: "trace-5", spanId: "span-5", startTime: timestamp, endTime: timestamp.AddSeconds(1)),
                            CreateSpan(traceId: "trace-6", spanId: "span-6", startTime: timestamp, endTime: timestamp.AddSeconds(1))
                        }
                    }
                }
            }
        });
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        Services.GetRequiredService<DimensionManager>().InvokeOnViewportInformationChanged(viewport);
        var cut = Render<Traces>(builder => builder.AddCascadingValue(viewport));
        var grid = cut.FindComponent<AspireFluentDataGrid<TraceSummary>>();
        await grid.InvokeAsync(grid.Instance.RefreshDataAndRenderAsync);

        cut.WaitForAssertion(() => Assert.All(cut.FindAll(".trace-service-tag"), element => Assert.Contains("(1)", element.TextContent, StringComparison.Ordinal)));

        var result = await cut.InvokeAsync(() => grid.Instance.ItemsProvider!(new GridItemsProviderRequest<TraceSummary>
        {
            StartIndex = 0,
            Count = 0
        }).AsTask());
        var updatedTraceId = result.Items.Single(item => item.FullName.EndsWith("span-1", StringComparison.Ordinal)).TraceId;

        await telemetryRepository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "trace-1", spanId: "span-1b", startTime: timestamp, endTime: timestamp.AddSeconds(2)) }
                    }
                }
            }
        });
        await grid.InvokeAsync(grid.Instance.RefreshDataAndRenderAsync);

        var refreshedResult = await cut.InvokeAsync(() => grid.Instance.ItemsProvider!(new GridItemsProviderRequest<TraceSummary>
        {
            StartIndex = 0,
            Count = 0
        }).AsTask());

        Assert.Equal(6, result.TotalItemCount);
        Assert.Equal(6, result.Items.Count);
        Assert.Equal(6, refreshedResult.TotalItemCount);
        Assert.Equal(6, refreshedResult.Items.Count);
        var itemComparer = Assert.IsAssignableFrom<IEqualityComparer<TraceSummary>>(grid.Instance.ItemComparer);
        foreach (var original in result.Items)
        {
            var refreshed = refreshedResult.Items.Single(item => item.TraceId == original.TraceId);
            Assert.NotSame(original, refreshed);
            if (original.TraceId == updatedTraceId)
            {
                Assert.True(refreshed.LastUpdatedTimestampTicks > original.LastUpdatedTimestampTicks);
                Assert.False(itemComparer.Equals(original, refreshed));
            }
            else
            {
                Assert.Equal(original.LastUpdatedTimestampTicks, refreshed.LastUpdatedTimestampTicks);
                Assert.True(itemComparer.Equals(original, refreshed));
                Assert.Equal(itemComparer.GetHashCode(original), itemComparer.GetHashCode(refreshed));
                Assert.False(itemComparer.Equals(original, result.Items.Single(item => item.TraceId == updatedTraceId)));
            }
        }
        Assert.Equal(2, refreshedResult.Items.Single(item => item.TraceId == updatedTraceId).Resources.Single().TotalSpans);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".trace-service-tag"), element => element.TextContent.Contains("(2)", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Render_TraceResources_RendersEveryResourceTagForBrowserOverflow()
    {
        SetupTracesServices();

        var timestamp = DateTime.UnixEpoch;
        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            CreateResourceSpans("frontend", CreateSpan(traceId: "trace", spanId: "span-1", startTime: timestamp, endTime: timestamp.AddSeconds(3))),
            CreateResourceSpans("catalogservice",
                CreateSpan(traceId: "trace", spanId: "span-2", parentSpanId: "span-1", startTime: timestamp.AddSeconds(1), endTime: timestamp.AddSeconds(2)),
                CreateSpan(traceId: "trace", spanId: "span-3", parentSpanId: "span-1", startTime: timestamp.AddSeconds(1), endTime: timestamp.AddSeconds(2))),
            CreateResourceSpans("catalogdb", CreateSpan(traceId: "trace", spanId: "span-4", parentSpanId: "span-2", startTime: timestamp.AddSeconds(1), endTime: timestamp.AddSeconds(2)))
        });
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        Services.GetRequiredService<DimensionManager>().InvokeOnViewportInformationChanged(viewport);
        var cut = Render<Traces>(builder => builder.AddCascadingValue(viewport));
        var grid = cut.FindComponent<AspireFluentDataGrid<TraceSummary>>();
        await grid.InvokeAsync(grid.Instance.RefreshDataAndRenderAsync);
        var result = await cut.InvokeAsync(() => grid.Instance.ItemsProvider!(new GridItemsProviderRequest<TraceSummary>
        {
            StartIndex = 0,
            Count = 0
        }).AsTask());
        var expectedTags = Assert.Single(result.Items).Resources.Select(resource => $"{resource.Resource.ResourceName} ({resource.TotalSpans})").ToList();
        Assert.Equal(3, expectedTags.Count);

        // The browser decides which tags fit and fills the count, so the server renders every tag and an empty
        // count placeholder. The hidden tags stay in the accessibility tree, and the count is presentational.
        cut.WaitForAssertion(() =>
        {
            var tagLine = cut.Find("aspire-tag-overflow");
            var children = tagLine.Children.ToList();
            Assert.Equal(expectedTags, children.SkipLast(1).Select(child => string.Join(" ", child.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));
            Assert.All(children.SkipLast(1), child => Assert.Contains("trace-service-tag", child.ClassList));
            var more = children[^1];
            Assert.True(more.HasAttribute("data-tag-overflow-more"));
            Assert.Equal("true", more.GetAttribute("aria-hidden"));
            Assert.Equal(string.Empty, more.TextContent);
            Assert.All(children, child =>
            {
                Assert.False(child.HasAttribute("data-count"));
                Assert.False(child.HasAttribute("data-tag-overflow-hidden"));
            });
        });

        static ResourceSpans CreateResourceSpans(string resourceName, params Span[] spans)
        {
            return new ResourceSpans
            {
                Resource = CreateResource(name: resourceName, instanceId: resourceName),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { spans }
                    }
                }
            };
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Render_AtTraceLimit_LimitMessageOnlyDisplayedForLiveRun(bool isReadOnly, int expectedMessageCount)
    {
        SetupTracesServices();
        Services.AddSingleton<IOptions<DashboardOptions>>(Options.Create(new DashboardOptions
        {
            TelemetryLimits = { MaxTraceCount = 1 }
        }));
        var timestamp = DateTime.UnixEpoch;
        await FluentUISetupHelpers.ConfigureTelemetryRepository(this, isReadOnly, telemetryRepository => telemetryRepository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "trace", spanId: "span", startTime: timestamp, endTime: timestamp.AddSeconds(1)) }
                    }
                }
            }
        }));
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        Services.GetRequiredService<DimensionManager>().InvokeOnViewportInformationChanged(viewport);
        var cut = FluentUISetupHelpers.RenderMessageBarProviderWithPage<Traces>(this, viewport);

        var grid = cut.FindComponent<AspireFluentDataGrid<TraceSummary>>();
        await grid.InvokeAsync(grid.Instance.RefreshDataAndRenderAsync);
        cut.WaitForAssertion(() => Assert.Equal(expectedMessageCount, cut.FindComponents<DashboardMessageBar>().Count));
    }

    private void SetupTracesServices()
    {
        FluentUISetupHelpers.SetupFluentOverflow(this);
        FluentUISetupHelpers.SetupFluentDivider(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentSearch(this);
        FluentUISetupHelpers.SetupFluentKeyCode(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentToolbar(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        JSInterop.SetupVoid("focusElement", _ => true);

        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<ILogger<Traces>>(NullLogger<Traces>.Instance);
        Services.AddTransient<TracesViewModel>();
    }
}
