// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Telemetry;

/// <summary>
/// For recording errors to telemetry. Can be injected into components that handle errors but we still want to record to telemetry.
/// </summary>
public interface ITelemetryErrorRecorder
{
    /// <summary>
    /// Records an error, reporting distinct leaf exceptions for aggregates by type, message, and stack trace.
    /// </summary>
    void RecordError(string message, Exception exception, bool writeToLogging = false);
}

public sealed class TelemetryErrorRecorder : ITelemetryErrorRecorder
{
    private readonly DashboardTelemetryService _telemetryService;
    private readonly ILogger<TelemetryErrorRecorder> _logger;

    public TelemetryErrorRecorder(DashboardTelemetryService telemetryService, ILogger<TelemetryErrorRecorder> logger)
    {
        _telemetryService = telemetryService;
        _logger = logger;
    }

    public void RecordError(string message, Exception exception, bool writeToLogging = false)
    {
        if (writeToLogging)
        {
            _logger.LogError(exception, message);
        }

        // Blazor passes newly created disposal aggregates to its error handler without throwing them,
        // so only the child exceptions have the original stacks.
        // https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Components/Components/src/RenderTree/Renderer.cs#L1312
        if (exception is AggregateException aggregateException)
        {
            var innerExceptions = aggregateException.Flatten().InnerExceptions;
            if (innerExceptions.Count > 0)
            {
                foreach (var innerException in innerExceptions.DistinctBy(e => (e.GetType(), e.Message, e.StackTrace)))
                {
                    RecordException(innerException);
                }

                return;
            }
        }

        RecordException(exception);
    }

    private void RecordException(Exception exception)
    {
        _telemetryService.PostFault(
            TelemetryEventKeys.Error,
            $"{exception.GetType().FullName}: {exception.Message}",
            FaultSeverity.Critical,
            new Dictionary<string, AspireTelemetryProperty>
            {
                [TelemetryPropertyKeys.ExceptionType] = new AspireTelemetryProperty(exception.GetType().FullName!),
                [TelemetryPropertyKeys.ExceptionMessage] = new AspireTelemetryProperty(exception.Message),
                [TelemetryPropertyKeys.ExceptionStackTrace] = new AspireTelemetryProperty(exception.StackTrace ?? string.Empty),
                [TelemetryPropertyKeys.ExceptionRuntimeVersion] = new AspireTelemetryProperty(VersionHelpers.RuntimeVersion?.ToString() ?? string.Empty),
            }
        );
    }
}
