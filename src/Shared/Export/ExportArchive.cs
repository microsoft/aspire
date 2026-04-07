// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aspire.Otlp.Serialization;
using Aspire.Shared.Model.Serialization;

namespace Aspire.Shared.Export;

/// <summary>
/// Represents an archive of exported Aspire resource and telemetry data.
/// Populate the strongly-typed collections, then call <see cref="WriteToFile"/> or <see cref="WriteToStream"/>
/// to produce a zip archive with source-generated JSON serialization (AOT-compatible).
/// </summary>
internal sealed class ExportArchive
{
    /// <summary>
    /// Gets the collection of resource details keyed by display name.
    /// Each entry is serialized to <c>resources/{name}.json</c>.
    /// </summary>
    public Dictionary<string, ResourceJson> Resources { get; } = new();

    /// <summary>
    /// Gets the collection of console log lines keyed by resource display name.
    /// Each entry is written to <c>consolelogs/{name}.txt</c> as plain text.
    /// </summary>
    public Dictionary<string, List<string>> ConsoleLogs { get; } = new();

    /// <summary>
    /// Gets the collection of structured logs (OTLP format) keyed by resource or aggregate name.
    /// Each entry is serialized to <c>structuredlogs/{name}.json</c>.
    /// </summary>
    public Dictionary<string, OtlpTelemetryDataJson> StructuredLogs { get; } = new();

    /// <summary>
    /// Gets the collection of traces (OTLP format) keyed by resource or aggregate name.
    /// Each entry is serialized to <c>traces/{name}.json</c>.
    /// </summary>
    public Dictionary<string, OtlpTelemetryDataJson> Traces { get; } = new();

    /// <summary>
    /// Gets the collection of metrics (OTLP format) keyed by resource name.
    /// Each entry is serialized to <c>metrics/{name}.json</c>.
    /// </summary>
    public Dictionary<string, OtlpTelemetryDataJson> Metrics { get; } = new();

    /// <summary>
    /// Writes the archive contents to a file.
    /// </summary>
    /// <param name="filePath">The file path to write the zip archive to.</param>
    public void WriteToFile(string filePath)
    {
        using var fileStream = File.Create(filePath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);
        WriteEntries(archive);
    }

    /// <summary>
    /// Writes the archive contents to a stream.
    /// </summary>
    /// <param name="stream">The stream to write the zip archive to. The stream is left open.</param>
    public void WriteToStream(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntries(archive);
    }

    private static readonly OtlpJsonSerializerContext s_serializerContext = OtlpJsonSerializerContext.Default;

    private static readonly JsonWriterOptions s_writerOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private void WriteEntries(ZipArchive archive)
    {
        foreach (var (name, resource) in Resources)
        {
            var entry = archive.CreateEntry($"resources/{SanitizeFileName(name)}.json");
            using var entryStream = entry.Open();
            using var writer = new Utf8JsonWriter(entryStream, s_writerOptions);
            JsonSerializer.Serialize(writer, resource, s_serializerContext.ResourceJson);
        }

        foreach (var (name, lines) in ConsoleLogs)
        {
            var entry = archive.CreateEntry($"consolelogs/{SanitizeFileName(name)}.txt");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);

            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }
        }

        foreach (var (name, data) in StructuredLogs)
        {
            var entry = archive.CreateEntry($"structuredlogs/{SanitizeFileName(name)}.json");
            using var entryStream = entry.Open();
            using var writer = new Utf8JsonWriter(entryStream, s_writerOptions);
            JsonSerializer.Serialize(writer, data, s_serializerContext.OtlpTelemetryDataJson);
        }

        foreach (var (name, data) in Traces)
        {
            var entry = archive.CreateEntry($"traces/{SanitizeFileName(name)}.json");
            using var entryStream = entry.Open();
            using var writer = new Utf8JsonWriter(entryStream, s_writerOptions);
            JsonSerializer.Serialize(writer, data, s_serializerContext.OtlpTelemetryDataJson);
        }

        foreach (var (name, data) in Metrics)
        {
            var entry = archive.CreateEntry($"metrics/{SanitizeFileName(name)}.json");
            using var entryStream = entry.Open();
            using var writer = new Utf8JsonWriter(entryStream, s_writerOptions);
            JsonSerializer.Serialize(writer, data, s_serializerContext.OtlpTelemetryDataJson);
        }
    }

    /// <summary>
    /// Sanitizes a file name by replacing invalid characters with underscores.
    /// </summary>
    internal static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            sanitized.Append(invalidChars.Contains(c) ? '_' : c);
        }

        return sanitized.ToString();
    }
}
