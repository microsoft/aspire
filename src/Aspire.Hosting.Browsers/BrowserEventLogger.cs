// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Turns low-level CDP events into resource log lines. Keeping this logic stateful but transport-free lets tests cover
// redirects, timing, and console formatting without needing a live browser.
internal sealed class BrowserEventLogger(string sessionId, ILogger resourceLogger)
{
    private static readonly JsonWriterOptions s_structuredValueWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _sessionId = sessionId;
    private readonly ILogger _resourceLogger = resourceLogger;
    // Network request information arrives as several independent CDP events. A live page can redirect, fail, or serve
    // from cache/service worker before the terminal event arrives, so keep just enough per-request state to emit one
    // resource-log line when the request is complete.
    private readonly Dictionary<string, BrowserNetworkRequestState> _networkRequests = new(StringComparer.Ordinal);

    public void HandleEvent(BrowserCdpProtocolEvent protocolEvent)
    {
        switch (protocolEvent)
        {
            case BrowserConsoleApiCalledEvent consoleApiCalledEvent:
                LogConsoleMessage(consoleApiCalledEvent.Parameters);
                break;
            case BrowserExceptionThrownEvent exceptionThrownEvent:
                LogUnhandledException(exceptionThrownEvent.Parameters);
                break;
            case BrowserLogEntryAddedEvent logEntryAddedEvent:
                LogEntryAdded(logEntryAddedEvent.Parameters);
                break;
            case BrowserRequestWillBeSentEvent requestWillBeSentEvent:
                TrackRequestStarted(requestWillBeSentEvent.Parameters);
                break;
            case BrowserResponseReceivedEvent responseReceivedEvent:
                TrackResponseReceived(responseReceivedEvent.Parameters);
                break;
            case BrowserLoadingFinishedEvent loadingFinishedEvent:
                TrackRequestCompleted(loadingFinishedEvent.Parameters);
                break;
            case BrowserLoadingFailedEvent loadingFailedEvent:
                TrackRequestFailed(loadingFailedEvent.Parameters);
                break;
        }
    }

    private void LogConsoleMessage(BrowserRuntimeConsoleApiCalledParameters parameters)
    {
        var level = parameters.Type ?? "log";
        var message = parameters.Args is { Length: > 0 }
            ? string.Join(" ", parameters.Args.Select(FormatRemoteObject).Where(static value => !string.IsNullOrEmpty(value)))
            : string.Empty;

        WriteLog(MapConsoleLevel(level), $"[console.{level}] {message}".TrimEnd());
    }

    private void LogUnhandledException(BrowserExceptionThrownParameters parameters)
    {
        var exceptionDetails = parameters.ExceptionDetails;
        if (exceptionDetails is null)
        {
            return;
        }

        var message = exceptionDetails.Exception?.Description
            ?? exceptionDetails.Text
            ?? "Unhandled browser exception";

        var location = GetLocationSuffix(exceptionDetails);
        WriteLog(LogLevel.Error, $"[exception] {message}{location}");
    }

    private void LogEntryAdded(BrowserLogEntryAddedParameters parameters)
    {
        var entry = parameters.Entry;
        if (entry is null)
        {
            return;
        }

        var level = entry.Level ?? "info";
        var text = entry.Text ?? string.Empty;
        var location = GetLocationSuffix(entry);

        WriteLog(MapLogEntryLevel(level), $"[log.{level}] {text}{location}".TrimEnd());
    }

    private void TrackRequestStarted(BrowserRequestWillBeSentParameters parameters)
    {
        // Network events arrive as a loosely-coupled CDP stream rather than one complete HTTP record:
        // https://chromedevtools.github.io/devtools-protocol/tot/Network/
        //
        // Typical successful request:
        //   Network.requestWillBeSent  { requestId: "1", request: { method: "GET", url: "https://..." }, timestamp: 1.25 }
        //   Network.responseReceived   { requestId: "1", response: { status: 200, fromDiskCache: false } }
        //   Network.loadingFinished    { requestId: "1", encodedDataLength: 1234, timestamp: 1.40 }
        //
        // Redirect edge case:
        //   Network.requestWillBeSent  { requestId: "1", redirectResponse: { status: 302 }, request: { url: "https://next" } }
        //
        // CDP reuses the same requestId for the redirected hop, so emit the previous hop before replacing its state.
        if (parameters.RequestId is not { Length: > 0 } requestId || parameters.Request is not { } request)
        {
            return;
        }

        var url = request.Url;
        var method = request.Method;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(method))
        {
            return;
        }

        if (parameters.RedirectResponse is not null &&
            _networkRequests.Remove(requestId, out var redirectedRequest))
        {
            // CDP reuses the same request id when a redirect starts the next hop, so emit the completed hop before
            // overwriting it with the redirected request state.
            UpdateResponse(redirectedRequest, parameters.RedirectResponse);
            LogCompletedRequest(redirectedRequest, parameters.Timestamp, encodedDataLength: null, redirectUrl: url);
        }

