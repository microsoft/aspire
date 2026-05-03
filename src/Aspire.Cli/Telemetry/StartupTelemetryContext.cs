// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Carries startup telemetry correlation across Aspire processes.
/// </summary>
internal sealed class StartupTelemetryContext
{
    /// <summary>
    /// Environment variable that carries the stable startup operation identifier.
    /// </summary>
    internal const string OperationIdEnvironmentVariable = "ASPIRE_STARTUP_OPERATION_ID";

    /// <summary>
    /// Environment variable that carries the W3C traceparent value.
    /// </summary>
    internal const string TraceParentEnvironmentVariable = "ASPIRE_STARTUP_TRACEPARENT";

    /// <summary>
    /// Environment variable that carries the W3C tracestate value.
    /// </summary>
    internal const string TraceStateEnvironmentVariable = "ASPIRE_STARTUP_TRACESTATE";

    private StartupTelemetryContext(string operationId, string? traceParent, string? traceState)
    {
        OperationId = operationId;
        TraceParent = traceParent;
        TraceState = traceState;
    }

    /// <summary>
    /// Gets the stable identifier for this startup operation.
    /// </summary>
    public string OperationId { get; }

    /// <summary>
    /// Gets the W3C traceparent value to use as the remote parent.
    /// </summary>
    public string? TraceParent { get; }

    /// <summary>
    /// Gets the W3C tracestate value to use as the remote parent.
    /// </summary>
    public string? TraceState { get; }

    /// <summary>
    /// Creates a new startup telemetry context for a parent process.
    /// </summary>
    public static StartupTelemetryContext Create(Activity? parentActivity)
    {
        return new StartupTelemetryContext(
            operationId: Guid.NewGuid().ToString("N"),
            traceParent: parentActivity?.Id,
            traceState: parentActivity?.TraceStateString);
    }

    /// <summary>
    /// Reads the startup telemetry context from environment variables.
    /// </summary>
    public static StartupTelemetryContext? FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        var operationId = getEnvironmentVariable(OperationIdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return null;
        }

        var traceParent = getEnvironmentVariable(TraceParentEnvironmentVariable);
        var traceState = getEnvironmentVariable(TraceStateEnvironmentVariable);
        return new StartupTelemetryContext(operationId, traceParent, traceState);
    }

    /// <summary>
    /// Adds this context to a child process environment dictionary.
    /// </summary>
    public void AddToEnvironment(IDictionary<string, string> environment)
    {
        environment[OperationIdEnvironmentVariable] = OperationId;

        if (!string.IsNullOrWhiteSpace(TraceParent))
        {
            environment[TraceParentEnvironmentVariable] = TraceParent;
        }

        if (!string.IsNullOrWhiteSpace(TraceState))
        {
            environment[TraceStateEnvironmentVariable] = TraceState;
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
    /// Adds startup correlation tags to an activity.
    /// </summary>
    public void AddTags(Activity? activity)
    {
        activity?.SetTag(TelemetryConstants.Tags.StartupOperationId, OperationId);
    }
}
