// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph;
using Microsoft.Build.Locator;

namespace Aspire.SelectTests;

/// <summary>
/// Layer 1 of the selective-CI selector: computes the set of projects affected by a PR's changed
/// files, using a static MSBuild <see cref="ProjectGraph"/> built from <c>Aspire.slnx</c> at the PR
/// head (HEAD-only — never evaluating from-commit content). This replaces the previous
/// <c>dotnet-affected</c> shell-out.
/// </summary>
/// <remarks>
/// <para>
/// Why HEAD-only graph (not <c>dotnet-affected</c>): <c>dotnet-affected</c> reads from-commit blobs
/// through a libgit2-backed MSBuild virtual filesystem to diff packages, which (a) crashes whenever
/// the diff touches <c>Directory.Packages.props</c> — it eager-loads <c>global.json</c> as MSBuild
/// XML (leonardochaia/dotnet-affected#155) — and (b) cannot run inside a git worktree. A HEAD-only
/// graph never evaluates from-commit content, so both problems disappear. Two-commit central-package
/// diffing is intentionally not reproduced; Layer 2 routes <c>Directory.Packages.props -&gt; ALL</c>.
/// </para>
/// <para>
/// Why no <c>Microsoft.Build.Prediction</c>: a file-&gt;project index built from the evaluated
/// <c>ProjectInstance</c> items (resolved <c>FullPath</c>, so cross-project linked files map to the
/// linking projects), plus <see cref="ProjectInstance.ImportPaths"/> (which captures repo hook files
/// imported through SDK/Arcade targets that live in the NuGet cache), reaches every file class that
/// matters here. Measured equal-or-superset of the prediction-based index on every changed-file
/// scenario, with no third-party package dependency.
/// </para>
/// </remarks>
/// <summary>
/// The outcome of the Layer 1 graph computation.
/// </summary>
/// <param name="AffectedProjects">
/// Affected project base names (production + test) — the reverse-dependency closure of the change.
/// </param>
/// <param name="AttributedPaths">
/// The changed repo-relative ('/'-separated) paths the graph actually mapped to a project (via the
/// evaluated-item <c>FullPath</c> index or directory containment). The selector treats these as
/// Layer-1-owned, so a link-compiled <c>src/Shared</c>/<c>tests/Shared</c> file — attributed but not
/// under a project directory — does not trip the run-all fallback without needing an <c>ignore</c> entry.
/// </param>
internal sealed record AffectedResult(
    IReadOnlyCollection<string> AffectedProjects,
    IReadOnlySet<string> AttributedPaths)
{
    public static readonly AffectedResult Empty =
        new(Array.Empty<string>(), new HashSet<string>(StringComparer.Ordinal));
}

