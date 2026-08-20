// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Serialization;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class DashboardJsonSerializerContextTests
{
    [Fact]
    public void PlotlyTraceArray_UsesGeneratedMetadata()
    {
        var value = new[]
        {
            new PlotlyTrace
            {
                Name = "requests",
                X = [new DateTimeOffset(2026, 8, 23, 1, 2, 3, TimeSpan.Zero)],
                Y = [1.5, null],
                Tooltips = ["request", null],
                TraceData = [new PlotlyTraceData("trace-id", "span-id")]
            }
        };

        var json = JsonSerializer.Serialize(
            value,
            DashboardJsonSerializerContext.Default.PlotlyTraceArray);

        Assert.Equal(
            """[{"name":"requests","x":["2026-08-23T01:02:03+00:00"],"y":[1.5,null],"tooltips":["request",null],"traceData":[{"traceId":"trace-id","spanId":"span-id"}]}]""",
            json);
    }

    [Fact]
    public void MetricTableIndices_UsesGeneratedMetadata()
    {
        var json = JsonSerializer.Serialize(
            new List<int> { 1, 3 },
            DashboardJsonSerializerContext.Default.ListInt32);

        Assert.Equal("[1,3]", json);
    }

    [Theory]
    [InlineData(typeof(ConsoleLogs.ConsoleLogsPageState))]
    [InlineData(typeof(Metrics.MetricsPageState))]
    [InlineData(typeof(global::Aspire.Dashboard.Components.Pages.Resources.ResourcesPageState))]
    [InlineData(typeof(StructuredLogs.StructuredLogsPageState))]
    [InlineData(typeof(Traces.TracesPageState))]
    public void PageStateType_UsesGeneratedMetadata(Type pageStateType)
    {
        Assert.NotNull(DashboardJsonSerializerContext.Default.GetTypeInfo(pageStateType));
    }

    [Fact]
    public void TracesPageState_UsesGeneratedMetadata()
    {
        var value = new Traces.TracesPageState
        {
            SelectedResource = "frontend",
            SelectedSpanType = "HTTP",
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = "trace.name",
                    Condition = FilterCondition.Contains,
                    Value = "GET"
                }
            ]
        };

        var json = JsonSerializer.Serialize(
            value,
            DashboardJsonSerializerContext.Default.TracesPageState);
        var result = JsonSerializer.Deserialize(
            json,
            DashboardJsonSerializerContext.Default.TracesPageState);

        Assert.NotNull(result);
        Assert.Equal("frontend", result.SelectedResource);
        Assert.Equal("HTTP", result.SelectedSpanType);
        var filter = Assert.Single(result.Filters);
        Assert.Equal("trace.name", filter.Field);
        Assert.Equal(FilterCondition.Contains, filter.Condition);
        Assert.Equal("GET", filter.Value);
    }
}
