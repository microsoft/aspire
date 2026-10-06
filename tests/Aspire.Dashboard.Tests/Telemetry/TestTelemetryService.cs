// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.Logging;

namespace Aspire.Dashboard.Tests.Telemetry;

internal sealed class TestTelemetryService(DashboardTelemetryFixture fixture)
    : AspireTelemetryBase(fixture.LoggerFactory.CreateLogger<TestTelemetryService>(), fixture.EventLogger,
        fixture.ActivitySourceName, fixture.DiagnosticsActivitySourceName, "test-error")
{
    public Activity? StartOperation(IEnumerable<KeyValuePair<string, object?>> properties)
    {
        var activity = StartReportedActivity("test-operation");
        if (activity is not null)
        {
            AddReportedActivityProperties(activity, properties);
        }

        return activity;
    }

    public void RecordEvent(IEnumerable<KeyValuePair<string, object?>> properties) => RecordEventCore("test-event", properties);

    protected override IReadOnlyList<KeyValuePair<string, object?>> GetDefaultTags() =>
    [
        new("allowed.default", "default"),
        new("secret.default", "private default")
    ];

    protected override bool TrySanitizeProperty(string key, object? value, out object? sanitizedValue)
    {
        sanitizedValue = value is string text ? text.ToUpperInvariant() : value;
        return key is "allowed.default" or "allowed.property" or "exception.type";
    }
}
