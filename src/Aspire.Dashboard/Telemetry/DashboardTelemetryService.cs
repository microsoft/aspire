// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Dashboard.Utils;
using Aspire.Shared;
using Aspire.Shared.Telemetry;

namespace Aspire.Dashboard.Telemetry;

/// <summary>
/// Records dashboard usage and errors independently of an IDE debug session.
/// </summary>
public sealed class DashboardTelemetryService : AspireTelemetryBase
{
    internal const string ReportedActivitySourceName = "Aspire.Dashboard.Reported";
    internal const string DiagnosticsActivitySourceName = ReportedActivitySourceName + ".Diagnostics";
    internal const string EventLogCategoryName = "Aspire.Dashboard.Reported.Events";
    internal const string TelemetryOptOutConfigKey = "ASPIRE_DASHBOARD_TELEMETRY_OPTOUT";
    private readonly DashboardTelemetryConfiguration _configuration;
    private readonly IReadOnlyList<KeyValuePair<string, object?>> _defaultTags;

    /// <summary>
    /// Initializes dashboard product instrumentation with resolved telemetry settings.
    /// </summary>
    /// <param name="logger">The logger for local dashboard errors.</param>
    /// <param name="configuration">The resolved product telemetry settings.</param>
    /// <param name="loggerFactory">The application logger factory for usage events.</param>
    public DashboardTelemetryService(ILogger<DashboardTelemetryService> logger, DashboardTelemetryConfiguration configuration, ILoggerFactory loggerFactory)
        : this(logger, configuration, loggerFactory, ReportedActivitySourceName, DiagnosticsActivitySourceName)
    {
    }

    internal DashboardTelemetryService(ILogger<DashboardTelemetryService> logger, DashboardTelemetryConfiguration configuration, ILoggerFactory loggerFactory, string reportedSourceName, string diagnosticsSourceName)
        : base(logger, loggerFactory.CreateLogger(EventLogCategoryName), reportedSourceName, diagnosticsSourceName, TelemetryEventKeys.Error)
    {
        _configuration = configuration;
        _defaultTags =
        [
            new(TelemetryPropertyKeys.DashboardVersion, AssemblyVersionHelper.GetInformationalVersion(typeof(DashboardWebApplication).Assembly)),
            new(TelemetryPropertyKeys.DashboardBuildId, AssemblyVersionHelper.GetFileVersion(typeof(DashboardWebApplication).Assembly))
        ];
    }

    /// <summary>
    /// Gets whether dashboard product reporting is enabled by the resolved settings.
    /// </summary>
    public bool IsTelemetryEnabled => _configuration.ReportedTelemetryEnabled;

    /// <summary>
    /// Starts a dashboard operation whose duration ends when the caller disposes its activity.
    /// </summary>
    /// <param name="eventName">The operation name.</param>
    /// <param name="startEventProperties">The operation properties.</param>
    /// <returns>The activity, or <see langword="null"/> when telemetry is disabled or not sampled.</returns>
    public Activity? StartOperation(string eventName, Dictionary<string, AspireTelemetryProperty> startEventProperties)
    {
        if (!IsTelemetryEnabled)
        {
            return null;
        }

        var activity = StartReportedActivity(eventName);
        if (activity is not null)
        {
            AddProperties(activity, startEventProperties);
        }

        return activity;
    }

    /// <summary>
    /// Sets an operation's result without ending its activity.
    /// </summary>
    /// <param name="activity">The operation activity, or <see langword="null"/> when not recorded.</param>
    /// <param name="result">The operation result.</param>
    public void SetOperationResult(Activity? activity, TelemetryResult result)
    {
        if (activity is not null)
        {
            SetResult(activity, result);
        }
    }

    /// <summary>
    /// Records a sanitized dashboard event as a structured log and on an active reported activity, if present.
    /// </summary>
    /// <param name="eventName">The event name.</param>
    /// <param name="result">The event result.</param>
    /// <param name="properties">The event properties.</param>
    public void RecordEvent(string eventName, TelemetryResult result, Dictionary<string, AspireTelemetryProperty>? properties = null)
    {
        if (!IsTelemetryEnabled)
        {
            return;
        }

        _ = GetResultStatus(result);
        var attributes = CreateProperties(properties);
        attributes.Add(new("aspire.dashboard.result", result.ToString()));
        base.RecordEvent(eventName, attributes);
    }

