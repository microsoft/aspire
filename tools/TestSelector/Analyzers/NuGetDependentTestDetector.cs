// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using TestSelector.Models;
using Microsoft.Extensions.FileSystemGlobbing;

namespace TestSelector.Analyzers;

/// <summary>
/// Detects when NuGet-dependent test projects should be triggered because
/// a packable source project (or archive-producing project) has changed.
/// </summary>
public sealed class NuGetDependentTestDetector
{
    private readonly List<string> _packageOrArchivePatterns;
    private readonly Matcher _packageOrArchiveMatcher;

    public NuGetDependentTestDetector(IEnumerable<string> packageOrArchiveProducingPatterns)
    {
        _packageOrArchivePatterns = packageOrArchiveProducingPatterns.ToList();
        _packageOrArchiveMatcher = new Matcher();
        foreach (var pattern in _packageOrArchivePatterns)
        {
            _packageOrArchiveMatcher.AddInclude(pattern);
        }
    }

    /// <summary>
    /// Detects NuGet-dependent tests that should run based on affected projects and changed files.
    /// </summary>
    /// <param name="affectedProjects">All projects from dotnet-affected (source + test).</param>
    /// <param name="activeFiles">Changed files that passed ignore filtering.</param>
    /// <param name="workingDir">Repository root directory for resolving file paths.</param>
    /// <returns>NuGet dependent test info, or null if no producers were affected.</returns>
    public NuGetDependentTestsInfo? Detect(
        List<string> affectedProjects,
        List<string> activeFiles,
        string workingDir)
    {
        var nugetProducers = new List<string>();

        // Check affected projects (from dotnet-affected) and active files against
        // packageOrArchiveProducingProjects glob patterns
        var candidates = affectedProjects.Concat(activeFiles).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var normalized = candidate.Replace('\\', '/');

            // Check against glob patterns first
            if (_packageOrArchiveMatcher.Match(normalized).HasMatches)
            {
                nugetProducers.Add(normalized);
                continue;
            }

            // If it's a .csproj path, check for <IsPackable>true</IsPackable>
            if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var fullPath = Path.IsPathRooted(normalized)
                    ? normalized
                    : Path.Combine(workingDir, normalized);

                if (File.Exists(fullPath) && IsPackableProject(fullPath))
                {
                    nugetProducers.Add(normalized);
                }
            }
        }

        if (nugetProducers.Count == 0)
        {
            return null;
        }

        // Scan test csproj files for <RequiredNuGetsForTesting>true</RequiredNuGetsForTesting>
        var nugetTestProjects = FindNuGetDependentTestProjects(workingDir);

        if (nugetTestProjects.Count == 0)
        {
            return null;
        }

        return new NuGetDependentTestsInfo
        {
            Triggered = true,
            Reason = $"Packable/archive projects affected: {string.Join(", ", nugetProducers)}",
            AffectedPackableProjects = nugetProducers,
            Projects = nugetTestProjects
        };
    }

    /// <summary>
    /// Finds all test projects that require NuGet packages by scanning for
    /// &lt;RequiredNuGetsForTesting&gt;true&lt;/RequiredNuGetsForTesting&gt; in test csproj files.
    /// Skips TestFixtures directories.
    /// </summary>
    internal static List<string> FindNuGetDependentTestProjects(string workingDir)
    {
        var testsDir = Path.Combine(workingDir, "tests");
        if (!Directory.Exists(testsDir))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var csproj in Directory.EnumerateFiles(testsDir, "*.csproj", SearchOption.AllDirectories))
        {
            // Skip TestFixtures directories to avoid false positives
            var normalizedCsproj = csproj.Replace('\\', '/');
            if (normalizedCsproj.Contains("/TestFixtures/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasRequiredNuGetsForTesting(csproj))
            {
                // Convert to relative .csproj path with forward slashes
                var relativePath = Path.GetRelativePath(workingDir, csproj)
                    .Replace('\\', '/');
                result.Add(relativePath);
            }
        }

        return result;
    }

    /// <summary>
    /// Checks if a csproj file has &lt;RequiredNuGetsForTesting&gt;true&lt;/RequiredNuGetsForTesting&gt;.
    /// </summary>
    internal static bool HasRequiredNuGetsForTesting(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            return doc.Descendants("RequiredNuGetsForTesting")
                .Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a csproj file has &lt;IsPackable&gt;true&lt;/IsPackable&gt;.
    /// Warns if the element has a Condition attribute.
    /// </summary>
    internal static bool IsPackableProject(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var isPackableElements = doc.Descendants("IsPackable").ToList();

            foreach (var element in isPackableElements)
            {
                if (element.Attribute("Condition") != null)
                {
                    Console.Error.WriteLine(
                        $"  Warning: {csprojPath} has <IsPackable> with a Condition attribute - " +
                        "cannot reliably determine packability");
                    continue;
                }

                if (string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // If no <IsPackable> element found, SDK defaults vary.
            // We conservatively return false (only detect explicit IsPackable=true).
            return false;
        }
        catch
        {
            return false;
        }
    }
}
