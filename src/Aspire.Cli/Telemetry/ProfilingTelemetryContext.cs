// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Carries profiling telemetry correlation across Aspire processes.
/// </summary>
internal sealed class ProfilingTelemetryContext
{
    /// <summary>
    /// Environment variable that enables profiling.
    /// </summary>
    internal const string EnabledEnvironmentVariable = KnownConfigNames.ProfilingEnabled;

    /// <summary>
    /// Environment variable that carries the stable profiling session identifier.
    /// </summary>
    internal const string SessionIdEnvironmentVariable = KnownConfigNames.ProfilingSessionId;

    /// <summary>
    /// Environment variable that carries the W3C traceparent value.
    /// </summary>
    internal const string TraceParentEnvironmentVariable = KnownConfigNames.ProfilingTraceParent;

    /// <summary>
    /// Environment variable that carries the W3C tracestate value.
    /// </summary>
    internal const string TraceStateEnvironmentVariable = KnownConfigNames.ProfilingTraceState;

    private ProfilingTelemetryContext(string sessionId, string? traceParent, string? traceState)
    {
        SessionId = sessionId;
        TraceParent = traceParent;
        TraceState = traceState;
    }

    /// <summary>
    /// Gets the stable identifier for this profiling session.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the W3C traceparent value to use as the remote parent.
    /// </summary>
    public string? TraceParent { get; }

    /// <summary>
    /// Gets the W3C tracestate value to use as the remote parent.
    /// </summary>
    public string? TraceState { get; }

    /// <summary>
    /// Creates a new profiling telemetry context for a parent process.
    /// </summary>
    public static ProfilingTelemetryContext Create(Activity? parentActivity)
    {
        return new ProfilingTelemetryContext(
            sessionId: Guid.NewGuid().ToString("N"),
            traceParent: parentActivity?.Id,
            traceState: parentActivity?.TraceStateString);
    }

    /// <summary>
    /// Reads the profiling telemetry context from configuration.
    /// </summary>
    public static ProfilingTelemetryContext? FromConfiguration(IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
        {
            return null;
        }

        var sessionId = GetConfigurationValue(configuration, SessionIdEnvironmentVariable, KnownConfigNames.Legacy.StartupOperationId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var traceParent = GetConfigurationValue(configuration, TraceParentEnvironmentVariable, KnownConfigNames.Legacy.StartupTraceParent);
        var traceState = GetConfigurationValue(configuration, TraceStateEnvironmentVariable, KnownConfigNames.Legacy.StartupTraceState);
        return new ProfilingTelemetryContext(sessionId, traceParent, traceState);
    }

    /// <summary>
    /// Returns <see langword="true"/> when profiling has been explicitly enabled.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration)
    {
        return IsTruthy(configuration[EnabledEnvironmentVariable]) ||
            IsTruthy(configuration[KnownConfigNames.Legacy.StartupProfilingEnabled]);
    }

    /// <summary>
    /// Adds this context to a child process environment dictionary.
    /// </summary>
    public void AddToEnvironment(IDictionary<string, string> environment)
    {
        environment[EnabledEnvironmentVariable] = "true";
        environment[SessionIdEnvironmentVariable] = SessionId;

        // DCP currently consumes the legacy startup-named variables. Keep writing them until DCP moves
        // to the generalized profiling names.
        environment[KnownConfigNames.Legacy.StartupProfilingEnabled] = "true";
        environment[KnownConfigNames.Legacy.StartupOperationId] = SessionId;

        if (!string.IsNullOrWhiteSpace(TraceParent))
        {
            environment[TraceParentEnvironmentVariable] = TraceParent;
            environment[KnownConfigNames.Legacy.StartupTraceParent] = TraceParent;
        }

        if (!string.IsNullOrWhiteSpace(TraceState))
        {
            environment[TraceStateEnvironmentVariable] = TraceState;
            environment[KnownConfigNames.Legacy.StartupTraceState] = TraceState;
        }
    }

    /// <summary>
    /// Attempts to parse the remote activity context from the W3C trace context values.
    /// </summary>
    public bool TryGetActivityContext(out ActivityContext activityContext)
    {
        if (!string.IsNullOrWhiteSpace(TraceParent) &&
            ActivityContext.TryParse(TraceParent, TraceState, out activityContext))
        {
            return true;
        }

        activityContext = default;
        return false;
    }

    /// <summary>
    /// Adds profiling correlation tags to an activity.
    /// </summary>
    public void AddTags(Activity? activity)
    {
        activity?.SetTag(ProfilingTelemetry.Tags.ProfilingSessionId, SessionId);
        activity?.SetTag(ProfilingTelemetry.Tags.LegacyStartupOperationId, SessionId);
    }

    private static string? GetConfigurationValue(IConfiguration configuration, string name, string legacyName)
    {
        return configuration[name] is { Length: > 0 } value ? value : configuration[legacyName];
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }
}
