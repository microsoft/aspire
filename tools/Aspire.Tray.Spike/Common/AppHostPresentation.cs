// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Tray.Spike;

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
        return string.IsNullOrEmpty(name) ? "AppHost" : name;
    }

    public static string GetTitle(AppHostInfo host) => Compact(GetDisplayName(host));

    public static string GetSubtitle(AppHostInfo host) => $"{Compact(GetLocation(host))} \u00b7 PID {host.AppHostPid}";

    public static string GetLabel(AppHostInfo host)
        => $"{Path.GetFileNameWithoutExtension(host.AppHostPath)} - {GetLocation(host)} (PID {host.AppHostPid})";

    public static string GetDirectory(AppHostInfo host) => Path.GetDirectoryName(host.AppHostPath) ?? host.AppHostPath;

    private static string GetLocation(AppHostInfo host)
    {
        var directory = GetDirectory(host);
        var parent = Path.GetDirectoryName(directory);
        return parent is null ? directory : Path.Combine(Path.GetFileName(parent), Path.GetFileName(directory));
    }

    private static string Compact(string value)
    {
        const int MaximumTextElements = 44;
        value = value.ReplaceLineEndings(" ").Replace('\t', ' ');
        var elements = StringInfo.ParseCombiningCharacters(value);
        if (elements.Length <= MaximumTextElements)
        {
            return value;
        }

        // Keep both ends without cutting a surrogate pair or a combining sequence.
        return value[..elements[21]] + "\u2026" + value[elements[^22]..];
    }
}
