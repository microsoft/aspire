// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Hosting.Resources;

namespace Aspire.Hosting;

/// <summary>
/// Resolves Chromium-based browser executables, user data directories, and profile directories.
/// </summary>
/// <remarks>
/// This type translates the resolved browser-log configuration into local machine paths. Keep OS/browser probing here
/// so <see cref="BrowserConfiguration"/> stays focused on configuration precedence and effective option values.
/// </remarks>
internal static class ChromiumBrowserResolver
{
    /// <summary>
    /// Resolves a logical browser name or explicit executable path to a runnable browser executable.
    /// </summary>
    internal static string? TryResolveExecutable(string browser)
    {
        if (Path.IsPathRooted(browser) && File.Exists(browser))
        {
            return browser;
        }

        // Probe well-known install paths before PATH lookup so logical names still work for browser app bundles or
        // standard Windows installs that are not exposed on PATH.
        foreach (var candidate in GetBrowserCandidates(browser))
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            else if (PathLookupHelper.FindFullPathFromPath(candidate) is { } resolvedPath)
            {
                return resolvedPath;
            }
        }

        return PathLookupHelper.FindFullPathFromPath(browser);
    }

    /// <summary>
    /// Resolves a Chromium profile directory name from a directory name, profile display name, or shortcut name.
    /// </summary>
    internal static string ResolveProfileDirectory(string userDataDirectory, string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        if (!Directory.Exists(userDataDirectory))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsUserDataDirectoryNotFound, userDataDirectory));
        }

        if (TryResolveProfileDirectoryFromDirectoryEntries(userDataDirectory, profile) is { } directMatch)
        {
            return directMatch;
        }

        // Chromium stores display names in the user-data-root "Local State" file under profile.info_cache. Directory
        // names like "Default" or "Profile 1" are stable command-line values, while display names are user-facing.
        //
        // Relevant Local State shape:
        // {
        //   "profile": {
        //     "info_cache": {
        //       "Default": { "name": "Person 1", "shortcut_name": "Person 1" },
        //       "Profile 1": { "name": "Work", "shortcut_name": "Work" }
        //     }
        //   }
        // }
        var localStatePath = Path.Combine(userDataDirectory, "Local State");
        if (File.Exists(localStatePath))
        {
            try
            {
                using var localStateStream = File.OpenRead(localStatePath);
                using var localStateDocument = JsonDocument.Parse(localStateStream);
                if (TryResolveProfileDirectory(localStateDocument.RootElement, userDataDirectory, profile) is { } profileDirectory)
                {
                    return profileDirectory;
                }
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsUnableToReadProfileMetadata, localStatePath, profile),
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsUnableToReadProfileMetadata, localStatePath, profile),
                    ex);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsInvalidProfileMetadata, localStatePath, profile),
                    ex);
            }
        }

        throw new InvalidOperationException(
            string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsProfileNotFound, profile, userDataDirectory));
    }

    /// <summary>
    /// Resolves a profile directory from Chromium's parsed Local State metadata.
    /// </summary>
    internal static string? TryResolveProfileDirectory(JsonElement localStateRoot, string userDataDirectory, string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        if (!localStateRoot.TryGetProperty("profile", out var profileElement) ||
            !profileElement.TryGetProperty("info_cache", out var infoCacheElement) ||
            infoCacheElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? match = null;

        foreach (var profileEntry in infoCacheElement.EnumerateObject())
        {
            // Ignore stale metadata entries whose profile directories no longer exist.
            if (!Directory.Exists(Path.Combine(userDataDirectory, profileEntry.Name)) ||
                !MatchesBrowserProfile(profileEntry, profile))
            {
                continue;
            }

            // Profile display names are not unique. Force the caller to use the stable directory name when ambiguity
            // would otherwise select an arbitrary profile.
            if (match is not null && !string.Equals(match, profileEntry.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsAmbiguousProfile, profile, userDataDirectory));
            }

            match = profileEntry.Name;
        }

        return match;
    }

    /// <summary>
    /// Gets platform-specific candidate executables for logical browser names.
    /// </summary>
    private static IEnumerable<string> GetBrowserCandidates(string browser)
    {
        if (OperatingSystem.IsMacOS())
        {
            return browser.ToLowerInvariant() switch
            {
                "msedge" or "edge" =>
                [
                    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                    "msedge"
                ],
                "chrome" or "google-chrome" =>
                [
                    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                    "google-chrome",
                    "chrome"
                ],
                _ => [browser]
            };
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            return browser.ToLowerInvariant() switch
            {
                "msedge" or "edge" =>
                [
                    Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                    "msedge.exe"
                ],
                "chrome" or "google-chrome" =>
                [
                    Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                    "chrome.exe"
                ],
                _ => [browser]
            };
        }

        return browser.ToLowerInvariant() switch
        {
            "msedge" or "edge" => ["microsoft-edge", "microsoft-edge-stable", "msedge"],
            "chrome" or "google-chrome" => ["google-chrome", "google-chrome-stable", "chrome", "chromium-browser", "chromium"],
            _ => [browser]
        };
    }

    private static string? TryResolveProfileDirectoryFromDirectoryEntries(string userDataDirectory, string profile)
    {
        foreach (var directoryPath in Directory.EnumerateDirectories(userDataDirectory))
        {
            var directoryName = Path.GetFileName(directoryPath);
            // Treat configured profile directory names as user input and match case-insensitively, then return the
            // actual directory name so the Chromium command line preserves the filesystem casing.
            if (string.Equals(directoryName, profile, StringComparison.OrdinalIgnoreCase))
            {
                return directoryName;
            }
        }

        return null;
    }

    private static bool MatchesBrowserProfile(JsonProperty profileEntry, string profile)
    {
        return string.Equals(profileEntry.Name, profile, StringComparison.OrdinalIgnoreCase) ||
            MatchesBrowserProfileProperty(profileEntry.Value, "name", profile) ||
            MatchesBrowserProfileProperty(profileEntry.Value, "shortcut_name", profile);
    }

    private static bool MatchesBrowserProfileProperty(JsonElement profileElement, string propertyName, string profile)
    {
        return profileElement.TryGetProperty(propertyName, out var propertyElement) &&
            propertyElement.ValueKind == JsonValueKind.String &&
            string.Equals(propertyElement.GetString(), profile, StringComparison.OrdinalIgnoreCase);
    }
}
