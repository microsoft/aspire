// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Headers;
using AspireWithBlazor.ClientServiceDefaults.Telemetry.Serializer;

namespace AspireWithBlazor.ClientServiceDefaults.Telemetry;

/// <summary>
/// A WebAssembly-compatible metrics collector and exporter that uses truly async HTTP calls
/// without blocking. This is necessary because the standard OpenTelemetry MeterProvider
/// doesn't work on WebAssembly due to platform limitations.
/// </summary>
/// <remarks>
/// This exporter manually collects metrics using a <see cref="MeterListener"/> and
/// periodically exports them to the OTLP endpoint using async HTTP POST.
/// Unlike the standard exporter, this uses a fire-and-forget pattern to avoid WebAssembly deadlocks.
/// </remarks>
public sealed class WebAssemblyOtlpMetricExporter : IDisposable
{
    private static readonly MediaTypeHeaderValue s_protobufMediaType = new("application/x-protobuf");

    private readonly Uri _endpoint;
    private readonly HttpClient _httpClient;
    private readonly string _serviceName;
    private readonly MeterListener _meterListener;
    private readonly List<OtlpMetricSerializer.CapturedMetric> _capturedMetrics = new();
    private readonly object _lock = new();
    private readonly Timer _exportTimer;
    private readonly DateTimeOffset _startTime;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebAssemblyOtlpMetricExporter"/> class.
    /// </summary>
    /// <param name="endpoint">The OTLP HTTP endpoint (e.g., https://localhost:21188/v1/metrics).</param>
    /// <param name="serviceName">The service name to use in resource attributes.</param>
    /// <param name="meterNames">The meter names to listen for. If null, listens to all meters.</param>
    /// <param name="exportIntervalMs">The export interval in milliseconds. Default is 10000 (10 seconds).</param>
    /// <param name="httpClient">Optional HTTP client to use. If null, a new one is created.</param>
    public WebAssemblyOtlpMetricExporter(
        Uri endpoint,
        string serviceName,
        IEnumerable<string>? meterNames = null,
        int exportIntervalMs = 10000,
        HttpClient? httpClient = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
        _startTime = DateTimeOffset.UtcNow;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        var meterNamesSet = meterNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"[WebAssemblyOtlpMetricExporter] Created exporter for endpoint: {_endpoint}");
        if (meterNamesSet is not null)
        {
            Console.WriteLine($"[WebAssemblyOtlpMetricExporter] Listening to meters: {string.Join(", ", meterNamesSet)}");
        }

        // Create meter listener
        _meterListener = new MeterListener();

        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            // Filter by meter name if specified
            if (meterNamesSet is null || meterNamesSet.Contains(instrument.Meter.Name))
            {
                Console.WriteLine($"[WebAssemblyOtlpMetricExporter] Subscribing to instrument: {instrument.Meter.Name}/{instrument.Name}");
                listener.EnableMeasurementEvents(instrument);
            }
        };

        // Set up measurement callbacks for different types
        _meterListener.SetMeasurementEventCallback<int>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<long>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<float>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<double>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<decimal>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<short>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<byte>(OnMeasurement);

        _meterListener.Start();

        // Set up periodic export timer
        _exportTimer = new Timer(
            callback: _ => ExportMetrics(),
            state: null,
            dueTime: exportIntervalMs,
            period: exportIntervalMs);

        Console.WriteLine($"[WebAssemblyOtlpMetricExporter] Started with export interval: {exportIntervalMs}ms");
    }

    private void OnMeasurement<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        where T : struct
    {
        var value = Convert.ToDouble(measurement, CultureInfo.InvariantCulture);
        var longValue = Convert.ToInt64(measurement, CultureInfo.InvariantCulture);

        var metricType = instrument switch
        {
            Counter<T> or ObservableCounter<T> => typeof(T) == typeof(double) || typeof(T) == typeof(float)
                ? OtlpMetricSerializer.MetricType.DoubleSum
                : OtlpMetricSerializer.MetricType.LongSum,
            ObservableGauge<T> or UpDownCounter<T> or ObservableUpDownCounter<T> => typeof(T) == typeof(double) || typeof(T) == typeof(float)
                ? OtlpMetricSerializer.MetricType.DoubleGauge
                : OtlpMetricSerializer.MetricType.LongGauge,
            Histogram<T> => OtlpMetricSerializer.MetricType.DoubleSum, // Simplified: treat histograms as sums for now
            _ => OtlpMetricSerializer.MetricType.DoubleGauge
        };

        var capturedMetric = new OtlpMetricSerializer.CapturedMetric
        {
            Name = instrument.Name,
            Description = instrument.Description,
            Unit = instrument.Unit,
            Type = metricType,
            Value = value,
            LongValue = longValue,
            Timestamp = DateTimeOffset.UtcNow,
            StartTime = _startTime,
            Attributes = tags.Length > 0 ? tags.ToArray() : null
        };

        lock (_lock)
        {
            _capturedMetrics.Add(capturedMetric);
        }
    }

    private void ExportMetrics()
    {
        if (_disposed)
        {
            return;
        }

        List<OtlpMetricSerializer.CapturedMetric> metricsToExport;
        lock (_lock)
        {
            if (_capturedMetrics.Count == 0)
            {
                return;
            }

            metricsToExport = new List<OtlpMetricSerializer.CapturedMetric>(_capturedMetrics);
            _capturedMetrics.Clear();
        }

        Console.WriteLine($"[WebAssemblyOtlpMetricExporter] Exporting {metricsToExport.Count} metrics");

        try
        {
            var payload = OtlpMetricSerializer.SerializeMetricData(metricsToExport, _serviceName);
            Console.WriteLine($"[WebAssemblyOtlpMetricExporter] Serialized payload size: {payload.Length} bytes");

            // Fire-and-forget the HTTP call
            SendAsync(payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebAssemblyOtlpMetricExporter] Export failed: {ex.Message}");
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

            Console.WriteLine($"[WebAssemblyOtlpMetricExporter] Sending HTTP POST to {_endpoint}");
            var response = await _httpClient.PostAsync(_endpoint, content).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[WebAssemblyOtlpMetricExporter] HTTP POST succeeded: {response.StatusCode}");
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.WriteLine($"[WebAssemblyOtlpMetricExporter] HTTP POST failed: {response.StatusCode} - {responseBody}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebAssemblyOtlpMetricExporter] SendAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Forces an immediate export of all collected metrics.
    /// </summary>
    public void Flush()
    {
        ExportMetrics();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _exportTimer.Dispose();
        _meterListener.Dispose();

        // Final flush
        ExportMetrics();

        Console.WriteLine($"[WebAssemblyOtlpMetricExporter] Disposed");
    }
}
