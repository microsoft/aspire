// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Aspire.Shared.Telemetry;

/// <summary>
/// Provides reported and diagnostic activities and structured events for Aspire product telemetry.
/// </summary>
public abstract class AspireTelemetryBase : IDisposable
{
    private readonly ActivitySource _reportedActivitySource;
    private readonly ActivitySource _diagnosticsActivitySource;
    private readonly ILogger _logger;
    private readonly ILogger _eventLogger;
    private readonly string _errorEventName;

    /// <summary>
    /// Initializes telemetry with product-specific activity source and error event names.
    /// </summary>
    /// <param name="logger">The logger for local errors.</param>
    /// <param name="eventLogger">The logger for structured product events.</param>
    /// <param name="reportedSourceName">The externally reported activity source name.</param>
    /// <param name="diagnosticsSourceName">The local diagnostic activity source name.</param>
    /// <param name="errorEventName">The name of reported error events.</param>
    protected AspireTelemetryBase(ILogger logger, ILogger eventLogger, string reportedSourceName, string diagnosticsSourceName, string errorEventName)
    {
        _logger = logger;
        _eventLogger = eventLogger;
        _reportedActivitySource = new ActivitySource(reportedSourceName);
        _diagnosticsActivitySource = new ActivitySource(diagnosticsSourceName);
        _errorEventName = errorEventName;
    }

    /// <summary>
    /// Starts an externally reported activity.
    /// </summary>
    /// <param name="name">The activity name.</param>
    /// <param name="kind">The activity kind.</param>
    /// <returns>The activity, or <see langword="null"/> when no listener samples it.</returns>
    public Activity? StartReportedActivity([CallerMemberName] string name = "", ActivityKind kind = ActivityKind.Internal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _reportedActivitySource.StartActivity(name, kind);
    }

    /// <summary>
    /// Starts an externally reported activity with an explicit parent.
    /// </summary>
    /// <param name="name">The activity name.</param>
    /// <param name="kind">The activity kind.</param>
    /// <param name="parentContext">The parent context.</param>
    /// <returns>The sampled activity, or <see langword="null"/>.</returns>
    public Activity? StartReportedActivity(string name, ActivityKind kind, ActivityContext parentContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _reportedActivitySource.StartActivity(name, kind, parentContext);
    }

