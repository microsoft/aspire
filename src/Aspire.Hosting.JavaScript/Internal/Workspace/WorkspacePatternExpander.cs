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
// Two directory kinds are always pruned during the walk, before patterns are
// applied, because no package manager ever treats them as members:
//   - node_modules: installed dependencies carry package.json files; walking
//     them on a real repo would also be pathologically slow.
//   - dot-directories (".git", ".turbo", ".cache", ...): hidden tooling state.

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
        var hasInclude = false;
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
                hasInclude = true;
            }
        }

        // A declaration consisting solely of negations (or nothing) selects no members, the
        // same as pnpm/npm would resolve it.
        if (!hasInclude)
        {
            return [];
        }

        // Collect every candidate package directory once (node_modules and dot-dirs pruned),
        // then let the matcher filter that in-memory list. Matcher.Match operates purely on the
        // supplied relative paths, so no second filesystem walk happens here.
        var candidateManifests = new List<string>();
        CollectPackageManifests(new DirectoryInfo(rootPath), relativePrefix: string.Empty, candidateManifests);

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

    private static void CollectPackageManifests(DirectoryInfo directory, string relativePrefix, List<string> manifests)
    {
        if (!directory.Exists)
        {
            return;
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            var name = child.Name;

            // Prune node_modules and dot-directories before recursing: they never contain
            // workspace members and node_modules in particular can be enormous.
            if (name.Length == 0 || name[0] == '.' || string.Equals(name, "node_modules", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = relativePrefix.Length == 0 ? name : $"{relativePrefix}/{name}";

            if (File.Exists(Path.Combine(child.FullName, PackageJsonFileName)))
            {
                manifests.Add(relative + PackageJsonSuffix);
            }

            // Recurse even into directories that are themselves members: pnpm "packages/**"
            // can match packages nested below another package's directory.
            CollectPackageManifests(child, relative, manifests);
        }
    }
}
