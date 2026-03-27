// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

internal static class LanguageDetectionPatternMatcher
{
    public static bool DirectoryContainsMatch(DirectoryInfo directory, string pattern)
    {
        if (!directory.Exists)
        {
            return false;
        }

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return directory.EnumerateFiles().Any(file => MatchesPattern(file.Name, pattern));
        }

        return File.Exists(Path.Combine(directory.FullName, pattern));
    }

    public static bool MatchesPattern(string fileName, string pattern)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var extension = pattern[1..];
            return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }

        return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
