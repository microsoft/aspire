// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Tests.Shared.Telemetry;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ColumnValueFilterTests
{
    private static OtlpContext CreateContext() => new() { Logger = NullLogger.Instance, Options = new() };

    private static OtlpLogEntry CreateLogEntry(string resourceName, string severity)
    {
        var context = CreateContext();
        var resource = new OtlpResource(resourceName, "instance", uninstrumentedPeer: false, context);
        var resourceView = new OtlpResourceView(resource, new RepeatedField<OpenTelemetry.Proto.Common.V1.KeyValue>());
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        var logRecord = TelemetryTestHelpers.CreateLogRecord(severity: severity switch
        {
            "Information" => OpenTelemetry.Proto.Logs.V1.SeverityNumber.Info,
            "Warning" => OpenTelemetry.Proto.Logs.V1.SeverityNumber.Warn,
            "Error" => OpenTelemetry.Proto.Logs.V1.SeverityNumber.Error,
            _ => OpenTelemetry.Proto.Logs.V1.SeverityNumber.Info,
        });
        return new OtlpLogEntry(logRecord, resourceView, scope, context);
    }

    [Fact]
    public void Apply_AllChecked_ReturnsAllEntries()
    {
        var values = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        values["frontend"] = true;
        values["backend"] = true;

        var filter = new ColumnValueFilter(
            values,
            logValueExtractor: entry => entry.ResourceView.Resource.ResourceName);

        var entries = new[]
        {
            CreateLogEntry("frontend", "Information"),
            CreateLogEntry("backend", "Warning"),
        };

        var result = filter.Apply(entries).ToList();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Apply_PartialChecked_FiltersOutUnchecked()
    {
        var values = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        values["frontend"] = true;
        values["backend"] = false;

        var filter = new ColumnValueFilter(
            values,
            logValueExtractor: entry => entry.ResourceView.Resource.ResourceName);
        filter.RecalculateIsUnfiltered();

        var entries = new[]
        {
            CreateLogEntry("frontend", "Information"),
            CreateLogEntry("backend", "Warning"),
        };

        var result = filter.Apply(entries).ToList();

        Assert.Single(result);
        Assert.Equal("frontend", result[0].ResourceView.Resource.ResourceName);
    }

    [Fact]
    public void Apply_EmptyDictionary_ReturnsAllEntries()
    {
        var values = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var filter = new ColumnValueFilter(
            values,
            logValueExtractor: entry => entry.ResourceView.Resource.ResourceName);

        var entries = new[]
        {
            CreateLogEntry("frontend", "Information"),
        };

        var result = filter.Apply(entries).ToList();

        Assert.Single(result);
    }

    [Fact]
    public void RecalculateIsUnfiltered_UpdatesCachedState()
    {
        var values = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        values["frontend"] = true;
        values["backend"] = true;

        var filter = new ColumnValueFilter(
            values,
            logValueExtractor: entry => entry.ResourceView.Resource.ResourceName);

        // Initially all checked — filter should return all entries
        var entries = new[] { CreateLogEntry("frontend", "Information"), CreateLogEntry("backend", "Warning") };
        Assert.Equal(2, filter.Apply(entries).Count());

        // Uncheck one value and recalculate
        values["backend"] = false;
        filter.RecalculateIsUnfiltered();

        Assert.Single(filter.Apply(entries));
    }

    [Fact]
    public void Apply_UnknownValue_IsVisible()
    {
        var values = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        values["frontend"] = true;
        values["backend"] = false;

        var filter = new ColumnValueFilter(
            values,
            logValueExtractor: entry => entry.ResourceView.Resource.ResourceName);
        filter.RecalculateIsUnfiltered();

        var entries = new[]
        {
            CreateLogEntry("frontend", "Information"),
            CreateLogEntry("backend", "Warning"),
            CreateLogEntry("unknown-service", "Error"),
        };

        var result = filter.Apply(entries).ToList();

        // "unknown-service" isn't in the dictionary. Unknown values default to visible so telemetry from
        // not-yet-seeded sources (e.g. a newly discovered resource) isn't silently dropped. "backend" is
        // explicitly unchecked, so it is filtered out.
        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.ResourceView.Resource.ResourceName == "frontend");
        Assert.Contains(result, e => e.ResourceView.Resource.ResourceName == "unknown-service");
        Assert.DoesNotContain(result, e => e.ResourceView.Resource.ResourceName == "backend");
    }
}
