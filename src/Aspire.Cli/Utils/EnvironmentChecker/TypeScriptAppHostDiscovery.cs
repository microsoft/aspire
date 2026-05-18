// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Describes a TypeScript AppHost discovered relative to a working directory.
/// </summary>
/// <param name="AppHostFile">The <c>apphost.ts</c> file that was discovered.</param>
/// <param name="AppHostDirectory">The directory containing <see cref="AppHostFile"/>.</param>
internal sealed record TypeScriptAppHostLocation(FileInfo AppHostFile, DirectoryInfo AppHostDirectory);

/// <summary>
/// Discovers a TypeScript AppHost (<c>apphost.ts</c>) near the current working directory.
/// </summary>
/// <remarks>
/// The discovery is intentionally narrow to keep <c>aspire doctor</c> noise-free for
/// non-polyglot projects. It searches:
/// <list type="number">
///   <item>The working directory itself.</item>
///   <item>Each immediate subdirectory of the working directory (one level only).</item>
///   <item>Hints from <c>.aspire/settings.json</c> when its <c>language</c> indicates TypeScript.</item>
/// </list>
/// </remarks>
internal static class TypeScriptAppHostDiscovery
{
    private const string AppHostFileName = "apphost.ts";
    private const string PackageJsonFileName = "package.json";
    private const string SettingsFolderName = ".aspire";
    private const string SettingsFileName = "settings.json";

    private static readonly string[] s_typeScriptLanguageMarkers =
    [
        "typescript/nodejs",
        "typescript",
    ];

    /// <summary>
    /// Attempts to discover a TypeScript AppHost near <paramref name="workingDirectory"/>.
    /// </summary>
    /// <param name="workingDirectory">The directory to start searching from.</param>
    /// <returns>The discovered AppHost location, or <see langword="null"/> if none was found.</returns>
    public static TypeScriptAppHostLocation? TryDiscover(DirectoryInfo workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        if (!workingDirectory.Exists)
        {
            return null;
        }

        // 1. apphost.ts in the working directory itself.
        if (TryGetLocation(workingDirectory, out var location))
        {
            return location;
        }

        // 2. apphost.ts in a directory referenced by .aspire/settings.json.
        if (TryFromSettings(workingDirectory, out location))
        {
            return location;
        }

        // 3. apphost.ts in any immediate subdirectory.
        foreach (var child in EnumerateChildDirectoriesSafely(workingDirectory))
        {
            if (TryGetLocation(child, out location))
            {
                return location;
            }
        }

        return null;
    }

    private static bool TryGetLocation(DirectoryInfo directory, out TypeScriptAppHostLocation location)
    {
        var appHostPath = Path.Combine(directory.FullName, AppHostFileName);
        var packageJsonPath = Path.Combine(directory.FullName, PackageJsonFileName);

        if (File.Exists(appHostPath) && File.Exists(packageJsonPath))
        {
            location = new TypeScriptAppHostLocation(new FileInfo(appHostPath), directory);
            return true;
        }

        location = null!;
        return false;
    }

    private static bool TryFromSettings(DirectoryInfo workingDirectory, out TypeScriptAppHostLocation location)
    {
        location = null!;

        var settingsPath = Path.Combine(workingDirectory.FullName, SettingsFolderName, SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            return false;
        }

        string? language;
        string? appHostPath;
        try
        {
            using var stream = File.OpenRead(settingsPath);
            var node = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (node is not JsonObject obj)
            {
                return false;
            }

            language = obj["language"]?.GetValue<string>();
            appHostPath = obj["appHostPath"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (language is null ||
            !s_typeScriptLanguageMarkers.Any(marker => string.Equals(marker, language, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(appHostPath))
        {
            return TryGetLocation(workingDirectory, out location);
        }

        var resolved = Path.IsPathRooted(appHostPath)
            ? appHostPath
            : Path.GetFullPath(Path.Combine(workingDirectory.FullName, appHostPath));

        var directory = new DirectoryInfo(Path.GetDirectoryName(resolved) ?? workingDirectory.FullName);
        return TryGetLocation(directory, out location);
    }

    private static IEnumerable<DirectoryInfo> EnumerateChildDirectoriesSafely(DirectoryInfo workingDirectory)
    {
        DirectoryInfo[] children;
        try
        {
            children = workingDirectory.GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            // Skip well-known noise directories.
            if (child.Name.StartsWith('.') ||
                string.Equals(child.Name, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(child.Name, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(child.Name, "obj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return child;
        }
    }
}
