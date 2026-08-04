// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TypeScriptApiCompat;

internal static class ExcludedPackageLoader
{
    public static IReadOnlySet<string> Load(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Excluded packages file '{filePath}' does not exist.", filePath);
        }

        var packages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            packages.Add(line);
        }

        return packages;
    }
}
