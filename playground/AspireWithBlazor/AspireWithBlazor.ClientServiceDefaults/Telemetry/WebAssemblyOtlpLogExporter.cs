// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Headers;
using AspireWithBlazor.ClientServiceDefaults.Telemetry.Serializer;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AspireWithBlazor.ClientServiceDefaults.Telemetry;

/// <summary>
/// A WebAssembly-compatible OTLP log exporter that uses truly async HTTP calls
/// without blocking. This is necessary because WebAssembly is single-threaded
/// and cannot use the blocking patterns in the standard OtlpLogExporter.
/// </summary>
/// <remarks>
/// This exporter serializes logs to OTLP protobuf format (application/x-protobuf)
/// and sends them directly to the dashboard endpoint using async HTTP POST.
/// </remarks>
public sealed class WebAssemblyOtlpLogExporter : BaseExporter<LogRecord>
{
    private static readonly MediaTypeHeaderValue s_protobufMediaType = new("application/x-protobuf");

    private readonly Uri _endpoint;
    private readonly HttpClient _httpClient;
    private readonly string _serviceName;
    private readonly Dictionary<string, string>? _headers;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebAssemblyOtlpLogExporter"/> class.
    /// </summary>
    /// <param name="endpoint">The OTLP HTTP endpoint (e.g., https://localhost:21188/v1/logs).</param>
    /// <param name="serviceName">The service name to use in resource attributes.</param>
    /// <param name="headers">Optional headers to send with each request (e.g., x-otlp-api-key for authentication).</param>
    /// <param name="httpClient">Optional HTTP client to use. If null, a new one is created.</param>
    public WebAssemblyOtlpLogExporter(Uri endpoint, string serviceName, Dictionary<string, string>? headers = null, HttpClient? httpClient = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
        _headers = headers;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        // Convert batch to our simplified data format - we need to capture the data
        // before returning because the LogRecord objects will be pooled/recycled
        var logRecords = new List<LogRecordData>();
        foreach (var logRecord in batch)
        {
            var data = CaptureLogRecord(logRecord);
            logRecords.Add(data);
        }

        if (logRecords.Count == 0)
        {
            return ExportResult.Success;
        }

        try
        {
            // Serialize to OTLP protobuf format
            var protobufPayload = OtlpLogSerializer.SerializeLogsData(logRecords, _serviceName);

            // Fire-and-forget the HTTP call - this is the key difference from the standard exporter
            // We don't block on the result, which avoids the WebAssembly single-thread deadlock
            SendAsync(protobufPayload);

            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebAssemblyOtlpLogExporter] Export failed: {ex.Message}");
            return ExportResult.Failure;
        }
    }

    /// <summary>
    /// Captures the log record data before the LogRecord is recycled.
    /// </summary>
    private static LogRecordData CaptureLogRecord(LogRecord logRecord)
    {
        // Capture attributes
        List<KeyValuePair<string, object?>>? attributes = null;
        if (logRecord.Attributes != null)
        {
            attributes = new List<KeyValuePair<string, object?>>();
            foreach (var attr in logRecord.Attributes)
            {
                attributes.Add(new KeyValuePair<string, object?>(attr.Key, attr.Value?.ToString()));
            }
        }

        return new LogRecordData
        {
            Timestamp = logRecord.Timestamp,
            CategoryName = logRecord.CategoryName,
            LogLevel = logRecord.LogLevel,
            EventId = logRecord.EventId,
            FormattedMessage = logRecord.FormattedMessage ?? logRecord.Body,
            Exception = logRecord.Exception,
            Attributes = attributes,
            TraceId = logRecord.TraceId,
            SpanId = logRecord.SpanId,
            TraceFlags = logRecord.TraceFlags
        };
    }

    /// <summary>
    /// Sends the serialized protobuf payload asynchronously without blocking.
    /// This is a fire-and-forget pattern to avoid WebAssembly deadlock.
    /// </summary>
    private async void SendAsync(byte[] payload)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = s_protobufMediaType;

            // Add custom headers (e.g., x-otlp-api-key for authentication)
            if (_headers is not null)
            {
                foreach (var header in _headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.WriteLine($"[WebAssemblyOtlpLogExporter] HTTP POST failed: {response.StatusCode} - {responseBody}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebAssemblyOtlpLogExporter] SendAsync failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        return true;
    }
}
