// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.SelectTests;

/// <summary>
/// Options that override normal selection.
/// </summary>
/// <param name="ForceAll">
/// The kill switch: a <c>[full ci]</c> token in the PR or a <c>run-all-tests</c> label forces the
/// whole matrix to run regardless of which files changed.
/// </param>
public sealed record SelectorOptions(bool ForceAll = false);

/// <summary>
/// The outcome of selecting which CI work to run for a set of changed files.
/// </summary>
/// <param name="SelectsAll">
/// True when the whole test matrix must run (a <c>run_all_globs</c> match, a fail-open escalation,
/// or the kill switch). When true, <see cref="TestProjects"/> is the full matrix.
/// </param>
/// <param name="TestProjects">The selected test project names (matrix <c>projectName</c>), aliases expanded.</param>
/// <param name="Jobs">The selected non-.NET jobs (e.g. <c>job:polyglot</c>, <c>job:extension-e2e</c>).</param>
/// <param name="EscalationReason">When <see cref="SelectsAll"/> is true, a short human-readable reason.</param>
public sealed record SelectionResult(
    bool SelectsAll,
    IReadOnlySet<string> TestProjects,
    IReadOnlySet<string> Jobs,
    string? EscalationReason);

/// <summary>
/// Filters the full CI matrix down to the subset relevant to a PR's changed files, using the
/// curated <c>docs/ci/test-trigger-map.yml</c> (Layer 2) unioned with a graph-derived affected set
/// (Layer 1, e.g. from <c>dotnet-affected</c>, supplied to <see cref="Select"/>).
/// </summary>
/// <remarks>
/// Behavior is specified by the acceptance tests in
/// <c>Infrastructure.Tests/TestTriggerMap/SelectTestsAcceptanceTests.cs</c>. The resolution logic
/// is not implemented yet; <see cref="Select"/> throws until it is.
/// </remarks>
public sealed class TestSelector
{
    private readonly string _mapPath;
    private readonly IReadOnlyCollection<string> _allTestProjects;

    /// <param name="mapPath">Path to <c>docs/ci/test-trigger-map.yml</c>.</param>
    /// <param name="allTestProjects">All matrix test project names — the universe an <c>ALL</c> selection expands to.</param>
    public TestSelector(string mapPath, IReadOnlyCollection<string> allTestProjects)
    {
        _mapPath = mapPath;
        _allTestProjects = allTestProjects;
    }

    /// <param name="changedFiles">Repo-relative, '/'-separated paths changed in the PR.</param>
    /// <param name="layer1Affected">
    /// Test projects reported by the graph tool (the union of its <em>changed</em> and
    /// <em>affected</em> sets). May be empty.
    /// </param>
    /// <param name="options">Selection overrides (kill switch).</param>
    public SelectionResult Select(
        IReadOnlyCollection<string> changedFiles,
        IReadOnlyCollection<string> layer1Affected,
        SelectorOptions options)
    {
        var map = TriggerMap.Load(_mapPath);

        var testProjects = new HashSet<string>(StringComparer.Ordinal);
        var jobs = new HashSet<string>(StringComparer.Ordinal);
        var selectsAll = false;
        string? reason = null;

        // Kill switch: a [full ci] token / run-all-tests label forces the whole matrix regardless
        // of which files changed.
        if (options.ForceAll)
        {
            selectsAll = true;
            reason = "kill switch: a [full ci] token or run-all-tests label forces the full matrix";
        }

        foreach (var file in changedFiles)
        {
            // Tracks whether ANY rule claimed this file. A src/** file claimed by nothing fails
            // open to ALL below: a missed test is a silent regression, an extra run is just slower.
            var fileMatched = false;

            // run_all catch-all (build infrastructure / broadly shared code) -> ALL.
            if (map.RunAllGlobs.Any(g => TriggerMap.GlobMatches(g, file)))
            {
                selectsAll = true;
                reason ??= $"run_all glob matched '{file}'";
                fileMatched = true;
            }

            // test_self: a change under tests/<X>/** always runs test project <X>. The map's
            // pattern is the literal placeholder "tests/<TestProject>/**", so match the shape
            // (tests/<segment>/...) directly and take the second path segment as the project.
            if (TryGetTestSelfProject(file, out var selfProject))
            {
                testProjects.Add(selfProject);
                fileMatched = true;
            }

            foreach (var rule in map.AllPathRules())
            {
                if (rule.Paths.Any(g => TriggerMap.GlobMatches(g, file)))
                {
                    ApplyTargets(rule.Targets, map, testProjects, jobs, ref selectsAll, ref reason, file);
                    fileMatched = true;
                }
            }

            foreach (var job in map.CuratedJobs)
            {
                if (job.Paths.Any(g => TriggerMap.GlobMatches(g, file)))
                {
                    ApplyTargets(new[] { job.Target }, map, testProjects, jobs, ref selectsAll, ref reason, file);
                    fileMatched = true;
                }
            }

            // Fail-open: an unmapped src/** file must still run everything.
            if (!fileMatched && file.StartsWith("src/", StringComparison.Ordinal))
            {
                selectsAll = true;
                reason ??= $"fail-open: src file '{file}' matched no rule";
            }
        }

        // Layer 1: the graph tool's affected/changed test projects are always part of the answer.
        foreach (var project in layer1Affected)
        {
            testProjects.Add(project);
        }

        if (selectsAll)
        {
            // ALL = full matrix + all jobs. Replace any partial set so the result is exactly the
            // universe the caller passed in (the matrix and the map's full job vocabulary).
            return new SelectionResult(
                SelectsAll: true,
                TestProjects: new HashSet<string>(_allTestProjects, StringComparer.Ordinal),
                Jobs: new HashSet<string>(map.AllJobTokens(), StringComparer.Ordinal),
                EscalationReason: reason ?? "full matrix selected");
        }

        return new SelectionResult(
            SelectsAll: false,
            TestProjects: testProjects,
            Jobs: jobs,
            EscalationReason: null);
    }

    private static void ApplyTargets(
        IEnumerable<string> targets,
        TriggerMap map,
        HashSet<string> testProjects,
        HashSet<string> jobs,
        ref bool selectsAll,
        ref string? reason,
        string file)
    {
        foreach (var target in targets)
        {
            if (target == "ALL")
            {
                selectsAll = true;
                reason ??= $"a rule matching '{file}' selects ALL";
            }
            else if (map.Aliases.TryGetValue(target, out var members))
            {
                foreach (var member in members)
                {
                    testProjects.Add(StripTestPrefix(member));
                }
            }
            else if (target.StartsWith("test:", StringComparison.Ordinal))
            {
                testProjects.Add(StripTestPrefix(target));
            }
            else if (target.StartsWith("job:", StringComparison.Ordinal))
            {
                jobs.Add(target);
            }
        }
    }

    // tests/<TestProject>/<more> -> <TestProject>. Returns false for paths outside tests/ or with
    // no file under a project folder (e.g. a bare "tests/X" with no trailing segment).
    private static bool TryGetTestSelfProject(string file, out string project)
    {
        project = "";
        if (!file.StartsWith("tests/", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = file.Split('/');
        if (parts.Length < 3 || parts[1].Length == 0)
        {
            return false;
        }

        project = parts[1];
        return true;
    }

    private static string StripTestPrefix(string target) =>
        target.StartsWith("test:", StringComparison.Ordinal) ? target["test:".Length..] : target;
}
