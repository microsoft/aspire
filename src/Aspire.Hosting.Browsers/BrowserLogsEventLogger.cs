// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Turns low-level CDP events into resource log lines. Keeping this logic stateful but transport-free lets tests cover
// redirects, timing, and console formatting without needing a live browser.
internal sealed class BrowserEventLogger(string sessionId, ILogger resourceLogger)
{
    private readonly string _sessionId = sessionId;
    private readonly ILogger _resourceLogger = resourceLogger;
    // Network request information arrives as several independent CDP events. A live page can redirect, fail, or serve
    // from cache/service worker before the terminal event arrives, so keep just enough per-request state to emit one
    // resource-log line when the request is complete.
    private readonly Dictionary<string, BrowserNetworkRequestState> _networkRequests = new(StringComparer.Ordinal);

    public void HandleEvent(BrowserLogsCdpProtocolEvent protocolEvent)
    {
        if (BrowserLogsCdpEventMapper.TryMap(protocolEvent) is { } diagnosticEvent)
        {
            HandleEvent(diagnosticEvent);
        }
    }

    public void HandleEvent(BrowserDiagnosticEvent diagnosticEvent)
    {
        switch (diagnosticEvent)
        {
            case BrowserConsoleDiagnosticEvent consoleEvent:
                LogConsoleMessage(consoleEvent);
                break;
            case BrowserExceptionDiagnosticEvent exceptionEvent:
                LogUnhandledException(exceptionEvent);
                break;
            case BrowserLogEntryDiagnosticEvent logEntryEvent:
                LogEntryAdded(logEntryEvent);
                break;
            case BrowserNetworkRequestStartedDiagnosticEvent requestStartedEvent:
                TrackRequestStarted(requestStartedEvent);
                break;
            case BrowserNetworkResponseReceivedDiagnosticEvent responseReceivedEvent:
                TrackResponseReceived(responseReceivedEvent);
                break;
            case BrowserNetworkRequestCompletedDiagnosticEvent requestCompletedEvent:
                TrackRequestCompleted(requestCompletedEvent);
                break;
            case BrowserNetworkRequestFailedDiagnosticEvent requestFailedEvent:
                TrackRequestFailed(requestFailedEvent);
                break;
        }
    }

    private void LogConsoleMessage(BrowserConsoleDiagnosticEvent consoleEvent)
    {
        WriteLog(MapConsoleLevel(consoleEvent.Level), $"[console.{consoleEvent.Level}] {consoleEvent.Message}".TrimEnd());
    }

    private void LogUnhandledException(BrowserExceptionDiagnosticEvent exceptionEvent)
    {
        var location = exceptionEvent.Location is null ? string.Empty : GetLocationSuffix(exceptionEvent.Location);
        WriteLog(LogLevel.Error, $"[exception] {exceptionEvent.Message}{location}");
    }

    private void LogEntryAdded(BrowserLogEntryDiagnosticEvent logEntryEvent)
    {
        var location = logEntryEvent.Location is null ? string.Empty : GetLocationSuffix(logEntryEvent.Location);
        WriteLog(MapLogEntryLevel(logEntryEvent.Level), $"[log.{logEntryEvent.Level}] {logEntryEvent.Text}{location}".TrimEnd());
    }

    private void TrackRequestStarted(BrowserNetworkRequestStartedDiagnosticEvent requestStartedEvent)
    {
        if (requestStartedEvent.RequestId is not { Length: > 0 } requestId)
        {
            return;
        }

        var url = requestStartedEvent.Url;
        var method = requestStartedEvent.Method;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(method))
        {
            return;
        }

        if (requestStartedEvent.RedirectResponse is not null &&
            _networkRequests.Remove(requestId, out var redirectedRequest))
        {
            // CDP reuses the same request id when a redirect starts the next hop, so emit the completed hop before
            // overwriting it with the redirected request state.
            UpdateResponse(redirectedRequest, requestStartedEvent.RedirectResponse);
            LogCompletedRequest(redirectedRequest, requestStartedEvent.Timestamp, encodedDataLength: null, redirectUrl: url);
        }

