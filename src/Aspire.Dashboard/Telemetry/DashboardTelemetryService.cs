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
    private readonly DashboardTelemetryConfiguration _configuration;
    private readonly TelemetryTagsSource _tagsSource;

    /// <summary>
    /// Initializes dashboard product instrumentation with resolved telemetry settings.
    /// </summary>
    /// <param name="logger">The logger for local dashboard errors.</param>
    /// <param name="configuration">The resolved product telemetry settings.</param>
    /// <param name="loggerFactory">The logger factory for metadata detection diagnostics.</param>
    public DashboardTelemetryService(ILogger<DashboardTelemetryService> logger, DashboardTelemetryConfiguration configuration, ILoggerFactory loggerFactory)
        : this(logger, configuration, CreateTagsSource(configuration, loggerFactory), ReportedActivitySourceName, DiagnosticsActivitySourceName)
    {
    }

    internal DashboardTelemetryService(ILogger<DashboardTelemetryService> logger, DashboardTelemetryConfiguration configuration,
        TelemetryTagsSource tagsSource, string reportedSourceName, string diagnosticsSourceName)
        : base(logger, reportedSourceName, diagnosticsSourceName, TelemetryEventKeys.Error)
    {
        _configuration = configuration;
        _tagsSource = tagsSource;
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
            AddReportedActivityProperties(activity, properties: null);
            SetActivityProperties(activity, startEventProperties);
        }

        return activity;
    }

    /// <summary>
    /// Sets classified dashboard activity properties using the shared privacy policy.
    /// </summary>
    /// <param name="activity">The activity, or <see langword="null"/> when not recorded.</param>
    /// <param name="properties">The classified dashboard properties.</param>
    /// <exception cref="ArgumentNullException">The properties collection is null.</exception>
    /// <exception cref="ArgumentException">A property name is empty or whitespace.</exception>
    public void SetActivityProperties(Activity? activity, IReadOnlyDictionary<string, AspireTelemetryProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        base.SetActivityProperties(activity, GetProperties(properties));
    }

    /// <summary>
    /// Sets an operation's status without ending its activity.
    /// </summary>
    /// <param name="activity">The operation activity, or <see langword="null"/> when not recorded.</param>
    /// <param name="status">The operation status.</param>
    public void SetOperationStatus(Activity? activity, ActivityStatusCode status)
    {
        if (activity is not null)
        {
            // Keep the legacy result values for existing telemetry queries while using
            // OpenTelemetry's standard status code as the API and activity status.
            var result = status switch
            {
                ActivityStatusCode.Ok => "Success",
                ActivityStatusCode.Error => "Failure",
                ActivityStatusCode.Unset => "None",
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown activity status.")
            };
            SetActivityProperty(activity, "aspire.dashboard.result", result);
            activity.SetStatus(status);
        }
    }

    /// <summary>
    /// Records a sanitized dashboard event as a structured log.
    /// </summary>
    /// <param name="eventName">The event name.</param>
    /// <param name="properties">The event properties.</param>
    public void RecordEvent(string eventName, Dictionary<string, AspireTelemetryProperty>? properties = null)
    {
        if (!IsTelemetryEnabled)
        {
            return;
        }

        RecordEventCore(eventName, GetProperties(properties));
    }

    /// <summary>
    /// Records a dashboard fault as a sanitized structured log.
    /// </summary>
    /// <param name="message">The local log message.</param>
    /// <param name="exception">The exception to record.</param>
    /// <param name="writeToLogging">Whether to also log the exception locally.</param>
    public void RecordError(string message, Exception exception, bool writeToLogging)
    {
        RecordErrorCore(message, exception, writeToLogging);
    }

    /// <inheritdoc />
    public override void RecordError(string message, Exception exception) => RecordError(message, exception, writeToLogging: true);

    protected override IReadOnlyList<KeyValuePair<string, object?>> GetDefaultTags() => _tagsSource.GetResolvedTags();

    protected override bool IsReportedTelemetryEnabled => IsTelemetryEnabled;

    protected override ActivityTagsCollection CreateErrorTags(Exception exception) => new()
    {
        // Preserve the IDE bridge's privacy policy: exception messages and stack traces
        // can contain resource names, secrets and workspace paths, so only report the type.
        [TelemetryPropertyKeys.ExceptionType] = exception.GetType().FullName,
        [TelemetryPropertyKeys.ExceptionRuntimeVersion] = VersionHelpers.RuntimeVersion?.ToString() ?? string.Empty
    };

    /// <inheritdoc />
    protected override bool TrySanitizeProperty(string key, object? value, out object? sanitizedValue)
    {
        sanitizedValue = null;
        if (value is AspireTelemetryProperty property)
        {
            // Product telemetry must not export arbitrary application data. The old IDE
            // bridge enforced a key allowlist and excluded free-form diagnostic fields.
            if (property.PropertyType == AspireTelemetryPropertyType.Pii || !IsAllowedProperty(key))
            {
                return false;
            }

            if (property.PropertyType == AspireTelemetryPropertyType.Metric)
            {
                if (double.TryParse(Convert.ToString(property.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                {
                    sanitizedValue = number;
                    return true;
                }

                return false;
            }

            value = property.Value;
        }
        else if (key is not (TelemetryPropertyKeys.DashboardVersion or TelemetryPropertyKeys.DashboardBuildId or
            TelemetryPropertyKeys.DeploymentEnvironmentName or TelemetryPropertyKeys.OsName or TelemetryPropertyKeys.OsType or TelemetryPropertyKeys.OsVersion or
            TelemetryPropertyKeys.MacAddressHash or TelemetryPropertyKeys.DeviceId or TelemetryPropertyKeys.InternalMicrosoft or
            TelemetryPropertyKeys.InternalMicrosoftSource or TelemetryPropertyKeys.InternalMicrosoftAlias or TelemetryPropertyKeys.InternalMicrosoftDomain or
            TelemetryPropertyKeys.ExceptionType or TelemetryPropertyKeys.ExceptionRuntimeVersion or "aspire.dashboard.result"))
        {
            return false;
        }

        sanitizedValue = value switch
        {
            string text => text.Length <= 1024 ? text : text[..1024],
            bool or int or double => value,
            _ => null
        };

        return sanitizedValue is not null;
    }

    private static IEnumerable<KeyValuePair<string, object?>> GetProperties(IReadOnlyDictionary<string, AspireTelemetryProperty>? properties)
    {
        if (properties is not null)
        {
            foreach (var (key, property) in properties)
            {
                yield return new(key, property);
            }
        }
    }

    private static bool IsAllowedProperty(string key) => key is
        TelemetryPropertyKeys.StartupSuccess or TelemetryPropertyKeys.StartupLaunchContext or
        TelemetryPropertyKeys.StartupPersistenceMode or TelemetryPropertyKeys.StartupDurationMilliseconds or TelemetryPropertyKeys.ErrorType or
        TelemetryPropertyKeys.DashboardComponentId or TelemetryPropertyKeys.DashboardComponentType or
        TelemetryPropertyKeys.ConsoleLogsShowTimestamp or TelemetryPropertyKeys.MetricsResourceIsReplica or
        TelemetryPropertyKeys.MetricsInstrumentsCount or TelemetryPropertyKeys.MetricsSelectedDuration or
        TelemetryPropertyKeys.MetricsSelectedView or TelemetryPropertyKeys.ResourceType or TelemetryPropertyKeys.ResourceView or
        TelemetryPropertyKeys.ErrorRequestId or TelemetryPropertyKeys.StructuredLogsSelectedLogLevel or
        TelemetryPropertyKeys.StructuredLogsFilterCount or TelemetryPropertyKeys.CommandName or
        TelemetryPropertyKeys.TerminalDockTrigger;

    private static TelemetryTagsSource CreateTagsSource(DashboardTelemetryConfiguration configuration, ILoggerFactory loggerFactory)
    {
        if (!configuration.ReportedTelemetryEnabled)
        {
            return new TelemetryTagsSource(loggerFactory.CreateLogger<TelemetryTagsSource>());
        }

        var isCIEnvironment = CIEnvironmentDetector.IsCIEnvironment(Environment.GetEnvironmentVariable);
        return CreateTagsSource(configuration, loggerFactory,
            TelemetryMetadata.CreateMachineInformationProvider(loggerFactory, new TelemetryEnvironment()),
            TelemetryMetadata.CreateInternalMicrosoftDetector(loggerFactory, isCIEnvironment),
            isCIEnvironment);
    }

    internal static TelemetryTagsSource CreateTagsSource(DashboardTelemetryConfiguration configuration, ILoggerFactory loggerFactory,
        IMachineInformationProvider machineProvider, IInternalMicrosoftDetector internalMicrosoftDetector, bool isCIEnvironment)
    {
        var tagsSource = new TelemetryTagsSource(loggerFactory.CreateLogger<TelemetryTagsSource>());
        if (configuration.ReportedTelemetryEnabled)
        {
            var logger = loggerFactory.CreateLogger<DashboardTelemetryService>();
            tagsSource.StartCalculation(async () =>
            {
                try
                {
                    var machineTagsTask = TelemetryMetadata.GetMachineTagsAsync(machineProvider);
                    var internalMicrosoftTask = internalMicrosoftDetector.IsInternalMicrosoftMachineAsync();
                    await Task.WhenAll(machineTagsTask, internalMicrosoftTask).ConfigureAwait(false);
                    var machineTags = await machineTagsTask.ConfigureAwait(false);
                    var internalMicrosoftResult = await internalMicrosoftTask.ConfigureAwait(false);
                    var tags = TelemetryMetadata.CreateDefaultTags(machineTags, isCIEnvironment, internalMicrosoftResult, "aspire.dashboard");
                    tags.Add(new(TelemetryPropertyKeys.DashboardVersion, AssemblyVersionHelper.GetInformationalVersion(typeof(DashboardWebApplication).Assembly)));
                    tags.Add(new(TelemetryPropertyKeys.DashboardBuildId, AssemblyVersionHelper.GetFileVersion(typeof(DashboardWebApplication).Assembly)));
                    return tags;
                }
                catch (Exception ex)
                {
                    // Optional product metadata must not turn a dashboard event into a startup failure.
                    logger.LogError(ex, "Error occurred initializing dashboard telemetry metadata.");
                    return Array.Empty<KeyValuePair<string, object?>>();
                }
            });
        }

        return tagsSource;
    }
}
