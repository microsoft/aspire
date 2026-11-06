// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Walks the workspace root filesystem and resolves a list of workspace glob
// patterns to actual member directories. Each candidate is checked for
// presence of a package.json (the marker for a JS package directory).
//
// Example:
//   root/
//     package.json
//     apps/
//       web/package.json
//       group/api/package.json
//     packages/
//       db/package.json
//     docs/README.md
//
//   patterns: ["apps/**", "packages/*", "!**/test/**"]
//   result:   ["apps/group/api", "apps/web", "packages/db"]
//
// This is the only filesystem-touching component of the workspace reader
// pipeline — the JSON/YAML parsers are pure.
//
// Glob semantics match what real workspace tooling supports: pnpm
// (https://pnpm.io/workspaces) and npm/yarn/bun (which resolve "workspaces"
// with minimatch) both accept the full vocabulary — recursive "**", a "*" that
// matches a single path segment, a "*" used inside a segment (e.g. "apps/*-svc"),
// literal paths, and "!"-prefixed negations that exclude matches. We delegate the
// matching to Microsoft.Extensions.FileSystemGlobbing's Matcher, which implements
// the same glob dialect, rather than reimplementing it.
//
// node_modules is always pruned: installed dependencies carry package.json files,
// and walking them on a real repo would also be pathologically slow.
// Dot-directories require an explicitly dot-prefixed pattern segment; ordinary
// wildcards must not discover hidden tooling state such as ".git" or ".cache".

using Microsoft.Extensions.FileSystemGlobbing;

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal static class WorkspacePatternExpander
{
    // The marker file that identifies a directory as a JS package, and the suffix we
    // append to each directory pattern so the glob matcher selects the package's manifest
    // (e.g. directory pattern "apps/**" becomes the file glob "apps/**/package.json").
    private const string PackageJsonFileName = "package.json";
    private const string PackageJsonSuffix = "/" + PackageJsonFileName;

    /// <summary>
    /// Expands workspace glob patterns to forward-slash relative paths under
    /// <paramref name="rootPath"/>. Each result has a package.json.
    /// </summary>
    /// <returns>The resolved set of workspace directories, sorted ordinally.</returns>
    public static IReadOnlyList<string> Expand(string rootPath, IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(patterns);

        // Ordinal matching keeps results deterministic across platforms; workspace patterns
        // and directory names are conventionally lowercase, so case folding would only mask
        // mistakes rather than help.
        var matcher = new Matcher(StringComparison.Ordinal);
        var includePatterns = new List<string>();
        foreach (var rawPattern in patterns)
        {
            var pattern = rawPattern?.Trim() ?? string.Empty;
            if (pattern.Length == 0)
            {
                continue;
            }

            var negate = pattern[0] == '!';
            if (negate)
            {
                pattern = pattern[1..];
            }

            var normalized = NormalizeDirectoryPattern(pattern);
            if (normalized.Length == 0)
            {
                continue;
            }

            // Match against the package manifest rather than the directory itself: a directory
            // only counts as a member when it actually contains a package.json.
            var fileGlob = normalized + PackageJsonSuffix;
            if (negate)
            {
                matcher.AddExclude(fileGlob);
            }
            else
            {
                matcher.AddInclude(fileGlob);
                includePatterns.Add(normalized);
            }
        }

        // A declaration consisting solely of negations (or nothing) selects no members, the
        // same as pnpm/npm would resolve it.
        if (includePatterns.Count == 0)
        {
            return [];
        }

        // Traverse each include independently so allowing "packages/.cache" does not let
        // a separate "**" include discover packages beneath that hidden directory.
        // The final matcher still applies all exclusions to the combined candidates.
        var candidateManifests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in includePatterns)
        {
            var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != ".").ToArray();
            var segmentMatchers = segments.Select(segment =>
                new Matcher(StringComparison.Ordinal).AddInclude(segment + PackageJsonSuffix)).ToArray();
            CollectPackageManifests(new DirectoryInfo(rootPath), string.Empty, segments, segmentMatchers,
                patternIndex: 0, candidateManifests, []);
        }

        var results = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var match in matcher.Match(candidateManifests).Files)
        {
            // match.Path is the matched relative manifest path with forward slashes,
            // e.g. "apps/web/package.json"; the member directory is everything before the suffix.
            if (match.Path.EndsWith(PackageJsonSuffix, StringComparison.Ordinal))
            {
                results.Add(match.Path[..^PackageJsonSuffix.Length]);
            }
        }

        return [.. results];
    }

    private static string NormalizeDirectoryPattern(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
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

    private static void CollectPackageManifests(DirectoryInfo directory, string relativePrefix,
        string[] segments, Matcher[] segmentMatchers, int patternIndex, HashSet<string> manifests,
        HashSet<(string RelativePath, int PatternIndex)> visited)
    {
        if (!directory.Exists || !visited.Add((relativePrefix, patternIndex)))
        {
            return;
        }

        if (patternIndex == segments.Length)
        {
            if (relativePrefix.Length > 0 && File.Exists(Path.Combine(directory.FullName, PackageJsonFileName)))
            {
                manifests.Add(relativePrefix + PackageJsonSuffix);
            }

            return;
        }

        var segment = segments[patternIndex];
        var recursive = segment == "**";
        if (recursive)
        {
            // "**/.workspace/*" may match zero visible ancestors before the explicit
            // hidden segment. Recursive wildcards themselves never consume hidden names.
            CollectPackageManifests(directory, relativePrefix, segments, segmentMatchers,
                patternIndex + 1, manifests, visited);
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            var name = child.Name;

            // Explicit includes cannot opt into installed dependencies. Hidden directories
            // are only reachable through a dot-prefixed segment in this particular include.
            if (name.Length == 0 || string.Equals(name, "node_modules", StringComparison.Ordinal)
                || (name[0] == '.' && !segment.StartsWith('.')))
            {
                continue;
            }

            if (!recursive && !segmentMatchers[patternIndex].Match(name + PackageJsonSuffix).HasMatches)
            {
                continue;
            }

            var relative = relativePrefix.Length == 0 ? name : $"{relativePrefix}/{name}";
            CollectPackageManifests(child, relative, segments, segmentMatchers,
                recursive ? patternIndex : patternIndex + 1, manifests, visited);
        }
    }
}
