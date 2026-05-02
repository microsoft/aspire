// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Aspire.Hosting;

// Converts CDP Runtime/Log/Network events into the protocol-neutral BrowserLogs diagnostic event model.
internal static class BrowserLogsCdpEventMapper
{
    private static readonly JsonWriterOptions s_structuredValueWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static BrowserDiagnosticEvent? TryMap(BrowserLogsCdpProtocolEvent protocolEvent)
    {
        return protocolEvent switch
        {
            BrowserLogsConsoleApiCalledEvent consoleApiCalledEvent => CreateConsoleEvent(consoleApiCalledEvent.Parameters),
            BrowserLogsExceptionThrownEvent exceptionThrownEvent => CreateExceptionEvent(exceptionThrownEvent.Parameters),
            BrowserLogsLogEntryAddedEvent logEntryAddedEvent => CreateLogEntryEvent(logEntryAddedEvent.Parameters),
            BrowserLogsRequestWillBeSentEvent requestWillBeSentEvent => CreateRequestStartedEvent(requestWillBeSentEvent.Parameters),
            BrowserLogsResponseReceivedEvent responseReceivedEvent => CreateResponseReceivedEvent(responseReceivedEvent.Parameters),
            BrowserLogsLoadingFinishedEvent loadingFinishedEvent => CreateRequestCompletedEvent(loadingFinishedEvent.Parameters),
            BrowserLogsLoadingFailedEvent loadingFailedEvent => CreateRequestFailedEvent(loadingFailedEvent.Parameters),
            _ => null
        };
    }

    private static BrowserConsoleDiagnosticEvent CreateConsoleEvent(BrowserLogsRuntimeConsoleApiCalledParameters parameters)
    {
        var level = parameters.Type ?? "log";
        var message = parameters.Args is { Length: > 0 }
            ? string.Join(" ", parameters.Args.Select(FormatRemoteObject).Where(static value => !string.IsNullOrEmpty(value)))
            : string.Empty;

        return new BrowserConsoleDiagnosticEvent(level, message);
    }

    private static BrowserExceptionDiagnosticEvent CreateExceptionEvent(BrowserLogsExceptionThrownParameters parameters)
    {
        var exceptionDetails = parameters.ExceptionDetails;
        var message = exceptionDetails?.Exception?.Description
            ?? exceptionDetails?.Text
            ?? "Unhandled browser exception";

        return new BrowserExceptionDiagnosticEvent(message, CreateLocation(exceptionDetails));
    }

    private static BrowserLogEntryDiagnosticEvent CreateLogEntryEvent(BrowserLogsLogEntryAddedParameters parameters)
    {
        var entry = parameters.Entry;
        return new BrowserLogEntryDiagnosticEvent(
            entry?.Level ?? "info",
            entry?.Text ?? string.Empty,
            CreateLocation(entry));
    }

    private static BrowserNetworkRequestStartedDiagnosticEvent CreateRequestStartedEvent(BrowserLogsRequestWillBeSentParameters parameters)
    {
        return new BrowserNetworkRequestStartedDiagnosticEvent(
            parameters.RequestId ?? string.Empty,
            parameters.Request?.Method ?? string.Empty,
            parameters.Request?.Url ?? string.Empty,
            parameters.Type,
            parameters.Timestamp,
            CreateResponseDetails(parameters.RedirectResponse));
    }

    private static BrowserNetworkResponseReceivedDiagnosticEvent CreateResponseReceivedEvent(BrowserLogsResponseReceivedParameters parameters)
    {
        return new BrowserNetworkResponseReceivedDiagnosticEvent(
            parameters.RequestId ?? string.Empty,
            parameters.Type,
            CreateResponseDetails(parameters.Response));
    }

    private static BrowserNetworkRequestCompletedDiagnosticEvent CreateRequestCompletedEvent(BrowserLogsLoadingFinishedParameters parameters)
    {
        return new BrowserNetworkRequestCompletedDiagnosticEvent(
            parameters.RequestId ?? string.Empty,
            parameters.Timestamp,
            parameters.EncodedDataLength);
    }

    private static BrowserNetworkRequestFailedDiagnosticEvent CreateRequestFailedEvent(BrowserLogsLoadingFailedParameters parameters)
    {
        return new BrowserNetworkRequestFailedDiagnosticEvent(
            parameters.RequestId ?? string.Empty,
            parameters.Timestamp,
            parameters.ErrorText,
            parameters.BlockedReason,
            parameters.Canceled);
    }

    private static BrowserNetworkResponseDetails? CreateResponseDetails(BrowserLogsResponse? response)
    {
        return response is null
            ? null
            : new BrowserNetworkResponseDetails(
                response.Url,
                response.Status,
                response.StatusText,
                response.FromDiskCache,
                response.FromServiceWorker);
    }

    private static BrowserDiagnosticSourceLocation? CreateLocation(BrowserLogsSourceLocation? sourceLocation)
    {
        return sourceLocation is null
            ? null
            : new BrowserDiagnosticSourceLocation(sourceLocation.Url, sourceLocation.LineNumber, sourceLocation.ColumnNumber);
    }

    private static string FormatRemoteObject(BrowserLogsCdpProtocolRemoteObject remoteObject)
    {
        // Console arguments can arrive either as pre-rendered descriptions or as structured values that need stable
        // formatting for logs and tests.
        if (remoteObject.Value is BrowserLogsCdpProtocolValue value)
        {
            return value switch
            {
                BrowserLogsCdpProtocolStringValue stringValue => stringValue.Value,
                BrowserLogsCdpProtocolNullValue => "null",
                BrowserLogsCdpProtocolBooleanValue booleanValue => booleanValue.Value ? bool.TrueString : bool.FalseString,
                BrowserLogsCdpProtocolNumberValue numberValue => numberValue.RawValue,
                _ => FormatStructuredValue(value)
            };
        }

        if (!string.IsNullOrEmpty(remoteObject.UnserializableValue))
        {
            return remoteObject.UnserializableValue;
        }

        return remoteObject.Description ?? string.Empty;
    }

    private static string FormatStructuredValue(BrowserLogsCdpProtocolValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, s_structuredValueWriterOptions);
        WriteStructuredValue(writer, value);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStructuredValue(Utf8JsonWriter writer, BrowserLogsCdpProtocolValue value)
    {
        switch (value)
        {
            case BrowserLogsCdpProtocolArrayValue arrayValue:
                writer.WriteStartArray();
                foreach (var item in arrayValue.Items)
                {
                    WriteStructuredValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case BrowserLogsCdpProtocolBooleanValue booleanValue:
                writer.WriteBooleanValue(booleanValue.Value);
                break;
            case BrowserLogsCdpProtocolNullValue:
                writer.WriteNullValue();
                break;
            case BrowserLogsCdpProtocolNumberValue numberValue:
                writer.WriteRawValue(numberValue.RawValue, skipInputValidation: false);
                break;
            case BrowserLogsCdpProtocolObjectValue objectValue:
                writer.WriteStartObject();
                foreach (var (propertyName, propertyValue) in objectValue.Properties)
                {
                    writer.WritePropertyName(propertyName);
                    WriteStructuredValue(writer, propertyValue);
                }

                writer.WriteEndObject();
                break;
            case BrowserLogsCdpProtocolStringValue stringValue:
                writer.WriteStringValue(stringValue.Value);
                break;
        }
    }
}