    /// <summary>
    /// Starts an externally reported activity with explicit correlation links.
    /// </summary>
    /// <param name="name">The activity name.</param>
    /// <param name="kind">The activity kind.</param>
    /// <param name="parentContext">The parent context.</param>
    /// <param name="links">Links to related activities.</param>
    /// <returns>The sampled activity, or <see langword="null"/>.</returns>
    protected Activity? StartReportedActivity(string name, ActivityKind kind, ActivityContext parentContext, IEnumerable<ActivityLink> links)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _reportedActivitySource.StartActivity(name, kind, parentContext, links: links);
    }

    /// <summary>
    /// Starts a local diagnostic activity.
    /// </summary>
    /// <param name="name">The activity name.</param>
    /// <param name="kind">The activity kind.</param>
    /// <returns>The sampled activity, or <see langword="null"/>.</returns>
    public Activity? StartDiagnosticActivity([CallerMemberName] string name = "", ActivityKind kind = ActivityKind.Internal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _diagnosticsActivitySource.StartActivity(name, kind);
    }

    /// <summary>
    /// Starts a local diagnostic activity with an explicit parent.
    /// </summary>
    /// <param name="name">The activity name.</param>
    /// <param name="kind">The activity kind.</param>
    /// <param name="parentContext">The parent context.</param>
    /// <returns>The sampled activity, or <see langword="null"/>.</returns>
    public Activity? StartDiagnosticActivity(string name, ActivityKind kind, ActivityContext parentContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _diagnosticsActivitySource.StartActivity(name, kind, parentContext);
    }

    /// <summary>
    /// Records a structured event immediately and adds it to the nearest active reported activity, if present.
    /// </summary>
    /// <remarks>
    /// Applies the product's property privacy policy to both the log and activity event, including default metadata.
    /// </remarks>
    /// <param name="eventName">The event name.</param>
    /// <param name="properties">The product-specific event properties.</param>
    protected void RecordEventCore(string eventName, IEnumerable<KeyValuePair<string, object?>>? properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (!IsReportedTelemetryEnabled)
        {
            return;
        }

        var tags = CreateProperties(properties);
        var activity = FindReportedActivity(Activity.Current);
        List<KeyValuePair<string, object?>> attributes = [.. tags, new("{OriginalFormat}", eventName)];
        _eventLogger.Log(LogLevel.Information, new EventId(0, eventName), attributes,
            exception: null, formatter: (_, _) => eventName);

        // Keep custom events in Application Insights' traces table rather than using
        // Activity.AddException, which would route errors to the exceptions table.
        activity?.AddEvent(new ActivityEvent(eventName, tags: tags));
    }

    /// <summary>
    /// Adds default metadata and properties to a reported activity after applying the product's privacy policy.
    /// </summary>
    /// <param name="activity">The reported activity.</param>
    /// <param name="properties">The product-specific activity properties.</param>
    protected void AddReportedActivityProperties(Activity activity, IEnumerable<KeyValuePair<string, object?>>? properties)
    {
        SetActivityProperties(activity, GetDefaultTags().Concat(properties ?? []));
    }

    /// <summary>
    /// Sets an activity property after applying the product's privacy policy.
    /// </summary>
    /// <remarks>
    /// Does not add default metadata. A disallowed property leaves any existing tag unchanged.
    /// </remarks>
    /// <param name="activity">The activity, or <see langword="null"/> when not recorded.</param>
    /// <param name="key">The property name.</param>
    /// <param name="value">The value, including any product-specific privacy classification.</param>
    /// <exception cref="ArgumentException">The property name is null, empty, or whitespace.</exception>
    public void SetActivityProperty(Activity? activity, string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (activity is not null && TrySanitizeProperty(key, value, out var sanitizedValue))
        {
            activity.SetTag(key, sanitizedValue);
        }
    }

    /// <summary>
    /// Sets activity properties after applying the product's privacy policy.
    /// </summary>
    /// <remarks>
    /// Does not add default metadata or enumerate properties when the activity is <see langword="null"/>.
    /// Disallowed properties leave existing tags unchanged.
    /// </remarks>
    /// <param name="activity">The activity, or <see langword="null"/> when not recorded.</param>
    /// <param name="properties">The properties, including any product-specific privacy classifications.</param>
    /// <exception cref="ArgumentNullException">The properties collection is null.</exception>
    /// <exception cref="ArgumentException">A property name is null, empty, or whitespace.</exception>
    public void SetActivityProperties(Activity? activity, IEnumerable<KeyValuePair<string, object?>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (activity is null)
        {
            return;
        }

        foreach (var (key, value) in properties)
        {
            SetActivityProperty(activity, key, value);
        }
    }

    /// <summary>
    /// Logs an error locally and records a structured event, also using the nearest active reported activity.
    /// </summary>
    /// <param name="message">The local log message.</param>
    /// <param name="exception">The exception to record.</param>
    public virtual void RecordError(string message, Exception exception)
    {
        RecordErrorCore(message, exception, writeToLogging: true, createActivity: false);
    }

    /// <summary>
    /// Records an error, optionally creating a bounded reported activity when none exists.
    /// </summary>
    /// <param name="message">The local log message.</param>
    /// <param name="exception">The exception to record.</param>
    /// <param name="writeToLogging">Whether to also log the error locally.</param>
    /// <param name="createActivity">Whether to create an activity for standalone errors.</param>
    protected void RecordErrorCore(string message, Exception exception, bool writeToLogging, bool createActivity)
    {
        if (writeToLogging)
        {
            _logger.LogError(exception, message);
        }

        if (!IsReportedTelemetryEnabled)
        {
            return;
        }

        using var errorActivity = createActivity && FindReportedActivity(Activity.Current) is null ? StartReportedActivity(_errorEventName) : null;
        if (errorActivity is not null)
        {
            AddReportedActivityProperties(errorActivity, properties: null);
            errorActivity.SetStatus(ActivityStatusCode.Error);
        }

        RecordEventCore(_errorEventName, CreateErrorTags(exception));
    }

    /// <summary>
    /// Creates product-specific exception fields that will be filtered by the product's privacy policy.
    /// </summary>
    /// <param name="exception">The exception to describe.</param>
    /// <returns>The error event tags.</returns>
    protected virtual ActivityTagsCollection CreateErrorTags(Exception exception) => new()
    {
        ["exception.type"] = exception.GetType().FullName,
        ["exception.message"] = exception.Message,
        ["exception.stacktrace"] = exception.StackTrace
    };

    /// <summary>
    /// Gets product-specific metadata to include on structured events.
    /// </summary>
    /// <returns>The default tags.</returns>
    protected abstract IReadOnlyList<KeyValuePair<string, object?>> GetDefaultTags();

    /// <summary>
    /// Determines whether a property may be reported and sanitizes its value.
    /// </summary>
    /// <param name="key">The property name.</param>
    /// <param name="value">The property value, including any product-specific privacy classification.</param>
    /// <param name="sanitizedValue">The value to report when the property is allowed.</param>
    /// <returns><see langword="true"/> when the property may be reported; otherwise, <see langword="false"/>.</returns>
    protected abstract bool TrySanitizeProperty(string key, object? value, out object? sanitizedValue);

    /// <summary>
    /// Gets whether structured events and errors may be recorded.
    /// </summary>
    protected virtual bool IsReportedTelemetryEnabled => true;

    private ActivityTagsCollection CreateProperties(IEnumerable<KeyValuePair<string, object?>>? properties)
    {
        var tags = new ActivityTagsCollection();
        foreach (var (key, value) in GetDefaultTags().Concat(properties ?? []))
        {
            if (TrySanitizeProperty(key, value, out var sanitizedValue))
            {
                tags[key] = sanitizedValue;
            }
        }

        return tags;
    }

    private Activity? FindReportedActivity(Activity? activity)
    {
        while (activity is not null)
        {
            if (activity.Source == _reportedActivitySource && !activity.IsStopped)
            {
                return activity;
            }

            activity = activity.Parent;
        }

        return null;
    }

    /// <summary>
    /// Releases the product's activity sources.
    /// </summary>
    public void Dispose()
    {
        _reportedActivitySource.Dispose();
        _diagnosticsActivitySource.Dispose();
        GC.SuppressFinalize(this);
    }
}
