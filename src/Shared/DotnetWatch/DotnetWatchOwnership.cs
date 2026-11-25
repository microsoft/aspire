// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;

namespace Aspire.Shared.DotnetWatch;

/// <summary>
/// Identifies CLI-owned watch runs independently of the execution-service settings injected by the watcher.
/// </summary>
internal static class DotnetWatchOwnership
{
    internal const string OwnerEnvironmentVariable = "ASPIRE_DOTNET_WATCH_OWNER";
    internal const string CliOwner = "cli";
    internal const string AppHostControlEnvironmentVariable = "ASPIRE_DOTNET_WATCH_HOST_CONTROL";
    internal const string AppHostSessionEnvironmentVariable = "ASPIRE_DOTNET_WATCH_HOST_SESSION";
    internal const string ResourceWatchSuppressionEnvironmentVariable = "ASPIRE_RESOURCE_WATCH_SUPPRESS";

    /// <summary>
    /// The values that <see cref="WatcherMSBuildEnvironmentVariables"/> would have in the AppHost without the watcher host,
    /// as a JSON object that omits unset variables, for example <c>{"MSBuildSDKsPath":"/custom/Sdks"}</c> or <c>{}</c>.
    /// </summary>
    internal const string AppHostEnvironmentVariable = "ASPIRE_DOTNET_WATCH_HOST_ENV";

    /// <summary>
    /// The variables the watcher sets on its own process when it loads MSBuild from the SDK it watches with, and which
    /// processes it launches, such as a watched AppHost, inherit.
    /// </summary>
    internal static IReadOnlyList<string> WatcherMSBuildEnvironmentVariables { get; } = Array.AsReadOnly(
    [
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath"
    ]);

    internal static IReadOnlyList<string> PrivateEnvironmentVariables { get; } = Array.AsReadOnly(
    [
        OwnerEnvironmentVariable,
        AppHostControlEnvironmentVariable,
        AppHostSessionEnvironmentVariable,
        ResourceWatchSuppressionEnvironmentVariable,
        AppHostEnvironmentVariable
    ]);

    internal static bool IsCliOwned(string? owner) => string.Equals(owner, CliOwner, StringComparison.Ordinal);

    internal static bool IsPrivateEnvironmentVariable(string name)
        => PrivateEnvironmentVariables.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Encodes the current values of <see cref="WatcherMSBuildEnvironmentVariables"/> for <see cref="AppHostEnvironmentVariable"/>.</summary>
    internal static string EncodeAppHostEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var name in WatcherMSBuildEnvironmentVariables)
            {
                if (getEnvironmentVariable(name) is { } value)
                {
                    writer.WriteString(name, value);
                }
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Sets each of <see cref="WatcherMSBuildEnvironmentVariables"/> to the value that <paramref name="encoded"/> records,
    /// and unsets the ones it omits. Nothing is set unless the whole record is valid.
    /// </summary>
    /// <exception cref="JsonException"><paramref name="encoded"/> isn't a JSON object of string values.</exception>
    internal static void RestoreAppHostEnvironment(string encoded, Action<string, string?> setEnvironmentVariable)
    {
        var values = new List<(string Name, string? Value)>();
        using (var document = JsonDocument.Parse(encoded))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException($"'{AppHostEnvironmentVariable}' must be a JSON object.");
            }

            foreach (var name in WatcherMSBuildEnvironmentVariables)
            {
                if (!document.RootElement.TryGetProperty(name, out var value))
                {
                    values.Add((name, null));
                }
                else if (value.ValueKind == JsonValueKind.String)
                {
                    values.Add((name, value.GetString()));
                }
                else
                {
                    throw new JsonException($"'{AppHostEnvironmentVariable}' has a non-string '{name}'.");
                }
            }
        }

        foreach (var (name, value) in values)
        {
            setEnvironmentVariable(name, value);
        }
    }
}
