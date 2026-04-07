// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.Serialization;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Otlp.Serialization;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Service for importing telemetry data from files.
/// </summary>
public sealed class TelemetryImportService
{
    private readonly TelemetryRepository _telemetryRepository;
    private readonly IOptionsMonitor<DashboardOptions> _options;
    private readonly ILogger<TelemetryImportService> _logger;

    /// <summary>
    /// Gets a value indicating whether import is enabled.
    /// </summary>
    public bool IsImportEnabled => _options.CurrentValue.UI.DisableImport != true;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryImportService"/> class.
    /// </summary>
    /// <param name="telemetryRepository">The telemetry repository.</param>
    /// <param name="options">The dashboard options.</param>
    /// <param name="logger">The logger.</param>
    public TelemetryImportService(TelemetryRepository telemetryRepository, IOptionsMonitor<DashboardOptions> options, ILogger<TelemetryImportService> logger)
    {
        _telemetryRepository = telemetryRepository;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Imports telemetry data from a file stream.
    /// </summary>
    /// <param name="fileName">The name of the file being imported.</param>
    /// <param name="stream">The file stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when import is disabled.</exception>
    public async Task ImportAsync(string fileName, Stream stream, CancellationToken cancellationToken)
    {
        if (!IsImportEnabled)
        {
            throw new InvalidOperationException("Import is disabled.");
        }

        await ImportCoreAsync(fileName, stream, allowZipFile: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportCoreAsync(string fileName, Stream stream, bool allowZipFile, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        switch (extension)
        {
            case ".zip":
                if (!allowZipFile)
                {
                    // Allowing zip file is a flag to not extract zip files inside zip files. Avoid unexpected recursion.
                    goto default;
                }

                // Copy input stream to MemoryStream because ZipArchive requires synchronous reads and seeking.
                var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
                memoryStream.Position = 0;

                await ImportZipAsync(memoryStream, cancellationToken).ConfigureAwait(false);
                break;
            case ".json":
                await ImportJsonAsync(fileName, stream, cancellationToken).ConfigureAwait(false);
                break;
            case ".txt":
                // Text files are console logs - currently ignored as per requirements
                _logger.LogDebug("Ignoring text file {FileName} - console log import not supported", fileName);
                break;
            default:
                _logger.LogDebug("Unsupported file type: {Extension}", extension);
                break;
        }
    }

    private async Task ImportZipAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            using var entryStream = entry.Open();
            await ImportCoreAsync(entry.Name, entryStream, allowZipFile: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ImportJsonAsync(string fileName, Stream stream, CancellationToken cancellationToken)
    {
        // Read the JSON content
        using var reader = new StreamReader(stream);
        var jsonContent = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            _logger.LogWarning("Empty JSON file: {FileName}", fileName);
            return;
        }

        OtlpTelemetryDataJson? telemetryData;
        try
        {
            telemetryData = JsonSerializer.Deserialize<OtlpTelemetryDataJson>(jsonContent, OtlpJsonSerializerContext.DefaultOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize JSON file: {FileName}", fileName);
            return;
        }

        if (telemetryData is null)
        {
            _logger.LogWarning("Could not deserialize telemetry data from file: {FileName}", fileName);
            return;
        }

        var imported = false;

        if (telemetryData.ResourceLogs is { Length: > 0 })
        {
            ImportLogs(telemetryData.ResourceLogs);
            _logger.LogDebug("Imported logs from {FileName}", fileName);
            imported = true;
        }

        if (telemetryData.ResourceSpans is { Length: > 0 })
        {
            ImportTraces(telemetryData.ResourceSpans);
            _logger.LogDebug("Imported traces from {FileName}", fileName);
            imported = true;
        }

        if (telemetryData.ResourceMetrics is { Length: > 0 })
        {
            ImportMetrics(telemetryData.ResourceMetrics);
            _logger.LogDebug("Imported metrics from {FileName}", fileName);
            imported = true;
        }

        if (!imported)
        {
            _logger.LogWarning("No telemetry data found in file: {FileName}", fileName);
        }
    }

    private void ImportLogs(OtlpResourceLogsJson[] resourceLogs)
    {
        var exportRequest = new OtlpExportLogsServiceRequestJson { ResourceLogs = resourceLogs };
        var protobufRequest = OtlpJsonToProtobufConverter.ToProtobuf(exportRequest);

        var addContext = new AddContext();
        _telemetryRepository.AddLogs(addContext, protobufRequest.ResourceLogs);

        _logger.LogDebug("Imported logs: {SuccessCount} succeeded, {FailureCount} failed", addContext.SuccessCount, addContext.FailureCount);
    }

    private void ImportTraces(OtlpResourceSpansJson[] resourceSpans)
    {
        var exportRequest = new OtlpExportTraceServiceRequestJson { ResourceSpans = resourceSpans };
        var protobufRequest = OtlpJsonToProtobufConverter.ToProtobuf(exportRequest);

        var addContext = new AddContext();
        _telemetryRepository.AddTraces(addContext, protobufRequest.ResourceSpans);

        _logger.LogDebug("Imported traces: {SuccessCount} succeeded, {FailureCount} failed", addContext.SuccessCount, addContext.FailureCount);
    }

    private void ImportMetrics(OtlpResourceMetricsJson[] resourceMetrics)
    {
        var exportRequest = new OtlpExportMetricsServiceRequestJson { ResourceMetrics = resourceMetrics };
        var protobufRequest = OtlpJsonToProtobufConverter.ToProtobuf(exportRequest);

        var addContext = new AddContext();
        _telemetryRepository.AddMetrics(addContext, protobufRequest.ResourceMetrics);

        _logger.LogDebug("Imported metrics: {SuccessCount} succeeded, {FailureCount} failed", addContext.SuccessCount, addContext.FailureCount);
    }
}