        _networkRequests[requestId] = new BrowserNetworkRequestState
        {
            Method = method,
            ResourceType = NormalizeResourceType(requestStartedEvent.ResourceType),
            StartTimestamp = requestStartedEvent.Timestamp,
            Url = url
        };
    }

    private void TrackResponseReceived(BrowserNetworkResponseReceivedDiagnosticEvent responseReceivedEvent)
    {
        if (responseReceivedEvent.RequestId is not { Length: > 0 } requestId ||
            !_networkRequests.TryGetValue(requestId, out var request))
        {
            return;
        }

        if (responseReceivedEvent.Response is not null)
        {
            // Cache and service-worker flags are only available on the response event, while the duration and encoded
            // byte count arrive later on loadingFinished.
            UpdateResponse(request, responseReceivedEvent.Response);
        }

        if (responseReceivedEvent.ResourceType is { Length: > 0 } resourceType)
        {
            request.ResourceType = NormalizeResourceType(resourceType);
        }
    }

    private void TrackRequestCompleted(BrowserNetworkRequestCompletedDiagnosticEvent requestCompletedEvent)
    {
        if (requestCompletedEvent.RequestId is not { Length: > 0 } requestId ||
            !_networkRequests.Remove(requestId, out var request))
        {
            return;
        }

        LogCompletedRequest(request, requestCompletedEvent.Timestamp, requestCompletedEvent.EncodedDataLength, redirectUrl: null);
    }

    private void TrackRequestFailed(BrowserNetworkRequestFailedDiagnosticEvent requestFailedEvent)
    {
        if (requestFailedEvent.RequestId is not { Length: > 0 } requestId ||
            !_networkRequests.Remove(requestId, out var request))
        {
            return;
        }

        var details = new List<string>();

        if (FormatDuration(request.StartTimestamp, requestFailedEvent.Timestamp) is { Length: > 0 } duration)
        {
            details.Add(duration);
        }

        if (requestFailedEvent.Canceled == true)
        {
            details.Add("canceled");
        }

        if (!string.IsNullOrEmpty(requestFailedEvent.BlockedReason))
        {
            details.Add($"blocked={requestFailedEvent.BlockedReason}");
        }

        WriteLog(LogLevel.Warning, $"[network.{request.ResourceType}] {request.Method} {request.Url} failed: {requestFailedEvent.ErrorText ?? "Request failed"}{FormatDetails(details)}");
    }

    private void LogCompletedRequest(BrowserNetworkRequestState request, double? completedTimestamp, double? encodedDataLength, string? redirectUrl)
    {
        var details = new List<string>();

        if (FormatDuration(request.StartTimestamp, completedTimestamp) is { Length: > 0 } duration)
        {
            details.Add(duration);
        }

        if (encodedDataLength is > 0)
        {
            details.Add($"{Math.Round(encodedDataLength.Value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} B");
        }

        if (request.FromDiskCache == true)
        {
            details.Add("disk-cache");
        }

        if (request.FromServiceWorker == true)
        {
            details.Add("service-worker");
        }

        if (!string.IsNullOrEmpty(redirectUrl))
        {
            details.Add($"redirect to {redirectUrl}");
        }

        var statusText = request.StatusCode is int statusCode
            ? string.IsNullOrEmpty(request.StatusText)
                ? $" -> {statusCode}"
                : $" -> {statusCode} {request.StatusText}"
            : redirectUrl is null
                ? " completed"
                : " -> redirect";

        WriteLog(LogLevel.Information, $"[network.{request.ResourceType}] {request.Method} {request.Url}{statusText}{FormatDetails(details)}");
    }

    private static void UpdateResponse(BrowserNetworkRequestState request, BrowserNetworkResponseDetails response)
    {
        request.Url = response.Url ?? request.Url;
        request.StatusCode = response.Status;
        request.StatusText = response.StatusText;
        request.FromDiskCache = response.FromDiskCache;
        request.FromServiceWorker = response.FromServiceWorker;
    }

    private void WriteLog(LogLevel logLevel, string message)
    {
        var sessionMessage = $"[{_sessionId}] {message}";

        switch (logLevel)
        {
            case LogLevel.Error:
            case LogLevel.Critical:
                _resourceLogger.LogError("{Message}", sessionMessage);
                break;
            case LogLevel.Warning:
                _resourceLogger.LogWarning("{Message}", sessionMessage);
                break;
            case LogLevel.Debug:
            case LogLevel.Trace:
                _resourceLogger.LogDebug("{Message}", sessionMessage);
                break;
            default:
                _resourceLogger.LogInformation("{Message}", sessionMessage);
                break;
        }
    }

    private static string NormalizeResourceType(string? resourceType) =>
        string.IsNullOrEmpty(resourceType)
            ? "request"
            : resourceType.ToLowerInvariant();

    private static string? FormatDuration(double? startTimestamp, double? endTimestamp)
    {
        if (startTimestamp is null || endTimestamp is null || endTimestamp < startTimestamp)
        {
            return null;
        }

        var durationMs = Math.Round((endTimestamp.Value - startTimestamp.Value) * 1000, MidpointRounding.AwayFromZero);
        return $"{durationMs.ToString(CultureInfo.InvariantCulture)} ms";
    }

    private static string FormatDetails(IReadOnlyList<string> details) =>
        details.Count > 0
            ? $" ({string.Join(", ", details)})"
            : string.Empty;

    private static LogLevel MapConsoleLevel(string level) => level switch
    {
        "error" or "assert" => LogLevel.Error,
        "warning" or "warn" => LogLevel.Warning,
        "debug" => LogLevel.Debug,
        _ => LogLevel.Information
    };

    private static LogLevel MapLogEntryLevel(string level) => level switch
    {
        "error" => LogLevel.Error,
        "warning" => LogLevel.Warning,
        "verbose" => LogLevel.Debug,
        _ => LogLevel.Information
    };

    private static string GetLocationSuffix(BrowserDiagnosticSourceLocation details)
    {
        var url = details.Url;
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        var lineNumber = details.LineNumber + 1;
        var columnNumber = details.ColumnNumber + 1;

        if (lineNumber > 0 && columnNumber > 0)
        {
            return $" ({url}:{lineNumber}:{columnNumber})";
        }

        return $" ({url})";
    }

    private sealed class BrowserNetworkRequestState
    {
        public bool? FromDiskCache { get; set; }

        public bool? FromServiceWorker { get; set; }

        public required string Method { get; set; }

        public required string ResourceType { get; set; }

        public double? StartTimestamp { get; set; }

        public int? StatusCode { get; set; }

        public string? StatusText { get; set; }

        public required string Url { get; set; }
    }
}
