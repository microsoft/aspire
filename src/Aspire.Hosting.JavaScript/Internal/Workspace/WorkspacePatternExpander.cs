// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Walks the workspace root filesystem and resolves a list of workspace glob
// patterns to actual member directories. Each candidate is checked for
// presence of a package.json (the marker for a JS package directory).
//
// Example:
// root/
// package.json
// packages/
// web/package.json
// api/package.json
// docs/README.md
//
// patterns: ["packages/\*"]
// result: ["packages/api", "packages/web"]
//
// This is the only filesystem-touching component of the workspace reader
// pipeline — the JSON/YAML parsers, validator, and matcher are all pure.
//
// Unsupported pattern shapes (negated, recursive, non-trailing star) should be
// rejected upstream by WorkspacePatternValidator. This expander is permissive
// (silently skips unsupported shapes) so that the validator owns the error
// surface.
namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal static class WorkspacePatternExpander
{
    ///
    /// Expands workspace glob patterns to forward-slash relative paths under
    /// . Each result has a package.json.
    ///
    /// The resolved set of workspace directories, sorted ordinally.
    public static IReadOnlyList<string> Expand(string rootPath, IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(patterns);
        var results = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rawPattern in patterns)
        {
            var pattern = rawPattern?.Trim() ?? string.Empty;
            if (pattern.Length == 0 || pattern[0] == '!')
            {
                continue;
            }
            var normalized = pattern.Replace('\\', '/');
            if (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }
            if (normalized.EndsWith('/'))
            {
                normalized = normalized[..^1];
            }
            if (normalized.Length == 0)
            {
                continue;
            }
            var lastSlash = normalized.LastIndexOf('/');
            var lastSegment = lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
            if (lastSegment == "*")
            {
                ExpandTrailingStar(rootPath, normalized, lastSlash, results);
            }
            else if (!normalized.Contains('*', StringComparison.Ordinal))
            {
                AddIfPackage(rootPath, normalized, results);
            }
            // Other shapes are rejected upstream by WorkspacePatternValidator.
        }
        return [.. results];
    }
    private static void ExpandTrailingStar(string rootPath, string normalized, int lastSlash, SortedSet<string> results)
    {
        var parentRel = lastSlash < 0 ? string.Empty : normalized[..lastSlash];
        var parentAbs = parentRel.Length == 0
        ? rootPath
        : Path.Combine(rootPath, parentRel.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(parentAbs))
        {
            return;
        }
        foreach (var childDir in Directory.EnumerateDirectories(parentAbs))
        {
            var childName = Path.GetFileName(childDir);
            if (childName.StartsWith('.'))
            {
                continue;
            }
            if (!File.Exists(Path.Combine(childDir, "package.json")))
            {
                continue;
            }
            var rel = parentRel.Length == 0 ? childName : $"{parentRel}/{childName}";
            results.Add(rel);
        }
    }
    private static void AddIfPackage(string rootPath, string normalized, SortedSet<string> results)
    {
        var abs = Path.Combine(rootPath, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(Path.Combine(abs, "package.json")))
        {
            results.Add(normalized);
        }
    }
}
