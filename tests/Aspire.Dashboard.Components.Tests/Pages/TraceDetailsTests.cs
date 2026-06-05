// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public partial class TraceDetailsTests : DashboardTestContext
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _testOutputHelper;

    public TraceDetailsTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public void Render_HasTrace_SubscriptionRemovedOnDispose()
    {
        // Arrange
        SetupTraceDetailsServices();

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
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
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        // Act
        var traceId = Convert.ToHexString(Encoding.UTF8.GetBytes("1"));
        var cut = RenderComponent<TraceDetail>(builder =>
        {
            builder.Add(p => p.TraceId, traceId);
            builder.AddCascadingValue(viewport);
        });

        // Assert
        Assert.Collection(telemetryRepository.TracesSubscriptions, t =>
        {
            Assert.Equal(nameof(TelemetryRepository.OnNewTraces), t.Name);
        });

        DisposeComponents();

        Assert.Empty(telemetryRepository.TracesSubscriptions);
    }

    [Fact]
    public async Task Render_ChangeTrace_RowsRendered()
    {
        // Arrange
        var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(_testOutputHelper);
        var logger = loggerFactory.CreateLogger(nameof(Render_ChangeTrace_RowsRendered));

        SetupTraceDetailsServices(loggerFactory: loggerFactory);

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
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
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(6), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        // Act
        var traceId = Convert.ToHexString(Encoding.UTF8.GetBytes("1"));
        var cut = RenderComponent<TraceDetail>(builder =>
        {
            builder.Add(p => p.TraceId, traceId);
            builder.AddCascadingValue(viewport);
        });

        // Assert
        logger.LogInformation($"Assert row count for '{traceId}'");
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
        {
            var grid = cut.FindComponent<FluentDataGrid<SpanWaterfallViewModel>>();
            var rows = grid.FindAll(".fluent-data-grid-row");
            return rows.Count == 3;
        }, "Expected rows to be rendered.", logger);

        traceId = Convert.ToHexString(Encoding.UTF8.GetBytes("2"));
        cut.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.TraceId, traceId);
        });

        logger.LogInformation($"Assert row count for '{traceId}'");
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
        {
            var grid = cut.FindComponent<FluentDataGrid<SpanWaterfallViewModel>>();
            var rows = grid.FindAll(".fluent-data-grid-row");
            return rows.Count == 2;
        }, "Expected rows to be rendered.", logger);
    }

    [Fact]
    public async Task Render_TraceUpdateWithNewSpans_RowsRendered()
    {
        // Arrange
        var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(_testOutputHelper);
        var logger = loggerFactory.CreateLogger(nameof(Render_ChangeTrace_RowsRendered));

        SetupTraceDetailsServices(loggerFactory: loggerFactory);

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
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
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(6), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        // Act
        var traceId = Convert.ToHexString(Encoding.UTF8.GetBytes("1"));
        var cut = RenderComponent<TraceDetail>(builder =>
        {
            builder.Add(p => p.TraceId, traceId);
            builder.AddCascadingValue(viewport);
        });

        // Assert
        logger.LogInformation($"Assert row count for '{traceId}'");
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
        {
            var grid = cut.FindComponent<FluentDataGrid<SpanWaterfallViewModel>>();
            var rows = grid.FindAll(".fluent-data-grid-row");
            return rows.Count == 3;
        }, "Expected rows to be rendered.", logger);

        telemetryRepository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
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
                            CreateSpan(traceId: "1", spanId: "1-3", startTime: s_testTime.AddMinutes(6), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-2"),
                        }
                    }
                }
            }
        });

        logger.LogInformation($"Assert updated row count for '{traceId}'");
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
        {
            var grid = cut.FindComponent<FluentDataGrid<SpanWaterfallViewModel>>();
            var rows = grid.FindAll(".fluent-data-grid-row");
            return rows.Count == 4;
        }, "Expected rows to be rendered.", logger);
    }

    [Fact]
    public async Task Render_UpdateDifferentTrace_TraceNotUpdated()
    {
        // Arrange
        var testSink = new TestSink();
        var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(_testOutputHelper, testSink: testSink);
        var logger = loggerFactory.CreateLogger(nameof(Render_ChangeTrace_RowsRendered));

        SetupTraceDetailsServices(loggerFactory: loggerFactory);

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
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
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(6), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        // Act
        var traceId = Convert.ToHexString(Encoding.UTF8.GetBytes("1"));
        var cut = RenderComponent<TraceDetail>(builder =>
        {
            builder.Add(p => p.TraceId, traceId);
            builder.AddCascadingValue(viewport);
        });

        // Assert
        logger.LogInformation($"Assert row count for '{traceId}'");
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
        {
            var grid = cut.FindComponent<FluentDataGrid<SpanWaterfallViewModel>>();
            var rows = grid.FindAll(".fluent-data-grid-row");
            return rows.Count == 3;
        }, "Expected rows to be rendered.", logger);

        logger.LogInformation($"Adding span for difference trace");
        telemetryRepository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
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
                            CreateSpan(traceId: "2", spanId: "2-2", startTime: s_testTime.AddMinutes(7), endTime: s_testTime.AddMinutes(10), parentSpanId: "2-1"),
                        }
                    }
                }
            }
        });

        logger.LogInformation($"Assert not updated for '{traceId}'");
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
        {
            return testSink.Writes.Any(w => w.Message?.Contains($"Trace '{traceId}' is unchanged.") ?? false);
        }, "Expected trace not updated.", logger);
    }

    [Fact]
    public async Task Render_SpansOrderedByStartTime_RowsRenderedInCorrectOrder()
    {
        // Arrange
        SetupTraceDetailsServices();

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddTraces(new AddContext(),
            new RepeatedField<ResourceSpans>
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
                                CreateSpan(traceId: "1", spanId: "1-1",
                                    startTime: s_testTime.AddMinutes(1),
                                    endTime: s_testTime.AddMinutes(10)),
                                CreateSpan(traceId: "1", spanId: "2-1",
                                    startTime: s_testTime.AddMinutes(1),
                                    endTime: s_testTime.AddMinutes(10),
                                    parentSpanId: "1-1"),
                                CreateSpan(traceId: "1", spanId: "3-1",
                                    startTime: s_testTime.AddMinutes(1),
                                    endTime: s_testTime.AddMinutes(10),
                                    parentSpanId: "2-1"),
                                CreateSpan(traceId: "1", spanId: "3-3",
                                    startTime: s_testTime.AddMinutes(3),
                                    endTime: s_testTime.AddMinutes(5),
                                    parentSpanId: "2-1"),
                                CreateSpan(traceId: "1", spanId: "3-2",
                                    startTime: s_testTime.AddMinutes(2),
                                    endTime: s_testTime.AddMinutes(6),
                                    parentSpanId: "2-1")
                            }
                        }
                    }
                }
            });

        // Act
        var traceId = Convert.ToHexString(Encoding.UTF8.GetBytes("1"));
        var cut = RenderComponent<TraceDetail>(builder =>
        {
            builder.Add(p => p.TraceId, traceId);
            builder.AddCascadingValue(viewport);
        });

        var data = await cut.Instance.GetData(new GridItemsProviderRequest<SpanWaterfallViewModel>());

        // Assert
        Assert.Collection(data.Items,
            item => Assert.Equal("Test span. Id: 1-1", item.Span.Name),
            item => Assert.Equal("Test span. Id: 2-1", item.Span.Name),
            item => Assert.Equal("Test span. Id: 3-1", item.Span.Name),
            item => Assert.Equal("Test span. Id: 3-2", item.Span.Name),
            item => Assert.Equal("Test span. Id: 3-3", item.Span.Name));
    }

    [Fact]
    public void ToggleCollapse_SpanStateChanges()
    {
        // Arrange
        SetupTraceDetailsServices();

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddTraces(new AddContext(),
            new RepeatedField<ResourceSpans>
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
                                CreateSpan(traceId: "1", spanId: "1-1",
                                    startTime: s_testTime.AddMinutes(1),
                                    endTime: s_testTime.AddMinutes(10)),
                                CreateSpan(traceId: "1", spanId: "2-1",
                                    startTime: s_testTime.AddMinutes(5),
                                    endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                                CreateSpan(traceId: "1", spanId: "3-1",
                                    startTime: s_testTime.AddMinutes(6),
                                    endTime: s_testTime.AddMinutes(10), parentSpanId: "2-1")
                            }
                        }
                    }
                }
            });

        var traceId = Convert.ToHexString(Encoding.UTF8.GetBytes("1"));
        var cut = RenderComponent<TraceDetail>(builder =>
        {
            builder.Add(p => p.TraceId, traceId);
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".main-grid-expand-button").Count));
        // Act and assert

        // Collapse the middle span
        cut.FindAll(".main-grid-expand-button")[1].Click();

        cut.WaitForAssertion(() =>
        {
            var expandContainers = cut.FindAll(".main-grid-expand-container");
            // There should now be two containers since the 3rd level element should now be filtered out
            Assert.Collection(expandContainers,
                container => Assert.True(container.ClassList.Contains("main-grid-expanded")),
                container => Assert.True(container.ClassList.Contains("main-grid-collapsed")));
        });

        // Collapse the parent span
        cut.FindAll(".main-grid-expand-button")[0].Click();
        cut.WaitForAssertion(() =>
        {
            var expandContainers = cut.FindAll(".main-grid-expand-container");
            // There should now be one container since the 2nd level element should now be filtered out
            Assert.Collection(expandContainers,
                container => Assert.True(container.ClassList.Contains("main-grid-collapsed")));
        });

        // Expand the parent span, we should now see the same two containers as before
        cut.FindAll(".main-grid-expand-button")[0].Click();
        cut.WaitForAssertion(() =>
        {
            var expandContainers = cut.FindAll(".main-grid-expand-container");
            // There should now be two containers since the 3rd level element should now be filtered out
            Assert.Collection(expandContainers,
                container => Assert.True(container.ClassList.Contains("main-grid-expanded")),
                container => Assert.True(container.ClassList.Contains("main-grid-collapsed")));
        });
    }

    [Fact]
    public void CollapseAllSpans_CollapsesAllSpans()
    {
        // Arrange
        SetupTraceDetailsServices();

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddTraces(new AddContext(),
            new RepeatedField<ResourceSpans>
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
                                CreateSpan(traceId: "1", spanId: "1-1",
                                    startTime: s_testTime.AddMinutes(1),
                                    endTime: s_testTime.AddMinutes(10)),
                                CreateSpan(traceId: "1", spanId: "2-1",
                                    startTime: s_testTime.AddMinutes(5),
                                    endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                                CreateSpan(traceId: "1", spanId: "3-1",
                                    startTime: s_testTime.AddMinutes(6),
                                    endTime: s_testTime.AddMinutes(10), parentSpanId: "2-1")
                            }
                        }
                    }
                }
            });

        var traceId = Convert.ToHexString(Encoding.UTF8.GetBytes("1"));
        var cut = RenderComponent<TraceDetail>(builder =>
        {
            builder.Add(p => p.TraceId, traceId);
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".main-grid-expand-button").Count));

        // Act - Find the dropdown menu and click Collapse All
        var menuButton = cut.FindComponent<AspireMenuButton>();
        var collapseAllMenuItem = menuButton.Instance.Items.FirstOrDefault(item => item.Text == "Collapse all"); // Locate by text since ID was removed
        Assert.NotNull(collapseAllMenuItem);
        cut.InvokeAsync(() => collapseAllMenuItem!.OnClick?.Invoke() ?? Task.CompletedTask);

        // Assert
        cut.WaitForAssertion(() =>
        {
            var expandContainers = cut
                .FindAll(".main-grid-expand-container")
                .Where(c => c.ParentElement?.QuerySelector(".main-grid-expand-button") != null)
                .ToList();

            for (var i = 0; i < expandContainers.Count; i++)
            {
                // The first container should be expanded
                // All other containers should be collapsed
                var expectedClass = (i == 0)
                    ? "main-grid-expanded"
                    : "main-grid-collapsed";

                Assert.True(expandContainers[i].ClassList.Contains(expectedClass));
            }
        });
    }

    [Fact]
    public void ExpandAllSpans_ExpandsAllSpans()
    {
        // Arrange
        SetupTraceDetailsServices();

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddTraces(new AddContext(),
            new RepeatedField<ResourceSpans>
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
                                CreateSpan(traceId: "1", spanId: "1-1",
                                    startTime: s_testTime.AddMinutes(1),
                                    endTime: s_testTime.AddMinutes(10)),
                                CreateSpan(traceId: "1", spanId: "2-1",
                                    startTime: s_testTime.AddMinutes(5),
                                    endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                                CreateSpan(traceId: "1", spanId: "3-1",
                                    startTime: s_testTime.AddMinutes(6),
                                    endTime: s_testTime.AddMinutes(10), parentSpanId: "2-1")
                            }
                        }
                    }
                }
            });

        var traceId = Convert.ToHexString(Encoding.UTF8.GetBytes("1"));
        var cut = RenderComponent<TraceDetail>(builder =>
        {
            builder.Add(p => p.TraceId, traceId);
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".main-grid-expand-button").Count));

        // First click "Collapse All" to collapse everything
        var menuButton = cut.FindComponent<AspireMenuButton>();
        var collapseAllMenuItem = menuButton.Instance.Items.FirstOrDefault(item => item.Text == "Collapse all"); // Locate by text since ID was removed
        Assert.NotNull(collapseAllMenuItem);
        cut.InvokeAsync(() => collapseAllMenuItem!.OnClick?.Invoke() ?? Task.CompletedTask);

        // Wait for spans to collapse
        cut.WaitForAssertion(() =>
        {
            var expandContainers = cut.FindAll(".main-grid-expand-container");
            // At least one span should be collapsed
            Assert.Contains(expandContainers, container => container.ClassList.Contains("main-grid-collapsed"));
        });

        // Act - Click "Expand All"
        var expandAllMenuItem = menuButton.Instance.Items.FirstOrDefault(item => item.Text == "Expand all"); // Locate by text since ID was removed
        Assert.NotNull(expandAllMenuItem);
        cut.InvokeAsync(() => expandAllMenuItem!.OnClick?.Invoke() ?? Task.CompletedTask);

        // Assert
        cut.WaitForAssertion(() =>
        {
            var expandContainers = cut.FindAll(".main-grid-expand-container");
            // All containers should now be expanded
            foreach (var container in expandContainers)
            {
                Assert.True(container.ClassList.Contains("main-grid-expanded"));
            }
        });
    }

    private void SetupTraceDetailsServices(ILoggerFactory? loggerFactory = null)
    {
        FluentUISetupHelpers.SetupFluentOverflow(this);
        FluentUISetupHelpers.SetupFluentDivider(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentList(this);

        FluentUISetupHelpers.SetupFluentSearch(this);
        FluentUISetupHelpers.SetupFluentKeyCode(this);
        FluentUISetupHelpers.SetupFluentToolbar(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        JSInterop.SetupVoid("initializeContinuousScroll");

        loggerFactory ??= NullLoggerFactory.Instance;

        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<ILoggerFactory>(loggerFactory);
    }
}
