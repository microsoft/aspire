// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Updates identity fields in the install-route sidecar while preserving the
/// route and any fields written by newer installers.
/// </summary>
internal static class InstallSidecarWriter
{
    /// <summary>
    /// Atomically updates the selected channel in the sidecar next to the CLI binary.
    /// A missing sidecar is left absent because the update path cannot infer the
    /// original install route's required <c>source</c> value.
    /// </summary>
    /// <param name="binaryDirectory">Directory containing the CLI binary.</param>
    /// <param name="channel">Channel selected for the installed CLI.</param>
    public static void UpdateChannel(string binaryDirectory, string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        var sidecarPath = Path.Combine(binaryDirectory, InstallSidecarReader.SidecarFileName);
        if (!File.Exists(sidecarPath))
        {
            return;
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
                        if (!property.NameEquals("channel"))
                        {
                            property.WriteTo(writer);
                        }
                    }

                    writer.WriteString("channel", channel);
                    writer.WriteEndObject();
                }

                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, sidecarPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
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
