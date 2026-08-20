// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Tests;

/// <summary>
/// Provides factory methods for creating <see cref="ActivityListener"/> instances scoped to a
/// specific <see cref="ActivitySource"/>. Using instance-based filtering (via
/// <see cref="object.ReferenceEquals"/>) instead of name-based filtering prevents activities from
/// parallel tests that use the same source name from leaking into the listener.
/// </summary>
public static class ActivityListenerHelper
{
    /// <summary>
    /// Creates an <see cref="ActivityListener"/> that enables sampling on the specified
    /// <paramref name="targetSource"/>, optionally invoking callbacks when activities start or stop.
    /// </summary>
    public static ActivityListener Create(ActivitySource targetSource, Action<Activity>? onActivityStarted = null, Action<Activity>? onActivityStopped = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, targetSource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = onActivityStarted,
            ActivityStopped = onActivityStopped
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
