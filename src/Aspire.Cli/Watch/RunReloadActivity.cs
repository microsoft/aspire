// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Watch;

/// <summary>
/// The telemetry of one accepted Ctrl+R reload, from the key press until its outcome is known.
/// </summary>
/// <remarks>
/// An AppHost reload outlives the AppHost that accepted it: that AppHost is torn down, and its replacement completes
/// the reload once it starts or fails. The activity is therefore stopped from whichever flow learns the outcome, which
/// may not be the flow that started it.
/// </remarks>
internal sealed class RunReloadActivity
{
    private readonly AspireCliTelemetry _telemetry;
    private readonly Activity? _activity;
    private int _completed;

    private RunReloadActivity(AspireCliTelemetry telemetry, Activity? activity)
    {
        _telemetry = telemetry;
        _activity = activity;
    }

    internal static RunReloadActivity Start(AspireCliTelemetry telemetry, Activity? parent, RunReloadMode mode)
    {
        var activity = StartWithoutChangingCurrent(telemetry, TelemetryConstants.Activities.RunAppHostReload, parent);
        telemetry.SetActivityProperty(activity, TelemetryConstants.Tags.RunReloadMode, ToTagValue(mode));
        return new(telemetry, activity);
    }

    /// <summary>Records how the reload ended. Only the first outcome is recorded.</summary>
    /// <param name="outcome">One of the <see cref="TelemetryConstants.RunReloadOutcomes"/> values.</param>
    internal void Complete(string outcome)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _telemetry.SetActivityProperty(_activity, TelemetryConstants.Tags.RunReloadOutcome, outcome);
        _telemetry.SetActivityProperty(_activity, TelemetryConstants.Tags.RunReloadSuccess,
            TelemetryConstants.RunReloadOutcomes.IsSuccess(outcome));
        StopWithoutChangingCurrent(_activity);
    }

    internal static string ToTagValue(RunReloadMode mode) => mode switch
    {
        RunReloadMode.AppHost => "apphost",
        RunReloadMode.Services => "services",
        _ => "none"
    };

    /// <summary>
    /// Starts a reported activity under <paramref name="parent"/> without making it <see cref="Activity.Current"/>.
    /// </summary>
    /// <remarks>
    /// Reload activities outlive the code that starts them, so they must not become the parent of unrelated activities
    /// that the caller starts afterwards.
    /// </remarks>
    internal static Activity? StartWithoutChangingCurrent(AspireCliTelemetry telemetry, string name, Activity? parent)
    {
        var current = Activity.Current;
        try
        {
            return parent is null
                ? telemetry.StartReportedActivity(name)
                : telemetry.StartReportedActivity(name, ActivityKind.Internal, parent.Context);
        }
        finally
        {
            Activity.Current = current;
        }
    }

    /// <summary>
    /// Stops <paramref name="activity"/> while keeping <see cref="Activity.Current"/>, which may belong to an unrelated
    /// operation of the flow that learned the outcome.
    /// </summary>
    internal static void StopWithoutChangingCurrent(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        var current = Activity.Current;
        activity.Dispose();
        if (!ReferenceEquals(current, activity))
        {
            Activity.Current = current;
        }
    }
}
