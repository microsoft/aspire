// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Aspire.Tests.Shared.Telemetry;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DefaultInstrumentUnitResolverTests
{
    [Theory]
    [InlineData("By/s", "instrument_name", true, true, "Bytes per second")]
    [InlineData("By/s", "instrument_name", false, true, "bytes per second")]
    [InlineData("{match_attempts}", "aspnetcore.routing.match_attempts", true, true, "Match attempts")]
    [InlineData("{match_attempts}", "aspnetcore.routing.match_attempts", false, true, "match attempts")]
    [InlineData("{Match_Attempt}", "aspnetcore.routing.match_attempts", false, false, "match attempt")]
    [InlineData("connection", "instrument_name", true, true, "Connections")]
    [InlineData("{connection}", "instrument_name", true, true, "Connections")]
    [InlineData("1", "instrument_name", true, true, "")]
    [InlineData("", "instrument_name", true, true, "Localized:PlotlyChartValue")]
    [InlineData("", "instrument_name.count", true, true, "Localized:PlotlyChartCount")]
    [InlineData("", "instrument_name.length", true, true, "Localized:PlotlyChartLength")]
    public void ResolveDisplayedUnit(string unit, string name, bool capitalize, bool pluralize, string expected)
    {
        var localizer = new TestStringLocalizer<ControlsStrings>();
        var resolver = new DefaultInstrumentUnitResolver(localizer);
        var context = TelemetryTestHelpers.CreateContext();
        var resource = new OtlpResource("resource", instanceId: null, uninstrumentedPeer: false, context);

        var otlpInstrumentSummary = new OtlpInstrumentSummary
        {
            Description = "Description!",
            Name = name,
            Parent = TelemetryTestHelpers.CreateOtlpScope(context, name: "meter_name"),
            Type = OtlpInstrumentType.Gauge,
            Unit = unit,
            AggregationTemporality = OtlpAggregationTemporality.Cumulative,
            IsMonotonic = false,
            ResourceView = new OtlpResourceView(resource, Array.Empty<KeyValuePair<string, string>>())
        };

        var result = resolver.ResolveDisplayedUnit(otlpInstrumentSummary, capitalize, pluralize);

        Assert.Equal(expected, result);
    }
}
