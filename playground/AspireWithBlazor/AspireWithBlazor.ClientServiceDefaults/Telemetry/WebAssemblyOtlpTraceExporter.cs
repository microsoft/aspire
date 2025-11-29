// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Http.Headers;
using AspireWithBlazor.ClientServiceDefaults.Telemetry.Serializer;
using OpenTelemetry;

namespace AspireWithBlazor.ClientServiceDefaults.Telemetry;

/// <summary>
/// A WebAssembly-compatible OTLP trace exporter that uses truly async HTTP calls
/// without blocking. This is necessary because WebAssembly is single-threaded
/// and cannot use the blocking patterns in the standard OtlpTraceExporter.
/// </summary>
/// <remarks>
/// This exporter serializes traces to OTLP protobuf format (application/x-protobuf)
/// and sends them directly to the dashboard endpoint using async HTTP POST.
/// </remarks>
public sealed class WebAssemblyOtlpTraceExporter : BaseExporter<Activity>
{
    private static readonly MediaTypeHeaderValue s_protobufMediaType = new("application/x-protobuf");

    private readonly Uri _endpoint;
    private readonly HttpClient _httpClient;
    private readonly string _serviceName;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebAssemblyOtlpTraceExporter"/> class.
    /// </summary>
    /// <param name="endpoint">The OTLP HTTP endpoint (e.g., https://localhost:21188/v1/traces).</param>
    /// <param name="serviceName">The service name to use in resource attributes.</param>
    /// <param name="httpClient">Optional HTTP client to use. If null, a new one is created.</param>
    public WebAssemblyOtlpTraceExporter(Uri endpoint, string serviceName, HttpClient? httpClient = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        Console.WriteLine($"[WebAssemblyOtlpTraceExporter] Created exporter for endpoint: {_endpoint}");
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        Console.WriteLine($"[WebAssemblyOtlpTraceExporter] Export called with {batch.Count} activities");

        // Convert batch to a list we can enumerate multiple times
        var activities = new List<Activity>();
        foreach (var activity in batch)
        {
            activities.Add(activity);
        }

        if (activities.Count == 0)
        {
            Console.WriteLine($"[WebAssemblyOtlpTraceExporter] No activities to export");
            return ExportResult.Success;
        }

        Console.WriteLine($"[WebAssemblyOtlpTraceExporter] Serializing {activities.Count} activities to protobuf");

        try
        {
            // Serialize to OTLP protobuf format
            var protobufPayload = OtlpTraceSerializer.SerializeTraceData(activities, _serviceName);
            Console.WriteLine($"[WebAssemblyOtlpTraceExporter] Serialized payload size: {protobufPayload.Length} bytes");

            // Fire-and-forget the HTTP call - this is the key difference from the standard exporter
            // We don't block on the result, which avoids the WebAssembly single-thread deadlock
            SendAsync(protobufPayload);

            Console.WriteLine($"[WebAssemblyOtlpTraceExporter] Export request sent (fire-and-forget)");
            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebAssemblyOtlpTraceExporter] Export failed: {ex.Message}");
            Console.WriteLine($"[WebAssemblyOtlpTraceExporter] Exception: {ex}");
            return ExportResult.Failure;
        }
    }

    /// <summary>
    /// Sends the serialized protobuf payload asynchronously without blocking.
    /// This is a fire-and-forget pattern to avoid WebAssembly deadlock.
    /// </summary>
    private async void SendAsync(byte[] payload)
    {
        try
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = s_protobufMediaType;

            Console.WriteLine($"[WebAssemblyOtlpTraceExporter] Sending HTTP POST to {_endpoint} with Content-Type: application/x-protobuf");
            var response = await _httpClient.PostAsync(_endpoint, content).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[WebAssemblyOtlpTraceExporter] HTTP POST succeeded: {response.StatusCode}");
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.WriteLine($"[WebAssemblyOtlpTraceExporter] HTTP POST failed: {response.StatusCode} - {responseBody}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebAssemblyOtlpTraceExporter] SendAsync failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        Console.WriteLine($"[WebAssemblyOtlpTraceExporter] Shutdown called");
        return true;
    }
}
