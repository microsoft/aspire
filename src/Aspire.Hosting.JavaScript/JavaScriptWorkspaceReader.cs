// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Helpers for reading JavaScript workspace metadata from a workspace root directory.
/// </summary>
internal static class JavaScriptWorkspaceReader
{
    /// <summary>
    /// Reads the <c>name</c> field from <c>&lt;directory&gt;/package.json</c>.
    /// </summary>
    /// <returns>The package name, or <see langword="null"/> if the file does not exist or has no <c>name</c> field.</returns>
    public static string? TryReadPackageName(string directory)
    {
        var packageJsonPath = Path.Combine(directory, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String)
            {
                var name = nameProp.GetString();
                return string.IsNullOrEmpty(name) ? null : name;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        return null;
    }

    /// <summary>
    /// Reads workspace member glob patterns declared at the given root.
    /// </summary>
    /// <remarks>
    /// Reads <c>package.json#workspaces</c> (string array form, or <c>{ "packages": [...] }</c> form) and
    /// <c>pnpm-workspace.yaml#packages</c>. Returns the union of patterns found.
    /// </remarks>
    /// <returns>The list of glob patterns, in declaration order. Negated patterns are returned for caller validation.</returns>
    public static IReadOnlyList<string> ReadWorkspacePatterns(string rootPath)
    {
        var patterns = new List<string>();

        var packageJsonPath = Path.Combine(rootPath, "package.json");
        if (File.Exists(packageJsonPath))
        {
            try
            {
                using var stream = File.OpenRead(packageJsonPath);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("workspaces", out var ws))
                {
                    if (ws.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in ws.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                            {
                                patterns.Add(s);
                            }
                        }
                    }
                    else if (ws.ValueKind == JsonValueKind.Object &&
                             ws.TryGetProperty("packages", out var wsPackages) &&
                             wsPackages.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in wsPackages.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                            {
                                patterns.Add(s);
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
        }

        var pnpmYamlPath = Path.Combine(rootPath, "pnpm-workspace.yaml");
        if (File.Exists(pnpmYamlPath))
        {
            try
            {
                foreach (var pattern in ParsePnpmWorkspacePackages(File.ReadAllLines(pnpmYamlPath)))
                {
                    patterns.Add(pattern);
                }
            }
            catch (IOException)
            {
            }
        }

        return patterns;
    }

    /// <summary>
    /// Expands workspace glob patterns to actual member directories under <paramref name="rootPath"/>.
    /// </summary>
    /// <remarks>
    /// Supports the common subset: literal path segments and a single trailing <c>*</c> (e.g. <c>packages/*</c>).
    /// Recursive globs (<c>**</c>) and negated patterns are not supported and should be rejected by the caller.
    /// </remarks>
    /// <returns>Forward-slash relative paths to discovered workspace directories. Order is stable (sorted).</returns>
    public static IReadOnlyList<string> ExpandWorkspacePatterns(string rootPath, IEnumerable<string> patterns)
    {
        var results = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var raw in patterns)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0)
            {
                continue;
            }

            // Skip negated patterns — caller validates and throws.
            if (pattern[0] == '!')
            {
                continue;
            }

            // Normalize separators
            var normalized = pattern.Replace('\\', '/');

            // Trim leading "./"
            if (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            // Trim trailing slash
            if (normalized.EndsWith('/'))
            {
                normalized = normalized[..^1];
            }

            if (normalized.Length == 0)
            {
                continue;
            }

            var lastSegment = normalized[(normalized.LastIndexOf('/') + 1)..];

            if (normalized.Contains("**", StringComparison.Ordinal))
            {
                // Recursive glob — not supported.
                continue;
            }

            if (lastSegment == "*")
            {
                // packages/* — enumerate immediate child dirs.
                var parentRel = normalized.Length > 1
                    ? normalized[..^2] // strip "/*"
                    : string.Empty;
                var parentAbs = string.IsNullOrEmpty(parentRel)
                    ? rootPath
                    : Path.Combine(rootPath, parentRel.Replace('/', Path.DirectorySeparatorChar));

                if (!Directory.Exists(parentAbs))
                {
                    continue;
                }

                foreach (var childDir in Directory.EnumerateDirectories(parentAbs))
                {
                    var childName = Path.GetFileName(childDir);
                    if (childName.StartsWith('.'))
                    {
                        // Skip dotted dirs (.git, .turbo, etc.)
                        continue;
                    }

                    if (!File.Exists(Path.Combine(childDir, "package.json")))
                    {
                        continue;
                    }

                    var rel = string.IsNullOrEmpty(parentRel) ? childName : $"{parentRel}/{childName}";
                    results.Add(rel);
                }
            }
            else if (!normalized.Contains('*', StringComparison.Ordinal))
            {
                // Literal path. Include if package.json exists.
                var abs = Path.Combine(rootPath, normalized.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(Path.Combine(abs, "package.json")))
                {
                    results.Add(normalized);
                }
            }
            // Other glob shapes (e.g. "packages/*-foo") are rare; fall through silently in v1.
        }

        return [.. results];
    }

    /// <summary>
    /// Parses the <c>packages:</c> sequence from a <c>pnpm-workspace.yaml</c> file.
    /// </summary>
    /// <remarks>
    /// Hand-rolled parser for the limited subset used by pnpm: a top-level <c>packages</c> key followed by
    /// a block sequence of strings. Other YAML constructs are ignored.
    /// </remarks>
    internal static IEnumerable<string> ParsePnpmWorkspacePackages(IReadOnlyList<string> lines)
    {
        var inPackages = false;
        var sequenceIndent = -1;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Replace("\t", "  ");

            // Strip comments (simple — does not honor quoted '#').
            var hashIdx = line.IndexOf('#');
            if (hashIdx >= 0)
            {
                line = line[..hashIdx];
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }
            var trimmed = line[indent..];

            if (!inPackages)
            {
                if (indent == 0 && trimmed.StartsWith("packages:", StringComparison.Ordinal))
                {
                    inPackages = true;
                }
                continue;
            }

            // We are in the packages: section.
            if (indent == 0)
            {
                // New top-level key; we're done.
                yield break;
            }

            if (sequenceIndent < 0)
            {
                sequenceIndent = indent;
            }
            else if (indent < sequenceIndent)
            {
                // Dedented out of the sequence.
                yield break;
            }

            if (!trimmed.StartsWith("- ", StringComparison.Ordinal) && trimmed != "-")
            {
                continue;
            }

            var value = trimmed.Length >= 2 ? trimmed[2..].Trim() : string.Empty;
            if (value.Length == 0)
            {
                continue;
            }

            // Strip surrounding quotes.
            if ((value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2) ||
                (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2))
            {
                value = value[1..^1];
            }

            if (value.Length > 0)
            {
                yield return value;
            }
        }
    }
}
