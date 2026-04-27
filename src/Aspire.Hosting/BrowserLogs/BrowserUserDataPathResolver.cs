// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.Resources;

namespace Aspire.Hosting;

// Resolves the local filesystem location used as a Chromium --user-data-dir for a tracked browser session.
//
// The resolver intentionally never points at the user's real browser profile root. Pointing Aspire at the real
// profile is fragile in practice: Chromium's per-user-data-dir singleton means a second launch silently hands
// off to the already-running browser, App-Bound Encryption (Chrome/Edge 127+) ties cookies to the launching
// process identity, and a normal user browser holds locks that prevent us from ever opening a debug-enabled
// instance. Both Shared and Isolated therefore live under an Aspire-managed root that can always be opened with
// remote debugging enabled.
//
// Layout (Windows shown; macOS/Linux mirror the same shape under the OS-appropriate data root):
//   %LocalAppData%\Aspire\BrowserData\shared\<browser>                              (Shared)
//   %LocalAppData%\Aspire\BrowserData\isolated\<AppHost:PathSha256>\<browser>       (Isolated)
//
// Both paths are persistent. AppHost shutdown does not delete them and does not close the browser. The next
// AppHost run reads the adoption sidecar and connects to the existing browser via CDP.
internal static class BrowserUserDataPathResolver
{
    // SHA-256 prefix length used for the per-AppHost segment. AppHost:PathSha256 is the full hex-encoded
    // SHA-256 of the AppHost project path; the full hash is unnecessary for a filesystem segment and a
    // shorter prefix keeps the resulting paths well under Windows' MAX_PATH for nested Chromium profile
    // sub-directories.
    private const int AppHostShaSegmentLength = 16;

    public static string Resolve(BrowserConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Browser);

        var root = GetAspireBrowserDataRoot();
        var browserSegment = NormalizeBrowserSegment(configuration.Browser);

        var path = configuration.UserDataMode switch
        {
            BrowserUserDataMode.Shared => Path.Combine(root, "shared", browserSegment),
            BrowserUserDataMode.Isolated => Path.Combine(root, "isolated", GetAppHostSegment(configuration), browserSegment),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration), configuration.UserDataMode, null)
        };

        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetAspireBrowserDataRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aspire",
                "BrowserData");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Aspire",
                "BrowserData");
        }

        // XDG: prefer XDG_DATA_HOME, fall back to ~/.local/share. Lower-case segment names match the
        // conventional XDG layout (e.g. ~/.config/google-chrome).
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrEmpty(xdgDataHome)
            ? xdgDataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "aspire", "browser-data");
    }

    private static string GetAppHostSegment(BrowserConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.AppHostKey))
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    MessageStrings.BrowserLogsAppHostPathShaNotAvailable,
                    BrowserUserDataMode.Isolated));
        }

        return configuration.AppHostKey.Length > AppHostShaSegmentLength
            ? configuration.AppHostKey[..AppHostShaSegmentLength]
            : configuration.AppHostKey;
    }

    // Maps a logical browser name or executable path to a stable lower-case folder segment so a Chrome -> Edge
    // configuration flip never silently shares state with the previous browser. Unknown executables fall back to
    // a sanitized form of the file name without extension.
    private static string NormalizeBrowserSegment(string browser)
    {
        var name = Path.IsPathRooted(browser) || Path.IsPathFullyQualified(browser)
            ? Path.GetFileNameWithoutExtension(browser)
            : browser;

        var lower = name.ToLowerInvariant();
        return lower switch
        {
            "msedge" or "edge" or "microsoft-edge" or "microsoft-edge-stable" or "microsoft edge" => "msedge",
            "chrome" or "google-chrome" or "google-chrome-stable" or "google chrome" => "chrome",
            "chromium" or "chromium-browser" => "chromium",
            _ => Sanitize(lower)
        };
    }

    private static string Sanitize(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            buffer[i] = Array.IndexOf(invalidChars, c) >= 0 || c == ' ' ? '_' : c;
        }

        var result = new string(buffer);
        return string.IsNullOrEmpty(result) ? "browser" : result;
    }
}
