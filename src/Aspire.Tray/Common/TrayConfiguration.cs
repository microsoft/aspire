// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;

namespace Aspire.Tray;

/// <summary>
/// Reads tray options from the CLI's existing user configuration without modifying it.
/// </summary>
internal static class TrayConfiguration
{
    internal const string RecentAppHostLimitKey = "tray.recentAppHostLimit";

    public static string GetSettingsPath(string cliPath)
        => GetSettingsPath(cliPath, Environment.GetEnvironmentVariable("ASPIRE_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string GetSettingsPath(string cliPath, string? configuredAspireHome, string userProfileDirectory)
        => Path.Combine(GetAspireHome(cliPath, configuredAspireHome, userProfileDirectory), "aspire.config.json");

    public static string GetSavedStatePath(string cliPath)
        => GetSavedStatePath(cliPath, Environment.GetEnvironmentVariable("ASPIRE_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string GetSavedStatePath(string cliPath, string? configuredAspireHome, string userProfileDirectory)
        => Path.Combine(GetAspireHome(cliPath, configuredAspireHome, userProfileDirectory), "tray", "apphosts.json");

    private static string GetAspireHome(string cliPath, string? configuredAspireHome, string userProfileDirectory)
    {
        // Match CLI Program.GetGlobalSettingsPath and CliPathHelper.GetAspireHomeDirectory:
        // script/localhive: <home>/bin/aspire; PR: <home>/dogfood/pr-123/bin/aspire.
        // Other installation routes use ASPIRE_HOME or ~/.aspire. settings.json is the
        // legacy project-local file, not the user configuration written by config --global.
        var home = TryGetInstalledHome(cliPath) ?? (string.IsNullOrWhiteSpace(configuredAspireHome)
            ? Path.Combine(userProfileDirectory, ".aspire") : configuredAspireHome);
        return TrayAppHostPath.Normalize(home);
    }

    /// <summary>
    /// Reads a limit of 0–50 from an explicitly selected configuration file; missing settings use 10.
    /// </summary>
    public static int LoadRecentAppHostLimit(string settingsPath)
    {
        settingsPath = TrayAppHostPath.Normalize(settingsPath);
        try
        {
            using var stream = File.OpenRead(settingsPath);
            // `aspire config set tray.recentAppHostLimit 10 --global` writes:
            // { "tray": { "recentAppHostLimit": "10" } }
            // Hand-edited integer values are also accepted. As in CLI ConfigurationHelper,
            // comments and trailing commas are supported; unrelated settings are never rewritten.
            // Legacy flattened "tray:recentAppHostLimit" keys are also accepted, but duplicate
            // case-insensitive keys or nested/flattened conflicts are rejected as ambiguous.
            using var document = JsonDocument.Parse(stream, new()
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidConfiguration(settingsPath);
            }
            var hasValue = TryGetProperty(root, "tray:recentAppHostLimit", settingsPath, out var value);
            if (TryGetProperty(root, "tray", settingsPath, out var tray))
            {
                if (tray.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidConfiguration(settingsPath);
                }
                if (TryGetProperty(tray, "recentAppHostLimit", settingsPath, out var nestedValue))
                {
                    if (hasValue)
                    {
                        throw InvalidConfiguration(settingsPath);
                    }
                    value = nestedValue;
                    hasValue = true;
                }
            }
            if (!hasValue)
            {
                return TraySavedState.DefaultRecentAppHostLimit;
            }
            var limit = 0;
            var valid = value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetInt32(out limit),
                JsonValueKind.String => int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit),
                _ => false
            };
            if (!valid || limit is < 0 or > TraySavedState.MaximumRecentAppHosts)
            {
                throw InvalidConfiguration(settingsPath);
            }
            return limit;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return TraySavedState.DefaultRecentAppHostLimit;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw InvalidConfiguration(settingsPath, ex);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, string settingsPath, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (found)
                {
                    throw InvalidConfiguration(settingsPath);
                }
                value = property.Value;
                found = true;
            }
        }
        return found;
    }

    private static InvalidOperationException InvalidConfiguration(string settingsPath, Exception? inner = null)
        => new($"Unable to read {RecentAppHostLimitKey} from '{settingsPath}'. The file must contain a valid JSON object "
            + $"and {RecentAppHostLimitKey} must be an integer from 0 to {TraySavedState.MaximumRecentAppHosts}. "
            + $"Fix the file or run 'aspire config set {RecentAppHostLimitKey} 10 --global', then restart the tray.", inner);

    private static string? TryGetInstalledHome(string cliPath)
    {
        try
        {
            var binary = new FileInfo(cliPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? cliPath;
            var directory = Path.GetDirectoryName(binary)!;
            using var stream = File.OpenRead(Path.Combine(directory, ".aspire-install.json"));
            // Keep the CLI InstallSidecarReader's 64 KiB bound and source-only read:
            // {"source":"script"}; missing/unknown installation metadata uses the default home.
            if (stream.Length > 64 * 1024)
            {
                return null;
            }
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            if (source.GetString() is "script" or "localhive")
            {
                return Path.GetDirectoryName(directory) ?? directory;
            }
            var dogfood = Path.GetDirectoryName(Path.GetDirectoryName(directory));
            return source.GetString() == "pr" && dogfood is not null && Path.GetFileName(dogfood) == "dogfood"
                ? Path.GetDirectoryName(dogfood) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            // Match the CLI's fallback for absent or unreadable installation metadata.
            // This does not suppress errors from the selected configuration file.
            return null;
        }
    }
}
