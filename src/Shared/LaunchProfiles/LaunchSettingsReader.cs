// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Aspire.Hosting;

internal static class LaunchSettingsReader
{
#if !CLI
    /// <summary>
    /// Reads launch settings from the <c>Properties/launchSettings.json</c> file within the specified directory.
    /// </summary>
    /// <param name="directoryPath">The directory to look for launch settings in. If <see langword="null"/>, a relative path is used.</param>
    /// <param name="resourceIdentifier">A descriptive identifier used in error messages when JSON parsing fails.</param>
    /// <returns>The deserialized <see cref="LaunchSettings"/>, or <see langword="null"/> if the file does not exist.</returns>
    internal static LaunchSettings? GetLaunchSettingsFromDirectory(string? directoryPath, string resourceIdentifier)
    {
        var launchSettingsFilePath = directoryPath is null
            ? Path.Combine("Properties", "launchSettings.json")
            : Path.Combine(Path.GetFullPath(directoryPath), "Properties", "launchSettings.json");

        if (!File.Exists(launchSettingsFilePath))
        {
            return null;
        }

        return ReadLaunchSettingsFile(launchSettingsFilePath, resourceIdentifier);
    }

    /// <summary>
    /// Reads and deserializes launch settings from the specified file.
    /// </summary>
    /// <param name="launchSettingsFilePath">The path to the launch settings file.</param>
    /// <param name="resourceIdentifier">A descriptive identifier used in error messages when JSON parsing fails.</param>
    /// <returns>The deserialized <see cref="LaunchSettings"/>.</returns>
    /// <exception cref="DistributedApplicationException">Thrown when the file is empty or contains malformed JSON.</exception>
    internal static LaunchSettings? ReadLaunchSettingsFile(string launchSettingsFilePath, string resourceIdentifier)
        => ReadWithDistributedApplicationException(() => ReadLaunchSettingsFile(
            launchSettingsFilePath,
            resourceIdentifier,
            LaunchSettingsSerializerContext.Default.LaunchSettings));

    private static LaunchSettings? ReadWithDistributedApplicationException(Func<LaunchSettings?> read)
    {
        try
        {
            return read();
        }
        catch (InvalidDataException ex)
        {
            throw new DistributedApplicationException(ex.Message, ex.InnerException ?? ex);
        }
    }
#endif

    internal static TLaunchSettings? ReadLaunchSettingsFile<TLaunchSettings>(
        string launchSettingsFilePath,
        string resourceIdentifier,
        JsonTypeInfo<TLaunchSettings> jsonTypeInfo)
    {
        using var stream = File.OpenRead(launchSettingsFilePath);

        try
        {
            return JsonSerializer.Deserialize(stream, jsonTypeInfo);
        }
        catch (JsonException ex)
        {
            var message = $"Failed to get effective launch profile for {resourceIdentifier}. There is malformed JSON in the project's launch settings file at '{launchSettingsFilePath}'.";
            throw new InvalidDataException(message, ex);
        }
    }
}
