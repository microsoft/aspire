// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using TestSelector.Models;
using Microsoft.Extensions.FileSystemGlobbing;

namespace TestSelector.Analyzers;

/// <summary>
/// Resolves test projects from changed files using sourceToTestMappings configuration.
/// Supports {name} capture group substitution for flexible source-to-test mapping.
/// </summary>
public sealed class ProjectMappingResolver
{
    private readonly List<CompiledMapping> _mappings;

    public ProjectMappingResolver(IEnumerable<SourceToTestMapping> mappings)
    {
        _mappings = mappings.Select(m => new CompiledMapping(m)).ToList();
    }

    /// <summary>
    /// Returns test project path(s) for a changed file, or empty if no match.
    /// </summary>
    /// <param name="changedFilePath">The changed file path to resolve.</param>
    /// <returns>List of test project paths (usually 0 or 1).</returns>
    public List<string> ResolveTestProjects(string changedFilePath)
    {
        var normalizedPath = changedFilePath.Replace('\\', '/');
        var results = new List<string>();

        foreach (var mapping in _mappings)
        {
            var testProject = mapping.TryMatch(normalizedPath);
            if (testProject != null && !results.Contains(testProject))
            {
                results.Add(testProject);
            }
        }

        return results;
    }

    /// <summary>
    /// Batch resolution - returns unique test projects for all changed files.
    /// </summary>
    /// <param name="changedFiles">The changed files to resolve.</param>
    /// <returns>Unique list of test project paths.</returns>
    public List<string> ResolveAllTestProjects(IEnumerable<string> changedFiles)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in changedFiles)
        {
            var testProjects = ResolveTestProjects(file);
            foreach (var project in testProjects)
            {
                results.Add(project);
            }
        }

        return results.ToList();
    }

    /// <summary>
    /// Batch resolution with detailed matching information.
    /// </summary>
    /// <param name="changedFiles">The changed files to resolve.</param>
    /// <returns>Result containing mappings and test projects.</returns>
    public ProjectMappingResult ResolveAllWithDetails(IEnumerable<string> changedFiles)
    {
        var result = new ProjectMappingResult();

        foreach (var file in changedFiles)
        {
            var normalizedPath = file.Replace('\\', '/');
            var matched = false;

            foreach (var mapping in _mappings)
            {
                var matchResult = mapping.TryMatchWithDetails(normalizedPath);
                if (matchResult != null)
                {
                    result.Mappings.Add(matchResult);
                    result.TestProjects.Add(matchResult.TestProject);
                    result.MatchedFiles.Add(normalizedPath);
                    matched = true;
                }
            }

            if (!matched && Matches(file))
            {
                // File matched but no test project resolved (edge case)
                result.MatchedFiles.Add(normalizedPath);
            }
        }

        return result;
    }

    /// <summary>
    /// Check if a file matches any projectMapping pattern.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    /// <returns>True if the file matches any mapping.</returns>
    public bool Matches(string filePath)
    {
        var normalizedPath = filePath.Replace('\\', '/');

        foreach (var mapping in _mappings)
        {
            if (mapping.Matches(normalizedPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the number of configured mappings.
    /// </summary>
    public int MappingCount => _mappings.Count;

    private sealed class CompiledMapping
    {
        private readonly CompiledSourcePattern[] _sources;
        private readonly string _testPattern;
        private readonly Matcher _excludeMatcher;

        public CompiledMapping(SourceToTestMapping mapping)
        {
            _testPattern = mapping.Test;
            _sources = mapping.Source.Select(s => new CompiledSourcePattern(s)).ToArray();

            // Build exclude matcher (applied to every source pattern in this mapping).
            _excludeMatcher = new Matcher();
            foreach (var exclude in mapping.Exclude)
            {
                _excludeMatcher.AddInclude(exclude);
            }
        }

        public string? TryMatch(string filePath)
        {
            // Check excludes first
            if (_excludeMatcher.Match(filePath).HasMatches)
            {
                return null;
            }

            foreach (var src in _sources)
            {
                var match = src.Regex.Match(filePath);
                if (!match.Success)
                {
                    continue;
                }

                if (src.HasCapture)
                {
                    var nameGroup = match.Groups["name"];
                    if (!nameGroup.Success)
                    {
                        continue;
                    }

                    return _testPattern.Replace("{name}", nameGroup.Value);
                }

                return _testPattern;
            }

            return null;
        }

        public ProjectMappingMatch? TryMatchWithDetails(string filePath)
        {
            // Check excludes first
            if (_excludeMatcher.Match(filePath).HasMatches)
            {
                return null;
            }

            foreach (var src in _sources)
            {
                var match = src.Regex.Match(filePath);
                if (!match.Success)
                {
                    continue;
                }

                string testProject;
                string? capturedName = null;

                if (src.HasCapture)
                {
                    var nameGroup = match.Groups["name"];
                    if (!nameGroup.Success)
                    {
                        continue;
                    }

                    capturedName = nameGroup.Value;
                    testProject = _testPattern.Replace("{name}", capturedName);
                }
                else
                {
                    testProject = _testPattern;
                }

                return new ProjectMappingMatch
                {
                    SourceFile = filePath,
                    SourcePattern = src.Pattern,
                    TestPattern = _testPattern,
                    TestProject = testProject,
                    CapturedName = capturedName
                };
            }

            return null;
        }

        public bool Matches(string filePath)
        {
            // Check excludes first
            if (_excludeMatcher.Match(filePath).HasMatches)
            {
                return false;
            }

            foreach (var src in _sources)
            {
                if (src.Regex.IsMatch(filePath))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ConvertGlobToRegex(string globPattern)
        {
            // Normalize path separators
            var pattern = globPattern.Replace('\\', '/');

            // Escape regex special characters (except * and ?)
            pattern = Regex.Escape(pattern);

            // Restore escaped glob patterns and convert to regex
            // Note: Regex.Escape escapes { but not }, so we check for both patterns
            // First, handle {name} capture group
            pattern = pattern.Replace("\\{name}", "(?<name>[^/]+)");
            pattern = pattern.Replace("\\{name\\}", "(?<name>[^/]+)"); // In case } is also escaped

            // Handle ** (match any path including separators)
            pattern = pattern.Replace("\\*\\*", ".*");

            // Handle * (match any characters except path separator)
            pattern = pattern.Replace("\\*", "[^/]*");

            // Handle ? (match single character)
            pattern = pattern.Replace("\\?", ".");

            return "^" + pattern + "$";
        }

        private sealed class CompiledSourcePattern
        {
            public CompiledSourcePattern(string pattern)
            {
                Pattern = pattern;
                HasCapture = pattern.Contains("{name}");
                Regex = new Regex(ConvertGlobToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }

            public string Pattern { get; }
            public bool HasCapture { get; }
            public Regex Regex { get; }
        }
    }
}

/// <summary>
/// Result of project mapping resolution with detailed information.
/// </summary>
public sealed class ProjectMappingResult
{
    /// <summary>
    /// All resolved mappings.
    /// </summary>
    public List<ProjectMappingMatch> Mappings { get; } = [];

    /// <summary>
    /// Unique set of resolved test projects.
    /// </summary>
    public HashSet<string> TestProjects { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files that matched at least one mapping.
    /// </summary>
    public HashSet<string> MatchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Information about a single file-to-project mapping match.
/// </summary>
public sealed class ProjectMappingMatch
{
    /// <summary>
    /// The source file that was matched.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// The source pattern that matched.
    /// </summary>
    public required string SourcePattern { get; init; }

    /// <summary>
    /// The test pattern used for resolution.
    /// </summary>
    public required string TestPattern { get; init; }

    /// <summary>
    /// The resolved test project path.
    /// </summary>
    public required string TestProject { get; init; }

    /// <summary>
    /// The captured {name} value, if applicable.
    /// </summary>
    public string? CapturedName { get; init; }
}
