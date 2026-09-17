// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Prepares install-route sidecar updates while preserving
/// route-specific and forward-compatible fields.
/// </summary>
internal static class InstallSidecarWriter
{
    /// <summary>
    /// Prepares an atomic sidecar update for a CLI self-update.
    /// The selected channel is written while version and commit are removed so the
    /// replacement binary's assembly metadata supplies those executable-specific values.
    /// A missing sidecar is left absent because the update path cannot infer the original
    /// install route's required <c>source</c> value.
    /// </summary>
    /// <param name="binaryDirectory">Directory containing the CLI binary.</param>
    /// <param name="channel">Channel selected for the installed CLI.</param>
    /// <returns>The prepared update, or <see langword="null"/> when no sidecar exists.</returns>
    internal static PreparedInstallSidecarUpdate? PrepareForSelfUpdate(string binaryDirectory, string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        var sidecarPath = Path.Combine(binaryDirectory, InstallSidecarReader.SidecarFileName);
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        using var existingSidecar = ReadExistingSidecar(sidecarPath);

        var temporaryPath = $"{sidecarPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();

                    foreach (var property in existingSidecar.RootElement.EnumerateObject())
                    {
                        if (!property.NameEquals("channel") &&
                            !property.NameEquals("version") &&
                            !property.NameEquals("commit"))
                        {
                            property.WriteTo(writer);
                        }
                    }

                    writer.WriteString("channel", channel);
                    writer.WriteEndObject();
                }

                stream.WriteByte((byte)'\n');
                if (stream.Position > InstallSidecarReader.MaxSidecarBytes)
                {
                    throw new InvalidDataException(
                        $"Prepared sidecar file size {stream.Position} bytes exceeds the {InstallSidecarReader.MaxSidecarBytes}-byte limit.");
                }

                stream.Flush(flushToDisk: true);
            }

            return new PreparedInstallSidecarUpdate(temporaryPath, sidecarPath);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static JsonDocument ReadExistingSidecar(string sidecarPath)
    {
        var length = new FileInfo(sidecarPath).Length;
        if (length > InstallSidecarReader.MaxSidecarBytes)
        {
            throw new InvalidDataException(
                $"Sidecar file size {length} bytes exceeds the {InstallSidecarReader.MaxSidecarBytes}-byte limit.");
        }

        using var stream = File.OpenRead(sidecarPath);
        var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("Install sidecar root must be a JSON object.");
        }

        return document;
    }
}

/// <summary>
/// Represents a sidecar update that has been fully written and is ready to commit atomically.
/// </summary>
internal sealed class PreparedInstallSidecarUpdate(string temporaryPath, string sidecarPath) : IDisposable
{
    private string? _temporaryPath = temporaryPath;

    /// <summary>
    /// Replaces the existing sidecar with the prepared update.
    /// </summary>
    internal void Commit()
    {
        if (_temporaryPath is null)
        {
            throw new InvalidOperationException("The prepared sidecar update has already been committed or disposed.");
        }

        File.Move(_temporaryPath, sidecarPath, overwrite: true);
        _temporaryPath = null;
    }

    public void Dispose()
    {
        if (_temporaryPath is not null && File.Exists(_temporaryPath))
        {
            File.Delete(_temporaryPath);
        }

        _temporaryPath = null;
    }
}
