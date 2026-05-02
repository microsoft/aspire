// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting;

// Converts WebDriver BiDi log/network events into the protocol-neutral BrowserLogs diagnostic event model.
internal static class BrowserLogsBidiEventMapper
{
    public static BrowserDiagnosticEvent? TryMap(BrowserLogsBidiProtocolEvent protocolEvent)
    {
        return protocolEvent switch
        {
            BrowserLogsBidiLogEntryAddedEvent logEntryAddedEvent => CreateLogDiagnosticEvent(logEntryAddedEvent.Parameters),
            BrowserLogsBidiNetworkBeforeRequestSentEvent beforeRequestSentEvent => CreateRequestStartedEvent(beforeRequestSentEvent.Parameters),
            BrowserLogsBidiNetworkResponseStartedEvent responseStartedEvent => CreateResponseReceivedEvent(responseStartedEvent.Parameters),
            BrowserLogsBidiNetworkFetchErrorEvent fetchErrorEvent => CreateRequestFailedEvent(fetchErrorEvent.Parameters),
            _ => null
        };
    }

    public static IReadOnlyList<BrowserDiagnosticEvent> MapResponseCompleted(BrowserLogsBidiNetworkResponseCompletedEvent responseCompletedEvent)
    {
        List<BrowserDiagnosticEvent> events = [];
        if (CreateResponseReceivedEvent(responseCompletedEvent.Parameters) is { } responseEvent)
        {
            events.Add(responseEvent);
        }

        if (responseCompletedEvent.Parameters.Request?.Request is { Length: > 0 } requestId)
        {
            events.Add(new BrowserNetworkRequestCompletedDiagnosticEvent(
                requestId,
                ConvertBidiTimestamp(responseCompletedEvent.Parameters.Timestamp),
                EncodedDataLength: null));
        }

        return events;
    }

    private static BrowserDiagnosticEvent CreateLogDiagnosticEvent(BrowserLogsBidiLogEntryAddedParameters parameters)
    {
        var text = parameters.Text;
        if (string.IsNullOrEmpty(text) && parameters.Args is { Length: > 0 })
        {
            text = string.Join(" ", parameters.Args.Select(FormatRemoteValue).Where(static value => !string.IsNullOrEmpty(value)));
        }

        text ??= string.Empty;

        if (parameters.Method is { Length: > 0 } method)
        {
            return new BrowserConsoleDiagnosticEvent(method, text);
        }

        var location = CreateLocation(parameters.StackTrace);
        if (string.Equals(parameters.Type, "javascript", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parameters.Level, "error", StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserExceptionDiagnosticEvent(
                string.IsNullOrEmpty(text) ? "Unhandled browser exception" : text,
                location);
        }

        return new BrowserLogEntryDiagnosticEvent(parameters.Level ?? "info", text, location);
    }

    private static BrowserNetworkRequestStartedDiagnosticEvent? CreateRequestStartedEvent(BrowserLogsBidiNetworkEventParameters parameters)
    {
        var request = parameters.Request;
        if (request?.Request is not { Length: > 0 } requestId ||
            request.Method is not { Length: > 0 } method ||
            request.Url is not { Length: > 0 } url)
        {
            return null;
        }

        return new BrowserNetworkRequestStartedDiagnosticEvent(
            requestId,
            method,
            url,
            ResourceType: "request",
            ConvertBidiTimestamp(parameters.Timestamp),
            RedirectResponse: null);
    }

    private static BrowserNetworkResponseReceivedDiagnosticEvent? CreateResponseReceivedEvent(BrowserLogsBidiNetworkEventParameters parameters)
    {
        var requestId = parameters.Request?.Request;
        if (string.IsNullOrEmpty(requestId))
        {
            return null;
        }

        return new BrowserNetworkResponseReceivedDiagnosticEvent(
            requestId,
            ResourceType: "request",
            CreateResponseDetails(parameters.Response));
    }

    private static BrowserNetworkRequestFailedDiagnosticEvent? CreateRequestFailedEvent(BrowserLogsBidiNetworkEventParameters parameters)
    {
        if (parameters.Request?.Request is not { Length: > 0 } requestId)
        {
            return null;
        }

        return new BrowserNetworkRequestFailedDiagnosticEvent(
            requestId,
            ConvertBidiTimestamp(parameters.Timestamp),
            parameters.ErrorText,
            BlockedReason: null,
            Canceled: null);
    }

    private static BrowserNetworkResponseDetails? CreateResponseDetails(BrowserLogsBidiNetworkResponse? response)
    {
        return response is null
            ? null
            : new BrowserNetworkResponseDetails(
                response.Url,
                response.Status,
                response.StatusText,
                response.FromCache,
                FromServiceWorker: null);
    }

    private static BrowserDiagnosticSourceLocation? CreateLocation(BrowserLogsBidiStackTrace? stackTrace)
    {
        var callFrame = stackTrace?.CallFrames?.FirstOrDefault(static frame => !string.IsNullOrEmpty(frame.Url));
        return callFrame is null
            ? null
            : new BrowserDiagnosticSourceLocation(callFrame.Url, callFrame.LineNumber, callFrame.ColumnNumber);
    }

    private static double? ConvertBidiTimestamp(double? timestamp)
    {
        return timestamp is null ? null : timestamp / 1000;
    }

    private static string FormatRemoteValue(BrowserLogsBidiRemoteValue value)
    {
        return value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.Value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => value.Type ?? string.Empty,
            _ => value.Value.GetRawText()
        };
    }
}