internal static class GraphAffectedProjects
{
    // Linux CI is the source of truth, but the tool also runs on dev machines (incl. case-insensitive
    // macOS/Windows filesystems). Project graph paths come back from MSBuild with OS-native casing, so
    // index/compare paths case-insensitively to avoid a same-file/different-case miss.
    private static readonly StringComparer s_pathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Registers the SDK's MSBuild assemblies with <see cref="MSBuildLocator"/>. MUST be called once
    /// before <see cref="Compute"/> (or any other method that touches a <c>Microsoft.Build</c> engine
    /// type). It only references <see cref="MSBuildLocator"/> — not the engine — so JITting it does not
    /// trigger engine assembly resolution before the resolver is in place; <see cref="Compute"/>'s body
    /// is JITted later, by which time the resolver supplies the engine from the SDK.
    /// </summary>
    public static void EnsureMSBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    /// <summary>
    /// Computes the projects affected by the changed files: the base names (csproj filename without
    /// extension — the same shape <c>dotnet-affected</c> emitted as <c>Name</c>, which
    /// <see cref="TestSelector"/> intersects with the matrix and matches against <c>project_rules</c>),
    /// plus the set of changed repo-relative paths the graph actually <em>attributed</em> to a project
    /// (so the selector can treat those as Layer-1-owned without relying on a directory heuristic).
    /// </summary>
    /// <param name="repoRoot">Repository root (where <c>Aspire.slnx</c> and <c>.git</c> live).</param>
    /// <param name="from">Base git ref to diff from. Required unless <paramref name="changedFilesPath"/> is given.</param>
    /// <param name="to">Head git ref. When null (and <paramref name="from"/> is set), diffs against the working tree.</param>
    /// <param name="changedFilesPath">
    /// Optional newline-delimited list of changed repo-relative paths, used instead of a git diff.
    /// When supplied there is no rename/delete status, so every path is treated as present.
    /// </param>
    /// <param name="filter">
    /// Optional pre-filter (the map's <c>prefilter</c>). Changed paths it excludes are dropped before
    /// attribution, so an excluded file (e.g. a packed <c>README.md</c>) never maps to a project. The
    /// same filter is applied to the Layer 2 input in <c>Program</c>.
    /// </param>
    public static AffectedResult Compute(string repoRoot, string? from, string? to, string? changedFilesPath, ChangedFileFilter? filter = null)
    {
        repoRoot = NormalizeFullPath(repoRoot);
        var solutionPath = NormalizeFullPath(Path.Combine(repoRoot, "Aspire.slnx"));
        if (!File.Exists(solutionPath))
        {
            throw new InvalidOperationException($"Aspire.slnx was not found under repository root: {repoRoot}");
        }

        var changedPaths = ResolveChangedPaths(repoRoot, from, to, changedFilesPath, filter);
        if (changedPaths.Count == 0)
        {
            return AffectedResult.Empty;
        }

        var graph = BuildGraph(repoRoot, solutionPath);
        var graphNodes = graph.ProjectNodes.ToArray();

        var nodesByProjectPath = BuildNodesByProjectPath(graphNodes);
        var inputFileIndex = BuildInputFileIndex(graphNodes);
        // Project directories sorted longest-first so the containment fallback attributes a file to the
        // most-specific (deepest) owning project, not a parent directory that happens to share a prefix.
        var projectDirectories = nodesByProjectPath.Keys
            .Select(p => NormalizeFullPath(Path.GetDirectoryName(p)!))
            .Distinct(s_pathComparer)
            .OrderByDescending(d => d.Length)
            .ToArray();

        var directlyChangedProjects = FindDirectlyChangedProjects(
            changedPaths, nodesByProjectPath, inputFileIndex, projectDirectories, out var attributedPaths);

        var affectedProjects = ComputeReverseClosure(directlyChangedProjects, nodesByProjectPath);

        var names = affectedProjects
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        return new AffectedResult(names, attributedPaths);
    }

    private static ProjectGraph BuildGraph(string repoRoot, string solutionPath)
    {
        // Mirror the global properties the solution build evaluates with so the graph nodes match the
        // real build's project instances (and so .slnx solution-folder/config resolution succeeds).
        var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = "Debug",
            ["Platform"] = "AnyCPU",
            ["SolutionDir"] = EnsureTrailingSlash(repoRoot),
            ["SolutionPath"] = solutionPath,
            ["SolutionFileName"] = Path.GetFileName(solutionPath),
            ["SolutionName"] = Path.GetFileNameWithoutExtension(solutionPath),
        };

