// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.FileSystemGlobbing;

namespace TestSelector.Analyzers;

/// <summary>
/// Filters out files that should be ignored based on glob patterns.
/// </summary>
public sealed class IgnorePathFilter
{
    private readonly List<string> _ignorePatterns;
    private readonly Matcher _matcher;
    private readonly Dictionary<string, Matcher> _individualMatchers;

    public IgnorePathFilter(IEnumerable<string> ignorePatterns)
    {
        _ignorePatterns = ignorePatterns.ToList();
        _matcher = new Matcher();
        _individualMatchers = [];

        foreach (var pattern in _ignorePatterns)
        {
            _matcher.AddInclude(pattern);

            // Create individual matchers for detailed diagnostics
            var singleMatcher = new Matcher();
            singleMatcher.AddInclude(pattern);
            _individualMatchers[pattern] = singleMatcher;
        }
    }

    /// <summary>
    /// Checks if a file should be ignored.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    /// <returns>True if the file should be ignored.</returns>
    public bool ShouldIgnore(string filePath)
    {
        // Normalize path separators
        var normalizedPath = filePath.Replace('\\', '/');

        // Check against all patterns using the Matcher
        var result = _matcher.Match(normalizedPath);
        return result.HasMatches;
    }

    /// <summary>
    /// Checks if a file should be ignored and returns the matching pattern.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    /// <param name="matchedPattern">The pattern that matched, if any.</param>
    /// <returns>True if the file should be ignored.</returns>
    public bool ShouldIgnore(string filePath, out string? matchedPattern)
    {
        matchedPattern = null;
        var normalizedPath = filePath.Replace('\\', '/');

        if (!_matcher.Match(normalizedPath).HasMatches)
        {
            return false;
        }

        // Find which pattern matched
        matchedPattern = FindMatchingPattern(normalizedPath);
        return true;
    }

    /// <summary>
    /// Splits files into ignored and active lists.
    /// </summary>
    /// <param name="files">The files to split.</param>
    /// <returns>A tuple of (ignoredFiles, activeFiles).</returns>
    public (List<string> Ignored, List<string> Active) SplitFiles(IEnumerable<string> files)
    {
        var ignored = new List<string>();
        var active = new List<string>();

        foreach (var file in files)
        {
            if (ShouldIgnore(file))
            {
                ignored.Add(file);
            }
            else
            {
                active.Add(file);
            }
        }

        return (ignored, active);
    }

    /// <summary>
    /// Splits files into ignored and active lists, with detailed matching information.
    /// </summary>
    /// <param name="files">The files to split.</param>
    /// <returns>A result containing ignored files with matched patterns and active files.</returns>
    public IgnoreFilterResult SplitFilesWithDetails(IEnumerable<string> files)
    {
        var result = new IgnoreFilterResult();

        foreach (var file in files)
        {
            if (ShouldIgnore(file, out var matchedPattern))
            {
                result.IgnoredFiles.Add(new IgnoredFileInfo
                {
                    FilePath = file,
                    MatchedPattern = matchedPattern ?? "unknown"
                });
            }
            else
            {
                result.ActiveFiles.Add(file);
            }
        }

        return result;
    }

    private string? FindMatchingPattern(string filePath)
    {
        foreach (var (pattern, matcher) in _individualMatchers)
        {
            if (matcher.Match(filePath).HasMatches)
            {
                return pattern;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the patterns being used for filtering.
    /// </summary>
    public IReadOnlyList<string> Patterns => _ignorePatterns;
}

/// <summary>
/// Result of ignore path filtering with detailed information.
/// </summary>
public sealed class IgnoreFilterResult
{
    /// <summary>
    /// Files that were ignored with their matching pattern.
    /// </summary>
    public List<IgnoredFileInfo> IgnoredFiles { get; } = [];

    /// <summary>
    /// Files that were not ignored.
    /// </summary>
    public List<string> ActiveFiles { get; } = [];
}

/// <summary>
/// Information about an ignored file.
/// </summary>
public sealed class IgnoredFileInfo
{
    /// <summary>
    /// The file path that was ignored.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// The pattern that caused the file to be ignored.
    /// </summary>
    public required string MatchedPattern { get; init; }
}
