// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// Helpers for parsing and comparing npm-style version ranges.
/// </summary>
internal static class NpmVersionHelper
{
    /// <summary>
    /// Determines whether the existing dependency version should be upgraded to the desired version.
    /// Returns <c>true</c> when both versions are parseable and the desired version is newer.
    /// </summary>
    internal static bool ShouldUpgrade(string existingVersion, string desiredVersion)
    {
        return TryParseNpmVersion(existingVersion, out var existingSemVersion)
            && TryParseNpmVersion(desiredVersion, out var desiredSemVersion)
            && SemVersion.ComparePrecedence(existingSemVersion, desiredSemVersion) < 0;
    }

    /// <summary>
    /// Attempts to extract a comparable <see cref="SemVersion"/> from an npm version range string.
    /// Strips range operators (<c>^</c>, <c>~</c>, <c>&gt;=</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&lt;</c>, <c>=</c>)
    /// and parses the remaining version. Returns <c>false</c> for union ranges (<c>||</c>),
    /// workspace references, file paths, and symlinks.
    /// </summary>
    internal static bool TryParseNpmVersion(string version, out SemVersion semVersion)
    {
        var normalizedVersion = version.Trim();
        if (normalizedVersion.Contains("||", StringComparison.Ordinal) ||
            normalizedVersion.StartsWith("workspace:", StringComparison.OrdinalIgnoreCase) ||
            normalizedVersion.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            normalizedVersion.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
        {
            semVersion = default!;
            return false;
        }

        while (normalizedVersion.Length > 0)
        {
            if (normalizedVersion.StartsWith(">=", StringComparison.Ordinal) ||
                normalizedVersion.StartsWith("<=", StringComparison.Ordinal))
            {
                normalizedVersion = normalizedVersion[2..].TrimStart();
                continue;
            }

            if (normalizedVersion[0] is '^' or '~' or '>' or '<' or '=')
            {
                normalizedVersion = normalizedVersion[1..].TrimStart();
                continue;
            }

            break;
        }

        if (SemVersion.TryParse(normalizedVersion, SemVersionStyles.Strict, out var strictVersion) &&
            strictVersion is not null)
        {
            semVersion = strictVersion;
            return true;
        }

        if (SemVersion.TryParse(normalizedVersion, SemVersionStyles.Any, out var anyVersion) &&
            anyVersion is not null)
        {
            semVersion = anyVersion;
            return true;
        }

        semVersion = default!;
        return false;
    }
}
