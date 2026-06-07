// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Validates workspace glob patterns declared in package.json/pnpm-workspace.yaml
// against the subset Aspire's Dockerfile generator supports.
//
// Supported pattern shapes (all forward-slash):
// - Literal path segment: "apps/web", "packages/utils"
// - Trailing star matching one segment: "packages/\*"
//
// Unsupported pattern shapes (we throw rather than silently skip):
// - Negated: "!apps/legacy"
// - Recursive: "packages/\*\*"
// - Non-trailing star: "apps/\*-svc", "apps/api-\*", "\*/api"
//
// Throwing eagerly (rather than silently dropping the pattern as the underlying
// directory walker would) gives the user a clear error that points at the
// declaration site, instead of a confusing "is not a declared workspace member"
// error far downstream.
//
// Real-world workspace tooling resolves these patterns with minimatch (npm/yarn
// classic/bun) or pnpm's own matcher, both of which support the full glob
// vocabulary. We intentionally support only the dominant subset and report the
// rest as unsupported.
namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal static class WorkspacePatternValidator
{
    ///
    /// Validates that every pattern uses one of the supported shapes.
    ///
    /// <param name="patterns">The list of workspace patterns to validate.</param>
    /// <param name="rootPath">The root path where the patterns are declared.</param>
    /// <exception cref="DistributedApplicationException">Thrown when a pattern is negated, recursive, or uses a non-trailing star.</exception>
    public static void Validate(IEnumerable<string> patterns, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(rootPath);
        foreach (var pattern in patterns)
        {
            ValidateOne(pattern, rootPath);
        }
    }
    private static void ValidateOne(string pattern, string rootPath)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return;
        }
        if (pattern[0] == '!')
        {
            throw new DistributedApplicationException(
            $"Negated workspace pattern '{pattern}' at '{rootPath}' is not supported.");
        }
        if (pattern.Contains("**", StringComparison.Ordinal))
        {
            throw new DistributedApplicationException(
            $"Recursive workspace pattern '{pattern}' at '{rootPath}' is not supported.");
        }
        // The only star we accept is a segment that is exactly "*" and is the
        // last segment of the pattern (e.g. "packages/*"). Anything else
        // (e.g. "apps/*-svc", "apps/api-*", "*/api") is rejected.
        var normalized = pattern.Replace('\\', '/');
        var segments = normalized.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (!segment.Contains('*', StringComparison.Ordinal))
            {
                continue;
            }
            if (segment == "*" && i == segments.Length - 1)
            {
                continue;
            }
            throw new DistributedApplicationException(
            $"Workspace pattern '{pattern}' at '{rootPath}' uses an unsupported glob shape. "
            + "Supported shapes are literal paths (for example, 'apps/web') or a single "
            + "trailing star (for example, 'packages/*').");
        }
    }
}
