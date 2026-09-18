// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;

namespace Aspire.Confluent.Kafka.Tests;

internal sealed class TestActivityExporter : BaseExporter<Activity>
{
    // The Kafka ActivitySource is shared with other tests, which can export concurrently.
    private readonly ConcurrentQueue<Activity> _activities = new();

    public Activity[] GetActivities() => _activities.ToArray();

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            _activities.Enqueue(activity);
        }

        return ExportResult.Success;
    }
}