        return new ProjectGraph(solutionPath, globalProperties);
    }

    private static Dictionary<string, List<ProjectGraphNode>> BuildNodesByProjectPath(IEnumerable<ProjectGraphNode> graphNodes)
    {
        // A project can appear as several graph nodes (different global properties / target frameworks).
        // Group them so seeding from a project path enqueues every node that represents it.
        var nodesByProjectPath = new Dictionary<string, List<ProjectGraphNode>>(s_pathComparer);
        foreach (var node in graphNodes)
        {
            var projectPath = NormalizeFullPath(node.ProjectInstance.FullPath);
            if (!nodesByProjectPath.TryGetValue(projectPath, out var nodes))
            {
                nodes = [];
                nodesByProjectPath.Add(projectPath, nodes);
            }

            nodes.Add(node);
        }

        return nodesByProjectPath;
    }

    // Item types that can carry a repo file a change would touch. Compile/None/Content/etc. cover the
    // common cases; the per-project AvailableItemName set adds any custom registered types (e.g.
    // Protobuf) so we don't silently miss them.
    private static readonly string[] s_indexedItemTypes =
    [
        "Compile", "Content", "ContentWithTargetPath", "None", "EmbeddedResource",
        "AdditionalFiles", "Analyzer", "EditorConfigFiles", "GlobalAnalyzerConfigFiles",
        "TypeScriptCompile", "ApplicationDefinition", "Page", "Resource",
    ];

    private static Dictionary<string, HashSet<string>> BuildInputFileIndex(IEnumerable<ProjectGraphNode> graphNodes)
    {
        var index = new Dictionary<string, HashSet<string>>(s_pathComparer);

        foreach (var node in graphNodes)
        {
            var instance = node.ProjectInstance;
            var projectPath = NormalizeFullPath(instance.FullPath);

            // The project file itself: a .csproj change is "directly changed".
            AddToIndex(index, projectPath, projectPath);

            // Imported props/targets, including repo files imported through SDK/Arcade targets that
            // live in the NuGet cache (e.g. eng/Versions.props, Directory.Build.props). ImportPaths
            // records every evaluated import regardless of where the importing file lives.
            foreach (var importPath in instance.ImportPaths)
            {
                AddToIndex(index, NormalizeFullPath(importPath), projectPath);
            }

            // Evaluated items. GetMetadataValue("FullPath") resolves linked items (<Compile
            // Include="..\Shared\X.cs" Link="..."/>) to their SOURCE path, so a shared file maps to
            // every project that links it — the cross-project reach that matters most.
            var itemTypes = new HashSet<string>(s_indexedItemTypes, StringComparer.OrdinalIgnoreCase);
            foreach (var availableItemName in instance.GetItems("AvailableItemName"))
            {
                itemTypes.Add(availableItemName.EvaluatedInclude);
            }

            foreach (var itemType in itemTypes)
            {
                foreach (var item in instance.GetItems(itemType))
                {
                    var fullPath = item.GetMetadataValue("FullPath");
                    if (!string.IsNullOrWhiteSpace(fullPath))
                    {
                        AddToIndex(index, NormalizeFullPath(fullPath), projectPath);
                    }
                }
            }
        }

        return index;
    }

    private static HashSet<string> FindDirectlyChangedProjects(
        IReadOnlyCollection<ChangedPath> changedPaths,
        IReadOnlyDictionary<string, List<ProjectGraphNode>> nodesByProjectPath,
        IReadOnlyDictionary<string, HashSet<string>> inputFileIndex,
        IReadOnlyList<string> projectDirectoriesLongestFirst,
        out IReadOnlySet<string> attributedRepoRelativePaths)
    {
        var directlyChanged = new HashSet<string>(s_pathComparer);
        // The repo-relative paths the graph actually attributed to a project (by either mechanism). The
        // selector treats these as Layer-1-owned so the run-all fallback does not fire for them.
        var attributed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var changed in changedPaths)
        {
            if (inputFileIndex.TryGetValue(changed.Absolute, out var owningProjects))
            {
                foreach (var project in owningProjects)
                {
                    directlyChanged.Add(project);
                }

                attributed.Add(changed.Relative);
                continue;
            }

            // Not an evaluated input at HEAD. This is the deleted-file / old-side-of-rename case (the
            // file no longer exists, so no project lists it), and the catch-all for project-owned files
            // not modeled as a known item type. Attribute it to the deepest project directory that
            // contains it; the reverse closure then pulls in that project's dependents.
            foreach (var directory in projectDirectoriesLongestFirst)
            {
                if (IsUnder(changed.Absolute, directory))
                {
                    foreach (var project in nodesByProjectPath.Keys)
                    {
                        if (s_pathComparer.Equals(NormalizeFullPath(Path.GetDirectoryName(project)!), directory))
                        {
                            directlyChanged.Add(project);
                        }
                    }

                    attributed.Add(changed.Relative);
                    break;
                }
            }
        }

        attributedRepoRelativePaths = attributed;
        return directlyChanged;
    }

    private static HashSet<string> ComputeReverseClosure(
        IEnumerable<string> directlyChangedProjects,
        IReadOnlyDictionary<string, List<ProjectGraphNode>> nodesByProjectPath)
    {
        var affected = new HashSet<string>(s_pathComparer);
        var visited = new HashSet<ProjectGraphNode>();
        var queue = new Queue<ProjectGraphNode>();

        foreach (var project in directlyChangedProjects)
        {
            affected.Add(project);
            if (nodesByProjectPath.TryGetValue(project, out var nodes))
            {
                foreach (var node in nodes)
                {
                    queue.Enqueue(node);
                }
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            // ReferencingProjects = the nodes that have a ProjectReference TO current, i.e. downstream
            // dependents. Walking these transitively gives every project a change can break.
            foreach (var referencing in current.ReferencingProjects)
            {
                affected.Add(NormalizeFullPath(referencing.ProjectInstance.FullPath));
                queue.Enqueue(referencing);
            }
        }

        return affected;
    }

    // A changed file in both forms the graph needs: Relative is the repo-relative '/'-separated path
    // (what the selector matches and what AttributedPaths reports); Absolute is the normalized full
    // path used to look the file up in the evaluated-item index and project-directory containment.
    private readonly record struct ChangedPath(string Relative, string Absolute);

    // Resolves the set of changed repo files. When diffing with git we use --name-status -M so that
    // deletes and BOTH sides of a rename are captured: a file moving from project A to project B must
    // mark both A (it lost the file) and B (it gained it).
    private static IReadOnlyCollection<ChangedPath> ResolveChangedPaths(string repoRoot, string? from, string? to, string? changedFilesPath, ChangedFileFilter? filter)
    {
        IEnumerable<string> relativePaths;
        if (changedFilesPath is not null)
        {
            relativePaths = File.ReadAllLines(changedFilesPath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0);
        }
        else if (from is not null)
        {
            relativePaths = GetChangedPathsFromGit(repoRoot, from, to);
        }
        else
        {
            throw new InvalidOperationException("Provide either changedFilesPath or from (with optional to).");
        }

        // Pre-filter: drop files the prefilter marks as needing no CI (docs/skills/etc.) BEFORE
        // attribution, so an excluded file (e.g. a packed README.md <None> item) never gets mapped to a
        // project and fanned out. The same filter is applied to the Layer 2 input in Program.cs, so both
        // layers see the identical post-exclude change set.
        var normalized = relativePaths.Select(rel => rel.Replace('\\', '/'));
        if (filter is not null)
        {
            normalized = normalized.Where(rel => !filter.IsExcluded(rel));
        }

        return normalized
            .Select(rel => new ChangedPath(
                rel,
                NormalizeFullPath(Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar)))))
            .DistinctBy(cp => cp.Absolute, s_pathComparer)
            .ToList();
    }

    private static IEnumerable<string> GetChangedPathsFromGit(string repoRoot, string from, string? to)
    {
        // --name-status -M output, one record per change. Examples (TAB-separated):
        //   M\tsrc/Aspire.Hosting/Foo.cs
        //   A\tsrc/Aspire.Hosting/Bar.cs
        //   D\tsrc/Shared/Old.cs
        //   R097\tsrc/A/Old.cs\tsrc/B/New.cs     (rename: status, old path, new path)
        // For renames we take BOTH paths; for everything else the single path. -M detects renames so
        // the old path is reported as R..., not as separate D + A.
        // -c core.quotePath=false so a non-ASCII path comes back as the literal UTF-8 repo-relative
        // path, not git's default octal-escaped, double-quoted form (e.g. "src/caf\303\251.cs"). The
        // quoted form would neither match the file index nor split correctly on TAB below, mis-attributing
        // the change. (Program.cs's Layer 2 diff does the same.)
        var args = new List<string> { "-c", "core.quotePath=false", "diff", "--name-status", "-M" };
        args.Add(from);
        if (to is not null)
        {
            args.Add(to);
        }

        var stdout = RunGit(repoRoot, args);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            // fields[0] = status (M/A/D/Rxxx/Cxxx); fields[1..] = path(s). Renames/copies carry the old
            // path at [1] and the new path at [2]; both should be attributed.
            for (var i = 1; i < fields.Length; i++)
            {
                yield return fields[i];
            }
        }
    }

    private static string RunGit(string repoRoot, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--no-pager");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var stderr = stderrTask.GetAwaiter().GetResult();
            throw new InvalidOperationException($"git diff failed ({process.ExitCode}): {stderr}");
        }

        return stdoutTask.GetAwaiter().GetResult();
    }

    private static void AddToIndex(Dictionary<string, HashSet<string>> index, string key, string project)
    {
        if (!index.TryGetValue(key, out var projects))
        {
            projects = new HashSet<string>(s_pathComparer);
            index.Add(key, projects);
        }

        projects.Add(project);
    }

    private static bool IsUnder(string path, string directory)
    {
        var prefix = EnsureTrailingSlash(directory);
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSlash(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string NormalizeFullPath(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // On macOS the temp dir is a symlink (/tmp -> /private/tmp). MSBuild reports the resolved
        // /private/tmp form for project paths, while git/changed-file inputs may carry /tmp; normalize
        // so the two compare equal (matters for the test fixtures, which run under the temp dir).
        if (OperatingSystem.IsMacOS())
        {
            if (string.Equals(fullPath, "/tmp", StringComparison.Ordinal))
            {
                return "/private/tmp";
            }

            if (fullPath.StartsWith("/tmp/", StringComparison.Ordinal))
            {
                return "/private" + fullPath;
            }
        }

        return fullPath;
    }
}
