// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.Browsers.Resources;
using System.Text.Json;

namespace Aspire.Hosting;

/// <summary>
/// Resolves Chromium-based browser executables, user data directories, and profile directories.
/// </summary>
/// <remarks>
/// This type translates the resolved browser automation configuration into local machine paths. Keep OS/browser probing here
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
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserLogsUserDataDirectoryNotFound, userDataDirectory));
        }

        if (TryResolveProfileDirectoryFromDirectoryEntries(userDataDirectory, profile) is { } directMatch)
        {
            return directMatch;
        }

        // Chromium stores profile metadata in the user-data-root "Local State" file under profile.info_cache. Directory
        // names like "Default" or "Profile 1" are stable command-line values, while "name" and "shortcut_name" are
        // user-facing labels that can be renamed in the browser UI.
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
                    string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserLogsUnableToReadProfileMetadata, localStatePath, profile),
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserLogsUnableToReadProfileMetadata, localStatePath, profile),
                    ex);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserLogsInvalidProfileMetadata, localStatePath, profile),
                    ex);
            }
        }

        throw new InvalidOperationException(
            string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserLogsProfileNotFound, profile, userDataDirectory));
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
                    string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserLogsAmbiguousProfile, profile, userDataDirectory));
            }

            match = profileEntry.Name;
        }

        return match;
    }

    internal static IReadOnlyList<ChromiumBrowserProfile> GetAvailableProfiles(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);

        if (!Directory.Exists(userDataDirectory))
        {
            return [];
        }

        var profiles = new Dictionary<string, ChromiumBrowserProfile>(StringComparer.OrdinalIgnoreCase);
        var localStatePath = Path.Combine(userDataDirectory, "Local State");
        if (File.Exists(localStatePath))
        {
            // Prefer Local State because it is the only place we can get the profile display names shown in the
            // configure dialog. The profile.info_cache object is keyed by profile directory name:
            //
            // <user-data-dir>\
            //   Local State
            //   Default\       <-- key "Default"; often displayed as "Profile 1" or a user-chosen name
            //   Profile 1\     <-- key "Profile 1"; display name might be "Work"
            //
            // The directory key is what Chromium accepts in --profile-directory. The display name is friendlier but
            // not unique, so the picker shows both when they differ and we persist the stable directory name.
            try
            {
                using var localStateStream = File.OpenRead(localStatePath);
                using var localStateDocument = JsonDocument.Parse(localStateStream);
                AddProfilesFromLocalState(localStateDocument.RootElement, userDataDirectory, profiles);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Fall back to profile directory names below. This can happen while Chromium is writing Local State.
            }
        }

        // Local State can be missing (new/empty user data directory), stale, or temporarily unreadable while Chromium
        // is writing it. Fall back to the profile directory names Chromium conventionally creates so the picker still
        // offers useful values when metadata is incomplete.
        foreach (var directoryPath in Directory.EnumerateDirectories(userDataDirectory))
        {
            var directoryName = Path.GetFileName(directoryPath);
            if (IsLikelyProfileDirectory(directoryName) && !profiles.ContainsKey(directoryName))
            {
                profiles[directoryName] = new ChromiumBrowserProfile(directoryName, DisplayName: null, ShortcutName: null);
            }
        }

        return [.. profiles.Values
            .OrderBy(static profile => string.Equals(profile.DirectoryName, "Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static profile => profile.DirectoryName, StringComparer.OrdinalIgnoreCase)];
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

    private static void AddProfilesFromLocalState(JsonElement localStateRoot, string userDataDirectory, Dictionary<string, ChromiumBrowserProfile> profiles)
    {
        if (!localStateRoot.TryGetProperty("profile", out var profileElement) ||
            !profileElement.TryGetProperty("info_cache", out var infoCacheElement) ||
            infoCacheElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var profileEntry in infoCacheElement.EnumerateObject())
        {
            // Chromium can leave profile.info_cache entries behind after a profile is deleted. Ignore entries whose
            // directories are missing so the dashboard doesn't offer stale profile choices.
            if (!Directory.Exists(Path.Combine(userDataDirectory, profileEntry.Name)))
            {
                continue;
            }

            profiles[profileEntry.Name] = new ChromiumBrowserProfile(
                profileEntry.Name,
                TryGetProfileProperty(profileEntry.Value, "name"),
                TryGetProfileProperty(profileEntry.Value, "shortcut_name"));
        }
    }

    private static bool IsLikelyProfileDirectory(string directoryName)
    {
        // Chromium's profile folders are convention-based. "Default" is the first profile, then additional profiles
        // are usually "Profile 1", "Profile 2", etc. Other folders in the user data directory (ShaderCache, Crashpad,
        // BrowserMetrics, component data, ...) are not launchable profiles and should not be shown as choices.
        return string.Equals(directoryName, "Default", StringComparison.OrdinalIgnoreCase) ||
            directoryName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
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

    private static string? TryGetProfileProperty(JsonElement profileElement, string propertyName)
    {
        return profileElement.TryGetProperty(propertyName, out var propertyElement) &&
            propertyElement.ValueKind == JsonValueKind.String
                ? propertyElement.GetString()
                : null;
    }
}

internal sealed record ChromiumBrowserProfile(string DirectoryName, string? DisplayName, string? ShortcutName);