        _networkRequests[requestId] = new BrowserNetworkRequestState
        {
            Method = method,
            ResourceType = NormalizeResourceType(parameters.Type),
            StartTimestamp = parameters.Timestamp,
            Url = url
        };
    }

    private void TrackResponseReceived(BrowserResponseReceivedParameters parameters)
    {
        if (parameters.RequestId is not { Length: > 0 } requestId ||
            !_networkRequests.TryGetValue(requestId, out var request))
        {
            return;
        }

        if (parameters.Response is not null)
        {
            // Cache and service-worker flags are only available on the response event, while the duration and encoded
            // byte count arrive later on loadingFinished.
            UpdateResponse(request, parameters.Response);
        }

        if (parameters.Type is { Length: > 0 } resourceType)
        {
            request.ResourceType = NormalizeResourceType(resourceType);
        }
    }

    private void TrackRequestCompleted(BrowserLoadingFinishedParameters parameters)
    {
        if (parameters.RequestId is not { Length: > 0 } requestId ||
            !_networkRequests.Remove(requestId, out var request))
        {
            return;
        }

        LogCompletedRequest(request, parameters.Timestamp, parameters.EncodedDataLength, redirectUrl: null);
    }

    private void TrackRequestFailed(BrowserLoadingFailedParameters parameters)
    {
        if (parameters.RequestId is not { Length: > 0 } requestId ||
            !_networkRequests.Remove(requestId, out var request))
        {
            return;
        }

        var details = new List<string>();

        if (FormatDuration(request.StartTimestamp, parameters.Timestamp) is { Length: > 0 } duration)
        {
            details.Add(duration);
        }

        if (parameters.Canceled == true)
        {
            details.Add("canceled");
        }

        if (!string.IsNullOrEmpty(parameters.BlockedReason))
        {
            details.Add($"blocked={parameters.BlockedReason}");
        }

        WriteLog(LogLevel.Warning, $"[network.{request.ResourceType}] {request.Method} {request.Url} failed: {parameters.ErrorText ?? "Request failed"}{FormatDetails(details)}");
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

    private static void UpdateResponse(BrowserNetworkRequestState request, BrowserResponse response)
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
        // CDP Network timestamps are MonotonicTime values in seconds. Keep the subtraction in that domain and only
        // convert the delta to milliseconds for the log line; DateTime/Stopwatch conversions would mix clocks and can
        // produce nonsense when the browser and AppHost have different time origins.
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

    private static string FormatRemoteObject(BrowserCdpProtocolRemoteObject remoteObject)
    {
        // Console arguments can arrive either as pre-rendered descriptions or as structured values that need stable
        // formatting for logs and tests.
        if (remoteObject.Value is BrowserCdpProtocolValue value)
        {
            return value switch
            {
                BrowserCdpProtocolStringValue stringValue => stringValue.Value,
                BrowserCdpProtocolNullValue => "null",
                BrowserCdpProtocolBooleanValue booleanValue => booleanValue.Value ? bool.TrueString : bool.FalseString,
                BrowserCdpProtocolNumberValue numberValue => numberValue.RawValue,
                _ => FormatStructuredValue(value)
            };
        }

        if (!string.IsNullOrEmpty(remoteObject.UnserializableValue))
        {
            return remoteObject.UnserializableValue;
        }

        return remoteObject.Description ?? string.Empty;
    }

    private static string FormatStructuredValue(BrowserCdpProtocolValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, s_structuredValueWriterOptions);
        WriteStructuredValue(writer, value);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStructuredValue(Utf8JsonWriter writer, BrowserCdpProtocolValue value)
    {
        switch (value)
        {
            case BrowserCdpProtocolArrayValue arrayValue:
                writer.WriteStartArray();
                foreach (var item in arrayValue.Items)
                {
                    WriteStructuredValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case BrowserCdpProtocolBooleanValue booleanValue:
                writer.WriteBooleanValue(booleanValue.Value);
                break;
            case BrowserCdpProtocolNullValue:
                writer.WriteNullValue();
                break;
            case BrowserCdpProtocolNumberValue numberValue:
                writer.WriteRawValue(numberValue.RawValue, skipInputValidation: false);
                break;
            case BrowserCdpProtocolObjectValue objectValue:
                writer.WriteStartObject();
                foreach (var (propertyName, propertyValue) in objectValue.Properties)
                {
                    writer.WritePropertyName(propertyName);
                    WriteStructuredValue(writer, propertyValue);
                }

                writer.WriteEndObject();
                break;
            case BrowserCdpProtocolStringValue stringValue:
                writer.WriteStringValue(stringValue.Value);
                break;
        }
    }

    private static string GetLocationSuffix(BrowserSourceLocation details)
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
