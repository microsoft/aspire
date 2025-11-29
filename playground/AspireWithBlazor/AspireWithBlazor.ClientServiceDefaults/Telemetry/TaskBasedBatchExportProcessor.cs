// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using OpenTelemetry;

namespace AspireWithBlazor.ClientServiceDefaults.Telemetry;

/// <summary>
/// A batch export processor that uses Tasks instead of Threads for WebAssembly compatibility.
/// WebAssembly is single-threaded and doesn't support the default ThreadPool-based batch export.
/// </summary>
/// <typeparam name="T">The type of telemetry data being exported.</typeparam>
public sealed class TaskBasedBatchExportProcessor<T> : BaseExportProcessor<T> where T : class
{
    private readonly int _maxQueueSize;
    private readonly int _maxExportBatchSize;
    private readonly int _scheduledDelayMilliseconds;
    private readonly int _exporterTimeoutMilliseconds;
    private readonly CircularBuffer<T> _circularBuffer;
    private readonly SemaphoreSlim _exportTrigger = new(0);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _exportTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskBasedBatchExportProcessor{T}"/> class.
    /// </summary>
    /// <param name="exporter">The exporter to use.</param>
    /// <param name="maxQueueSize">The maximum number of items in the queue.</param>
    /// <param name="scheduledDelayMilliseconds">The delay between scheduled exports.</param>
    /// <param name="exporterTimeoutMilliseconds">The timeout for the exporter.</param>
    /// <param name="maxExportBatchSize">The maximum batch size for export.</param>
    public TaskBasedBatchExportProcessor(
        BaseExporter<T> exporter,
        int maxQueueSize = 2048,
        int scheduledDelayMilliseconds = 5000,
        int exporterTimeoutMilliseconds = 30000,
        int maxExportBatchSize = 512)
        : base(exporter)
    {
        _maxQueueSize = maxQueueSize;
        _maxExportBatchSize = Math.Min(maxExportBatchSize, maxQueueSize);
        _scheduledDelayMilliseconds = scheduledDelayMilliseconds;
        _exporterTimeoutMilliseconds = exporterTimeoutMilliseconds;
        _circularBuffer = new CircularBuffer<T>(maxQueueSize);

        // Start the background export task
        _exportTask = ExportLoopAsync(_shutdownCts.Token);
        Console.WriteLine($"[TaskBasedBatchExportProcessor] Created processor for {typeof(T).Name}, maxQueueSize={maxQueueSize}, scheduledDelay={scheduledDelayMilliseconds}ms");
    }

    /// <inheritdoc/>
    public override void OnEnd(T data)
    {
        if (_disposed)
        {
            return;
        }

        Console.WriteLine($"[TaskBasedBatchExportProcessor] OnEnd called with {typeof(T).Name}, buffer count: {_circularBuffer.Count}");

        // Add to buffer; drop if full
        if (!_circularBuffer.TryAdd(data))
        {
            Console.WriteLine($"[TaskBasedBatchExportProcessor] Buffer full, dropping item");
            // Buffer is full, item dropped
            return;
        }

        // If we've hit the batch size, trigger an export
        if (_circularBuffer.Count >= _maxExportBatchSize)
        {
            Console.WriteLine($"[TaskBasedBatchExportProcessor] Triggering export, buffer count: {_circularBuffer.Count}");
            _exportTrigger.Release();
        }
    }

    /// <summary>
    /// Processes a single item. Not used in batch processing - items go through OnEnd.
    /// </summary>
    /// <param name="data">The data item.</param>
    protected override void OnExport(T data)
    {
        // For batch processing, items are collected in OnEnd and exported in batches
        // This method is required by the base class but we use OnEnd for buffering
    }

    private async Task ExportLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for either the scheduled delay or a trigger
                await Task.WhenAny(
                    _exportTrigger.WaitAsync(cancellationToken),
                    Task.Delay(_scheduledDelayMilliseconds, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await ExportBatchAsync();
        }

        // Final export on shutdown
        await ExportBatchAsync();
    }

    private async Task ExportBatchAsync()
    {
        var batch = new List<T>();

        while (batch.Count < _maxExportBatchSize && _circularBuffer.TryTake(out var item))
        {
            if (item is not null)
            {
                batch.Add(item);
            }
        }

        if (batch.Count == 0)
        {
            Console.WriteLine($"[TaskBasedBatchExportProcessor] ExportBatchAsync called but batch is empty");
            return;
        }

        Console.WriteLine($"[TaskBasedBatchExportProcessor] Exporting batch of {batch.Count} {typeof(T).Name} items");
        Console.WriteLine($"[TaskBasedBatchExportProcessor] Exporter type: {exporter.GetType().FullName}");

        try
        {
            // In WebAssembly, the OTLP exporter uses HttpClient.SendAsync().GetAwaiter().GetResult()
            // which would deadlock because there's only one thread.
            // We need to wrap the export in Task.Run to create a proper async context.
            // However, in .NET 8 WebAssembly, Task.Run still runs on the same thread.
            // The only way to avoid the deadlock is to use a fire-and-forget pattern.
            var batchToExport = new Batch<T>([.. batch], batch.Count);
            Console.WriteLine($"[TaskBasedBatchExportProcessor] Calling exporter.Export with batch of {batchToExport.Count} items");
            
            // Schedule the export on a new async context that won't deadlock
            _ = Task.Run(async () =>
            {
                // Small yield to ensure we're not blocking the main flow
                await Task.Yield();
                try
                {
                    var result = exporter.Export(batchToExport);
                    Console.WriteLine($"[TaskBasedBatchExportProcessor] Export result: {result}");
                }
                catch (Exception innerEx)
                {
                    Console.WriteLine($"[TaskBasedBatchExportProcessor] Export failed in Task.Run: {innerEx.Message}");
                }
            });
            
            Console.WriteLine($"[TaskBasedBatchExportProcessor] Export scheduled (fire-and-forget)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Export failed: {ex.Message}");
            Console.WriteLine($"[TaskBasedBatchExportProcessor] Export failed: {ex.Message}");
            Console.WriteLine($"[TaskBasedBatchExportProcessor] Exception: {ex}");
        }

        // Yield to allow other async operations
        await Task.Yield();
    }

    /// <inheritdoc/>
    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        _exportTrigger.Release();
        return true;
    }

    /// <inheritdoc/>
    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        _shutdownCts.Cancel();

        // In WebAssembly we can't block with Wait(), so we just request cancellation
        // and let the export loop finish naturally
        return true;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _shutdownCts.Cancel();
                _shutdownCts.Dispose();
                _exportTrigger.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
