// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.FileSystemGlobbing;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Strongly-typed view of <c>docs/ci/test-trigger-map.yml</c>. The map is curated by hand,
/// so the verifier tests in <see cref="TestTriggerMapTests"/> load this model and assert it
/// stays consistent with repo reality (referenced projects/jobs exist, every source project
/// is reachable by some rule, etc.).
/// </summary>
public sealed class TestTriggerMap
{
    public int Version { get; set; }

    public Dictionary<string, List<string>> Aliases { get; set; } = new();

    public TestSelfRule? TestSelf { get; set; }

    public List<string> RunAllGlobs { get; set; } = new();

    public List<PathRule> TestHubs { get; set; } = new();

    public List<PathRule> SharedSource { get; set; } = new();

    public List<PathRule> CoreSource { get; set; } = new();

    public List<JobRule> CuratedJobs { get; set; } = new();

    public List<PathRule> LooseFileDeps { get; set; } = new();

    public List<PathRule> LeafSource { get; set; } = new();

    public List<PathRule> SharedCompiledSource { get; set; } = new();

    public List<PathRule> Gaps { get; set; } = new();

    /// <summary>
    /// Every rule section that carries <c>paths</c> globs, in one sequence. Used by the
    /// path-existence and coverage checks. <see cref="Gaps"/> is excluded: it documents
    /// known-uncovered source on purpose, so it must not satisfy coverage.
    /// </summary>
    public IEnumerable<PathRule> AllPathRules()
    {
        foreach (var r in TestHubs) { yield return r; }
        foreach (var r in SharedSource) { yield return r; }
        foreach (var r in CoreSource) { yield return r; }
        foreach (var r in LooseFileDeps) { yield return r; }
        foreach (var r in LeafSource) { yield return r; }
        foreach (var r in SharedCompiledSource) { yield return r; }
    }

    /// <summary>
    /// Every glob that, when matched, should select at least one target — i.e. all rule paths
    /// plus the curated-job paths and the catch-all globs. Drives source-project coverage.
    /// </summary>
    public IEnumerable<string> AllSelectingGlobs()
    {
        foreach (var g in RunAllGlobs) { yield return g; }
        foreach (var r in AllPathRules())
        {
            foreach (var p in r.Paths) { yield return p; }
        }
        foreach (var j in CuratedJobs)
        {
            foreach (var p in j.Paths) { yield return p; }
        }
    }

    /// <summary>
    /// Every concrete target string (<c>test:*</c> / <c>job:*</c> / <c>ALL*</c>) referenced
    /// anywhere in the map: alias members, every rule's targets, and the curated-job targets.
    /// The <c>test_self</c> target is excluded because it is the literal placeholder
    /// <c>test:&lt;TestProject&gt;</c>, not a concrete project reference.
    /// </summary>
    public IEnumerable<string> AllReferencedTargets()
    {
        foreach (var members in Aliases.Values)
        {
            foreach (var t in members) { yield return t; }
        }
        foreach (var r in AllPathRules())
        {
            foreach (var t in r.Targets) { yield return t; }
        }
        foreach (var j in CuratedJobs) { yield return j.Target; }
    }

    /// <summary>
    /// Resolves which test projects the map selects for a single changed file: the union of
    /// every matching rule's targets, with aliases expanded to their member test projects.
    /// <paramref name="selectsAll"/> is set when any matching rule yields the <c>ALL</c>
    /// sentinel (the whole matrix), in which case the test-project set is not meaningful.
    /// <c>job:</c> targets are ignored — this resolves test projects only.
    /// </summary>
    public IReadOnlySet<string> SelectTestProjects(string changedPath, out bool selectsAll)
        => SelectTestProjects(changedPath, projectLevelOnly: false, out selectsAll);

    /// <param name="projectLevelOnly">
    /// When true, resolves using only the rules derived from the ProjectReference graph
    /// (<c>run_all_globs</c>, <c>core_source</c>, <c>leaf_source</c>) — the rules a
    /// project-granular graph tool like <c>dotnet-affected</c> can reproduce. File-granular
    /// (<c>shared_compiled_source</c>), runtime (<c>loose_file_deps</c>), and test-side
    /// (<c>test_hubs</c>) rules are excluded because that tool cannot see them at project
    /// granularity. Used by the oracle cross-check.
    /// </param>
    public IReadOnlySet<string> SelectTestProjects(string changedPath, bool projectLevelOnly, out bool selectsAll)
    {
        selectsAll = false;
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (globs, targets) in SelectionRules(projectLevelOnly))
        {
            if (!globs.Any(g => GlobMatches(g, changedPath)))
            {
                continue;
            }

            foreach (var target in targets)
            {
                if (target == "ALL")
                {
                    selectsAll = true;
                }
                else if (Aliases.TryGetValue(target, out var members))
                {
                    foreach (var m in members) { result.Add(StripTestPrefix(m)); }
                }
                else if (target.StartsWith("test:", StringComparison.Ordinal))
                {
                    result.Add(StripTestPrefix(target));
                }
                // job: targets are not test projects; ignore.
            }
        }

        return result;
    }

    private IEnumerable<(IReadOnlyList<string> Globs, IReadOnlyList<string> Targets)> SelectionRules(bool projectLevelOnly)
    {
        // run_all_globs is a flat list of globs that all select the ALL sentinel.
        yield return (RunAllGlobs, new[] { "ALL" });

        if (projectLevelOnly)
        {
            foreach (var r in CoreSource) { yield return (r.Paths, r.Targets); }
            foreach (var r in LeafSource) { yield return (r.Paths, r.Targets); }
            yield break;
        }

        foreach (var r in AllPathRules()) { yield return (r.Paths, r.Targets); }
        foreach (var j in CuratedJobs) { yield return (j.Paths, new[] { j.Target }); }
    }

    private static string StripTestPrefix(string target) =>
        target.StartsWith("test:", StringComparison.Ordinal) ? target["test:".Length..] : target;

    /// <summary>Matches a repo-relative '/'-separated path against a single map glob.</summary>
    public static bool GlobMatches(string glob, string path)
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude(glob);
        return matcher.Match(new[] { path }).HasMatches;
    }

    public static TestTriggerMap Load(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "docs", "ci", "test-trigger-map.yml");
        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<TestTriggerMap>(yaml);
    }
}

/// <summary>A rule keyed by one or more path globs that selects a set of targets.</summary>
public sealed class PathRule
{
    public List<string> Paths { get; set; } = new();

    public List<string> Targets { get; set; } = new();

    public string? Note { get; set; }

    public string? Reason { get; set; }

    public int? Fanout { get; set; }
}

/// <summary>A curated job rule: a single <c>job:</c> target gated by a set of path globs.</summary>
public sealed class JobRule
{
    public string Target { get; set; } = "";

    public string? Reason { get; set; }

    public List<string> Paths { get; set; } = new();
}

/// <summary>The <c>test_self</c> rule: a test project's own folder always runs that project.</summary>
public sealed class TestSelfRule
{
    public string Pattern { get; set; } = "";

    public string Target { get; set; } = "";

    public string? Note { get; set; }
}
