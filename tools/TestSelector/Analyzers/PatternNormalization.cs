// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TestSelector.Analyzers;

/// <summary>
/// Shared helpers for normalizing user-facing glob patterns before they're
/// fed to <see cref="Microsoft.Extensions.FileSystemGlobbing.Matcher"/> or the
/// regex-based source-pattern compiler.
/// </summary>
internal static class PatternNormalization
{
    /// <summary>
    /// Bare-filename rule: a pattern that contains no path separator (e.g.
    /// <c>Directory.Build.props</c>, <c>global.json</c>, <c>*.sln</c>,
    /// <c>.editorconfig</c>) should match at any path depth — not just at
    /// the repository root. The raw <c>FileSystemGlobbing.Matcher</c> only
    /// matches such patterns at the root, which silently misses nested
    /// occurrences like <c>src/Directory.Build.props</c> or
    /// <c>tests/Directory.Build.props</c>.
    /// </summary>
    /// <remarks>
    /// User intent in the Aspire rules file is that bare filenames are
    /// shorthand for "any file with this name anywhere in the tree". The
    /// audit-replay evaluator (<c>eval_rules.py</c>) documents the same
    /// rule. Applying the prefix here makes the C# evaluator match Python
    /// audit-replay and the documented user intent.
    /// </remarks>
    public static string NormalizeGlob(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return pattern;
        }

        // Already rooted, or already has a separator → use as-is.
        if (pattern.Contains('/') || pattern.Contains('\\'))
        {
            return pattern;
        }

        return "**/" + pattern;
    }
}
