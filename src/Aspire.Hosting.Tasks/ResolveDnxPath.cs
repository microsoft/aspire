// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;

namespace Aspire.Hosting.Tasks;

/// <summary>
/// Resolves the DNX executable from the current PATH.
/// </summary>
public sealed class ResolveDnxPath : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// The resolved DNX executable path.
    /// </summary>
    [Output]
    public string? DnxPath { get; set; }

    public override bool Execute()
    {
        DnxPath = ResolveFromPath();
        return true;
    }

    private static string? ResolveFromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var seenPaths = new HashSet<string>(IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var pathEntry in path.Split(Path.PathSeparator))
        {
            var directory = pathEntry.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var executableName in GetDnxExecutableNames())
            {
                var candidate = Path.Combine(directory, executableName);
                if (seenPaths.Add(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string[] GetDnxExecutableNames()
    {
        return IsWindows()
            ? ["dnx.exe", "dnx.cmd", "dnx.bat", "dnx"]
            : ["dnx"];
    }

    private static bool IsWindows() => Path.DirectorySeparatorChar == '\\';
}
