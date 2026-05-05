// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Models;
using Microsoft.Extensions.FileSystemGlobbing;

namespace TestSelector.Analyzers;

/// <summary>
/// Filters projects to identify test projects based on glob patterns.
/// Uses convention-based matching from configuration.
/// </summary>
public sealed class TestProjectFilter
{
    private readonly Matcher _includeMatcher;
    private readonly Matcher _excludeMatcher;
    private readonly IncludeExcludePatterns _patterns;

    public TestProjectFilter(IncludeExcludePatterns patterns)
    {
        _patterns = patterns;

        _includeMatcher = new Matcher();
        foreach (var pattern in patterns.Include)
        {
            _includeMatcher.AddInclude(pattern);
        }

        _excludeMatcher = new Matcher();
        foreach (var pattern in patterns.Exclude)
        {
            _excludeMatcher.AddInclude(pattern);
        }
    }

    /// <summary>
    /// Checks if a project path matches the test project patterns.
    /// </summary>
    /// <param name="projectPath">Path to the project file.</param>
    /// <returns>True if the project matches test project patterns.</returns>
    public bool IsTestProject(string projectPath)
    {
        var normalizedPath = projectPath.Replace('\\', '/');

        // Check excludes first
        if (_excludeMatcher.Match(normalizedPath).HasMatches)
        {
            return false;
        }

        return _includeMatcher.Match(normalizedPath).HasMatches;
    }

    /// <summary>
    /// Filters a list of projects to only test projects.
    /// </summary>
    /// <param name="projectPaths">List of project paths.</param>
    /// <returns>List of test project paths.</returns>
    public List<string> FilterTestProjects(IEnumerable<string> projectPaths)
    {
        return projectPaths.Where(IsTestProject).ToList();
    }

    /// <summary>
    /// Filters projects with detailed classification info.
    /// </summary>
    /// <param name="projectPaths">List of project paths.</param>
    /// <returns>Detailed filter result.</returns>
    public TestProjectFilterResult FilterWithDetails(IEnumerable<string> projectPaths)
    {
        var result = new TestProjectFilterResult();

        foreach (var path in projectPaths)
        {
            var normalizedPath = path.Replace('\\', '/');
            var info = new ProjectInfoWithReason
            {
                Path = path,
                Name = Path.GetFileNameWithoutExtension(path)
            };

            // Check excludes first
            if (_excludeMatcher.Match(normalizedPath).HasMatches)
            {
                info.IsTestProject = false;
                info.ClassificationReason = "Excluded by testProjectPatterns.exclude";
                result.ExcludedProjects.Add(info);
                continue;
            }

            if (_includeMatcher.Match(normalizedPath).HasMatches)
            {
                info.IsTestProject = true;
                info.ClassificationReason = "Matched testProjectPatterns.include";
                result.TestProjects.Add(info);
            }
            else
            {
                info.IsTestProject = false;
                info.ClassificationReason = "Did not match testProjectPatterns.include";
                result.OtherProjects.Add(info);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the include patterns being used.
    /// </summary>
    public IReadOnlyList<string> IncludePatterns => _patterns.Include;

    /// <summary>
    /// Gets the exclude patterns being used.
    /// </summary>
    public IReadOnlyList<string> ExcludePatterns => _patterns.Exclude;
}

/// <summary>
/// Result of test project filtering with detailed classification information.
/// </summary>
public sealed class TestProjectFilterResult
{
    /// <summary>
    /// Projects that matched as test projects.
    /// </summary>
    public List<ProjectInfoWithReason> TestProjects { get; } = [];

    /// <summary>
    /// Projects that were excluded by exclude patterns.
    /// </summary>
    public List<ProjectInfoWithReason> ExcludedProjects { get; } = [];

    /// <summary>
    /// Projects that didn't match include patterns.
    /// </summary>
    public List<ProjectInfoWithReason> OtherProjects { get; } = [];
}

/// <summary>
/// Extended project information including the reason for classification.
/// </summary>
public sealed class ProjectInfoWithReason
{
    /// <summary>
    /// Full path to the project file.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Project name (without extension).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Whether the project is a test project.
    /// </summary>
    public bool IsTestProject { get; set; }

    /// <summary>
    /// Human-readable explanation for why this project was classified.
    /// </summary>
    public string ClassificationReason { get; set; } = "";
}
