// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Tray;

internal static class AppHostPresentation
{
    public static string GetDisplayName(AppHostInfo host)
    {
        var name = Path.GetFileNameWithoutExtension(host.AppHostPath);
        var directory = GetDirectory(host);
        // File-based hosts often use apphost.cs/apphost.mts inside an AppHost directory.
        // Prefer the project/worktree name over repeating "apphost".
        while (name.Equals("apphost", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileName(directory);
            directory = Path.GetDirectoryName(directory) ?? "";
        }
        const string Suffix = ".AppHost";
        if (name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^Suffix.Length];
        }
        return string.IsNullOrEmpty(name) ? "AppHost" : ToSingleLine(name);
    }

    public static string GetTitle(AppHostInfo host) => Compact(GetDisplayName(host), 44);

    public static string GetPathLabel(string appHostPath) => Compact(appHostPath, 44);

    public static string GetMenuDetailsText(string appHostPath, string subtitle)
        => ToSingleLine($"{appHostPath} \u00b7 {subtitle}");

    public static string GetMenuDetailsLabel(string details) => Compact(details, 45);

    public static string GetCompactMenuLabel(AppHostMenuItem host) => Compact(host.DisplayName, 44);

    public static string GetSubtitle(AppHostInfo host) => $"{Compact(GetLocation(host), 44)} \u00b7 PID {host.AppHostPid}";

    public static string GetLabel(AppHostInfo host)
        => ToSingleLine($"{Path.GetFileNameWithoutExtension(host.AppHostPath)} - {GetLocation(host)} (PID {host.AppHostPid})");

    public static string GetDirectory(AppHostInfo host) => Path.GetDirectoryName(host.AppHostPath) ?? host.AppHostPath;

    private static string GetLocation(AppHostInfo host)
    {
        var directory = GetDirectory(host);
        var parent = Path.GetDirectoryName(directory);
        return parent is null ? directory : Path.Combine(Path.GetFileName(parent), Path.GetFileName(directory));
    }

    private static string Compact(string value, int maximumTextElements)
    {
        value = ToSingleLine(value);
        var elements = StringInfo.ParseCombiningCharacters(value);
        if (elements.Length <= maximumTextElements)
        {
            return value;
        }

        // Keep both ends without cutting a surrogate pair or a combining sequence.
        var tailLength = maximumTextElements / 2;
        var headLength = maximumTextElements - tailLength - 1;
        return value[..elements[headLength]] + "\u2026" + value[elements[^tailLength]..];
    }

    private static string ToSingleLine(string value) => value.ReplaceLineEndings(" ").Replace('\t', ' ');
}
