// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Matches a single workspace glob pattern against a forward-slash candidate
// directory path, using the subset of glob syntax that Aspire's Dockerfile
// generator supports.
//
// Supported shapes (validated by WorkspacePatternValidator, replicated here for
// the matcher's benefit):
// - Literal: "apps/web" matches exactly "apps/web" (after trimming "./" and
//   trailing "/")
// - Trailing single-star: "packages/*" matches "packages/<name>" where <name>
//   does not start with '.' (dotted directories like ".git" are excluded by
//   convention to mirror minimatch / pnpm matcher defaults).
//
// Pattern-level validation should be performed by the caller before invoking
// this matcher; the matcher returns false for unsupported shapes rather than
// throwing.

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal static class WorkspacePatternMatcher
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="candidatePath"/>
    /// (a forward-slash relative path) matches <paramref name="pattern"/>.
    /// </summary>
    public static bool IsMatch(string pattern, string candidatePath)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(candidatePath);

        var normalizedPattern = Normalize(pattern);
        var normalizedCandidate = Normalize(candidatePath);
        if (normalizedPattern.Length == 0 || normalizedCandidate.Length == 0)
        {
            return false;
        }

        var lastSlash = normalizedPattern.LastIndexOf('/');
        var lastSegment = lastSlash < 0 ? normalizedPattern : normalizedPattern[(lastSlash + 1)..];
        if (lastSegment == "*")
        {
            var parent = lastSlash < 0 ? string.Empty : normalizedPattern[..lastSlash];
            var candidateLastSlash = normalizedCandidate.LastIndexOf('/');
            var candidateParent = candidateLastSlash < 0 ? string.Empty : normalizedCandidate[..candidateLastSlash];
            var candidateName = candidateLastSlash < 0 ? normalizedCandidate : normalizedCandidate[(candidateLastSlash + 1)..];
            if (candidateName.StartsWith('.'))
            {
                return false;
            }

            return string.Equals(candidateParent, parent, StringComparison.Ordinal);
        }

        if (normalizedPattern.Contains('*', StringComparison.Ordinal))
        {
            // Validator should have rejected this; treat as no-match.
            return false;
        }

        return string.Equals(normalizedPattern, normalizedCandidate, StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.EndsWith('/'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }
}
