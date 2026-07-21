// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tasks;

internal static class CommandPathResolver
{
    public static string? ResolveFromPath(string command, string? path = null)
    {
        return EnumerateFromPath(command, path).FirstOrDefault();
    }

    public static IEnumerable<string> EnumerateFromPath(string command, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        var executableNames = GetExecutableNames(command);
        var seenPaths = new HashSet<string>(IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // PATH entries can be quoted, for example:
        //   C:\Tools;"C:\Program Files\dotnet";C:\Users\user\.dotnet\tools
        foreach (var pathEntry in path.Split(Path.PathSeparator))
        {
            var directory = pathEntry.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var executableName in executableNames)
            {
                var candidate = Path.Combine(directory, executableName);
                if (seenPaths.Add(candidate) && File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static string[] GetExecutableNames(string command)
    {
        return IsWindows()
            ? [$"{command}.exe", $"{command}.cmd", $"{command}.bat", command]
            : [command];
    }

    private static bool IsWindows() => Path.DirectorySeparatorChar == '\\';
}
