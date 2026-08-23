// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Serialization;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class DashboardJsonSerializerContextTests
{
    [Fact]
    public void OverflowChangedEventArgs_UsesGeneratedMetadata()
    {
        const string json =
            """{"id":"toolbar","items":[],"overflowCount":0,"firstOverflowIndex":-1,"orderedItemIds":[]}""";

        var value = JsonSerializer.Deserialize(
            json,
            DashboardJsonSerializerContext.Default.OverflowChangedEventArgs);

        Assert.IsType<OverflowChangedEventArgs>(value);
        Assert.Equal("toolbar", value.Id);
        Assert.NotNull(value.Items);
        Assert.Empty(value.Items);
        Assert.Equal(0, value.OverflowCount);
        Assert.Equal(-1, value.FirstOverflowIndex);
        Assert.NotNull(value.OrderedItemIds);
        Assert.Empty(value.OrderedItemIds);
    }

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
}
