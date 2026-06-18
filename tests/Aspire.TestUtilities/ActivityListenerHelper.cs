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
    /// <paramref name="targetSource"/> without capturing activities.
    /// Use when you only need activity creation to succeed (e.g. <c>Activity.Current</c> must be non-null).
    /// </summary>
    public static ActivityListener Create(ActivitySource targetSource)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, targetSource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// Creates an <see cref="ActivityListener"/> that enables sampling on the specified
    /// <paramref name="targetSource"/> and invokes <paramref name="onActivityStopped"/> when
    /// activities complete.
    /// </summary>
    public static ActivityListener Create(ActivitySource targetSource, Action<Activity> onActivityStopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, targetSource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = onActivityStopped
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// Creates an <see cref="ActivityListener"/> that enables sampling on the specified
    /// <paramref name="targetSource"/> and invokes <paramref name="onActivityStarted"/> when
    /// activities start.
    /// </summary>
    public static ActivityListener CreateWithStarted(ActivitySource targetSource, Action<Activity> onActivityStarted)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, targetSource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = onActivityStarted
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// Creates an <see cref="ActivityListener"/> that enables sampling on the named source.
    /// Use only for test-specific activity sources with unique names that cannot conflict with
    /// parallel tests (e.g. <c>"test-my-scenario"</c>).
    /// </summary>
    public static ActivityListener Create(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// Creates an <see cref="ActivityListener"/> that enables sampling on the named source and
    /// invokes <paramref name="onActivityStopped"/> when activities complete.
    /// Use only for test-specific activity sources with unique names.
    /// </summary>
    public static ActivityListener Create(string sourceName, Action<Activity> onActivityStopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = onActivityStopped
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