    /// <summary>
    /// Records a dashboard fault even when Blazor has already stopped its activity.
    /// </summary>
    /// <param name="message">The local log message.</param>
    /// <param name="exception">The exception to record.</param>
    /// <param name="writeToLogging">Whether to also log the exception locally.</param>
    public void RecordError(string message, Exception exception, bool writeToLogging)
    {
        RecordErrorCore(message, exception, writeToLogging, createActivity: true);
    }

    /// <inheritdoc />
    public override void RecordError(string message, Exception exception) => RecordError(message, exception, writeToLogging: true);

    protected override IReadOnlyList<KeyValuePair<string, object?>> GetDefaultTags() => _defaultTags;

    protected override bool IsReportedTelemetryEnabled => IsTelemetryEnabled;

    protected override ActivityTagsCollection CreateErrorTags(Exception exception) => new()
    {
        // Preserve the IDE bridge's privacy policy: exception messages and stack traces
        // can contain resource names, secrets and workspace paths, so only report the type.
        [TelemetryPropertyKeys.ExceptionType] = exception.GetType().FullName,
        [TelemetryPropertyKeys.ExceptionRuntimeVersion] = VersionHelpers.RuntimeVersion?.ToString() ?? string.Empty
    };

    private void AddProperties(Activity activity, Dictionary<string, AspireTelemetryProperty>? properties)
    {
        foreach (var (key, value) in CreateProperties(properties))
        {
            activity.SetTag(key, value);
        }
    }

    private List<KeyValuePair<string, object?>> CreateProperties(Dictionary<string, AspireTelemetryProperty>? properties)
    {
        List<KeyValuePair<string, object?>> attributes = [.. _defaultTags];
        if (properties is null)
        {
            return attributes;
        }

        foreach (var (key, property) in properties)
        {
            // Product telemetry must not export arbitrary application data. The old IDE
            // bridge enforced a key allowlist and excluded free-form diagnostic fields.
            if (property.PropertyType == AspireTelemetryPropertyType.Pii || !IsAllowedProperty(key))
            {
                continue;
            }

            if (property.PropertyType == AspireTelemetryPropertyType.Metric)
            {
                if (double.TryParse(Convert.ToString(property.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                {
                    attributes.Add(new(key, value));
                }
            }
            else if (property.Value is string text)
            {
                attributes.Add(new(key, text.Length <= 1024 ? text : text[..1024]));
            }
            else if (property.Value is string[] values)
            {
                attributes.Add(new(key, values.Take(100).Select(value => value.Length <= 256 ? value : value[..256]).ToArray()));
            }
            else if (property.Value is bool or int or double)
            {
                attributes.Add(new(key, property.Value));
            }
        }

        return attributes;
    }

    private static bool IsAllowedProperty(string key) => key is
        TelemetryPropertyKeys.DashboardComponentId or TelemetryPropertyKeys.DashboardComponentType or
        TelemetryPropertyKeys.ConsoleLogsShowTimestamp or TelemetryPropertyKeys.MetricsResourceIsReplica or
        TelemetryPropertyKeys.MetricsInstrumentsCount or TelemetryPropertyKeys.MetricsSelectedDuration or
        TelemetryPropertyKeys.MetricsSelectedView or TelemetryPropertyKeys.ResourceTypes or
        TelemetryPropertyKeys.ResourceType or TelemetryPropertyKeys.ResourceView or
        TelemetryPropertyKeys.ErrorRequestId or TelemetryPropertyKeys.StructuredLogsSelectedLogLevel or
        TelemetryPropertyKeys.StructuredLogsFilterCount or TelemetryPropertyKeys.CommandName or
        TelemetryPropertyKeys.TerminalDockTrigger;

    private static void SetResult(Activity activity, TelemetryResult result)
    {
        activity.SetTag("aspire.dashboard.result", result.ToString());
        activity.SetStatus(GetResultStatus(result));
    }

    private static ActivityStatusCode GetResultStatus(TelemetryResult result) => result switch
    {
        TelemetryResult.Success => ActivityStatusCode.Ok,
        TelemetryResult.Failure or TelemetryResult.UserFault => ActivityStatusCode.Error,
        TelemetryResult.None or TelemetryResult.UserCancel => ActivityStatusCode.Unset,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown telemetry result.")
    };
}
