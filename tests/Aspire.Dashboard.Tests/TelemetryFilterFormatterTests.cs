// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model.Otlp;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class TelemetryFilterFormatterTests
{
    [Fact]
    public void RoundTripDurationFilter_UsesInvariantValue()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var serializedFilters = TelemetryFilterFormatter.SerializeFiltersToString([
                new FieldTelemetryFilter
                {
                    Field = KnownTraceFields.DurationField,
                    Condition = FilterCondition.GreaterThanOrEqual,
                    Value = "12.5"
                }
            ]);

            var filters = TelemetryFilterFormatter.DeserializeFiltersFromString(serializedFilters);

            var filter = Assert.Single(filters);

            Assert.Equal(KnownTraceFields.DurationField, filter.Field);
            Assert.Equal(FilterCondition.GreaterThanOrEqual, filter.Condition);
            Assert.Equal("12.5", filter.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void RoundTripFilterWithColon()
    {
        var serializedFilters = TelemetryFilterFormatter.SerializeFiltersToString([
            new FieldTelemetryFilter
            {
                Field = "test:name",
                Condition = FilterCondition.Equals,
                Value = "test:value"
            }
        ]);

        var filters = TelemetryFilterFormatter.DeserializeFiltersFromString(serializedFilters);

        var filter = Assert.Single(filters);

        Assert.Equal("test:name", filter.Field);
        Assert.Equal("test:value", filter.Value);
    }

    [Fact]
    public void RoundTripFiltersWithPluses()
    {
        var serializedFilters = TelemetryFilterFormatter.SerializeFiltersToString([
            new FieldTelemetryFilter
            {
                Field = "test+name",
                Condition = FilterCondition.Equals,
                Value = "test+value"
            }
        ]);

        var filters = TelemetryFilterFormatter.DeserializeFiltersFromString(serializedFilters);

        var filter = Assert.Single(filters);

        Assert.Equal("test+name", filter.Field);
        Assert.Equal("test+value", filter.Value);
    }

    [Fact]
    public void RoundTripFilterWithColon_Disabled()
    {
        var serializedFilters = TelemetryFilterFormatter.SerializeFiltersToString([
            new FieldTelemetryFilter
            {
                Field = "test:name",
                Condition = FilterCondition.Equals,
                Value = "test:value",
                Enabled = false
            }
        ]);

        var filters = TelemetryFilterFormatter.DeserializeFiltersFromString(serializedFilters);

        var filter = Assert.Single(filters);

        Assert.Equal("test:name", filter.Field);
        Assert.Equal("test:value", filter.Value);
        Assert.False(filter.Enabled);
    }
}
