// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Hosting;

internal static class SafariBrowserResolver
{
    internal const string StableSafariDriverPath = "/usr/bin/safaridriver";
    internal const string TechnologyPreviewSafariDriverPath = "/Applications/Safari Technology Preview.app/Contents/MacOS/safaridriver";

    public static bool IsSafariBrowser(string browser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browser);

        if (Path.IsPathRooted(browser))
        {
            return string.Equals(Path.GetFileName(browser), "safaridriver", StringComparison.OrdinalIgnoreCase);
        }

        return browser.ToLowerInvariant() switch
        {
            "safari" or
            "safaridriver" or
            "safari-technology-preview" or
            "safari technology preview" or
            "safari-tp" or
            "safaritp" => true,
            _ => false
        };
    }

    public static SafariBrowserDriver? TryResolveDriver(string browser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browser);

        if (Path.IsPathRooted(browser))
        {
            return File.Exists(browser)
                ? new SafariBrowserDriver(browser, GetDisplayName(browser))
                : null;
        }

        foreach (var candidate in GetDriverCandidates(browser))
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                {
                    return new SafariBrowserDriver(candidate, GetDisplayName(candidate));
                }
            }
            else if (PathLookupHelper.FindFullPathFromPath(candidate) is { } resolvedPath)
            {
                return new SafariBrowserDriver(resolvedPath, GetDisplayName(resolvedPath));
            }
        }

        return null;
    }

    public static string GetUnableToLocateDriverMessage(string browser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browser);
        return string.Format(
            CultureInfo.CurrentCulture,
            Browsers.Resources.BrowserMessageStrings.BrowserLogsUnableToLocateSafariDriver,
            browser,
            StableSafariDriverPath,
            TechnologyPreviewSafariDriverPath);
    }

    private static IEnumerable<string> GetDriverCandidates(string browser)
    {
        return browser.ToLowerInvariant() switch
        {
            "safari" or "safaridriver" => [StableSafariDriverPath, "safaridriver"],
            "safari-technology-preview" or "safari technology preview" or "safari-tp" or "safaritp" => [TechnologyPreviewSafariDriverPath],
            _ => [browser]
        };
    }

    private static string GetDisplayName(string driverPath) =>
        string.Equals(Path.GetFullPath(driverPath), Path.GetFullPath(TechnologyPreviewSafariDriverPath), GetPathComparison())
            ? "Safari Technology Preview"
            : "Safari";

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

internal sealed record SafariBrowserDriver(string DriverPath, string DisplayName);
