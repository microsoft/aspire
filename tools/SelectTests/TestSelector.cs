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
/// True when the whole test matrix must run (a path rule whose target is <c>ALL</c>, a fail-open
/// escalation, or the kill switch). When true, <see cref="TestProjects"/> is the full matrix.
/// </param>
/// <param name="TestProjects">The selected test project names (matrix <c>projectName</c>), aliases expanded.</param>
/// <param name="Jobs">The selected non-.NET jobs (e.g. <c>job:polyglot</c>, <c>job:extension-e2e</c>).</param>
/// <param name="EscalationReason">When <see cref="SelectsAll"/> is true, a short human-readable reason.</param>
/// <param name="UnmatchedFiles">
/// Changed files that matched <em>no</em> curated map rule (Layer 2). After the trim, normal
/// <c>src</c> files are expected here (Layer 1 / the affected-projects graph owns the project closure),
/// so a consumer that wants the "neither layer" set subtracts the files Layer 1 attributed. The
/// raw set is still the early-warning signal for a loose, non-project dependency that needs a
/// curated rule.
/// </param>
public sealed record SelectionResult(
    bool SelectsAll,
    IReadOnlySet<string> TestProjects,
    IReadOnlySet<string> Jobs,
    string? EscalationReason,
    IReadOnlySet<string> UnmatchedFiles);

/// <summary>
/// Filters the full CI matrix down to the subset relevant to a PR's changed files, using the
/// curated <c>eng/test-trigger-map.yml</c> (Layer 2) unioned with a graph-derived affected set
/// (Layer 1, from <see cref="GraphAffectedProjects"/>, supplied to <see cref="Select"/>).
/// </summary>
/// <remarks>
/// Behavior is specified by the acceptance tests in
/// <c>Infrastructure.Tests/TestTriggerMap/SelectTestsAcceptanceTests.cs</c>.
/// </remarks>
public sealed class TestSelector
{
    private readonly string _mapPath;
    private readonly IReadOnlyCollection<string> _allTestProjects;
    private readonly IReadOnlyCollection<string> _projectDirectories;

    /// <param name="mapPath">Path to <c>eng/test-trigger-map.yml</c>.</param>
    /// <param name="allTestProjects">All matrix test project names — the universe an <c>ALL</c> selection expands to.</param>
    /// <param name="projectDirectories">
    /// Repo-relative, '/'-separated directories of every project in <c>Aspire.slnx</c> (the universe
    /// the Layer 1 graph walks). Used to decide whether a changed file is "Layer-1-owned": a file
    /// under one of these dirs is attributed by the graph, so it never triggers the run-all
    /// fallback. May be empty (then no file is treated as owned).
    /// </param>
    public TestSelector(
        string mapPath,
        IReadOnlyCollection<string> allTestProjects,
        IReadOnlyCollection<string> projectDirectories)
    {
        _mapPath = mapPath;
        _allTestProjects = allTestProjects;
        _projectDirectories = projectDirectories;
    }

    /// <param name="changedFiles">Repo-relative, '/'-separated paths changed in the PR.</param>
    /// <param name="layer1Affected">
    /// The full affected project set reported by the graph tool — production <em>and</em> test
    /// project names (the union of its <em>changed</em> and <em>affected</em> sets). Test names are
    /// intersected with the matrix and selected; production names drive <c>project_rules</c>. May be
    /// empty.
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
        var unmatchedFiles = new HashSet<string>(StringComparer.Ordinal);
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
            // Tracks whether a Layer 2 rule added targets for this file. Combined below with
            // "ignored" and "Layer-1-owned" to decide whether the file is a true leftover.
            var fileMatched = false;

            // conventions: a <name>-capture pattern -> target template, emitted only when the
            // derived test project exists in the matrix (existence guard). Additive. Covers a test
            // project's own folder (tests/<name>/**) and the Hosting/Components integration dirs.
            foreach (var convention in map.Conventions)
            {
                if (TriggerMap.TryExpandConvention(convention, file, out var target) &&
                    target.StartsWith("test:", StringComparison.Ordinal))
                {
                    var project = StripTestPrefix(target);
                    if (_allTestProjects.Contains(project))
                    {
                        testProjects.Add(project);
                        fileMatched = true;
                    }
                }
            }

            // path_rules: a glob set -> a target set (test: / job: / group / ALL).
            foreach (var rule in map.PathRules)
            {
                if (rule.Paths.Any(g => TriggerMap.GlobMatches(g, file)))
                {
                    ApplyTargets(rule.Targets, map, testProjects, jobs, ref selectsAll, ref reason, file);
                    fileMatched = true;
                }
            }

            // ignore: files Layer 2 deliberately accounts for with no target (Layer 1 covers them, or
            // they are inert). They must not trigger the run-all fallback below.
            var ignored = map.Ignore.Any(g => TriggerMap.GlobMatches(g, file));

            // Layer-1-owned: the file sits under a project dir in Aspire.slnx, so dotnet-affected
            // attributes it. Such files rely on Layer 1 and never force ALL.
            var layer1Owned = IsLayer1Owned(file);

            if (fileMatched || ignored || layer1Owned)
            {
                // Accounted for by some layer; nothing more to do for this file.
                continue;
            }

            // A true leftover: no Layer 2 rule, not ignored, not a graph-owned project file. Under
            // src/** this is the run-all fallback (a missed test is a silent regression; an extra run
            // is just slower) -- typically a new shared source dir nobody mapped. Outside src/** it is
            // only an audit signal (a loose, non-project dependency that may need a curated rule).
            unmatchedFiles.Add(file);
            if (file.StartsWith("src/", StringComparison.Ordinal))
            {
                selectsAll = true;
                reason ??= $"run-all fallback: src file '{file}' is neither Layer-1-owned nor matched by a Layer 2 rule";
            }
        }

        // Layer 1: the graph tool reports the full affected set (production + test projects). The
        // affected TEST projects are always part of the answer; the production names drive
        // project_rules below.
        foreach (var project in layer1Affected)
        {
            if (_allTestProjects.Contains(project))
            {
                testProjects.Add(project);
            }
        }

        // affected_project_rules: an affected PRODUCTION project (matched by name glob) pulls in
        // jobs/tests. This replaces the duplicated src/<Project>/** path globs the job rules used to
        // carry, and follows the graph's transitive closure (a dependency change marks the project
        // affected). Keyed on the affected-project set, so it contributes nothing when Layer 1
        // produced none (e.g. --skip-layer1) -- the path_rules still cover the loose-file triggers.
        foreach (var rule in map.AffectedProjectRules)
        {
            if (layer1Affected.Any(name => rule.Projects.Any(p => TriggerMap.ProjectNameMatches(p, name))))
            {
                ApplyTargets(rule.Targets, map, testProjects, jobs, ref selectsAll, ref reason, "(affected project)");
            }
        }

        if (selectsAll)
        {
            // ALL = full matrix + all jobs. Replace any partial set so the result is exactly the
            // universe the caller passed in (the matrix and the map's full job vocabulary).
            return new SelectionResult(
                SelectsAll: true,
                TestProjects: new HashSet<string>(_allTestProjects, StringComparer.Ordinal),
                Jobs: new HashSet<string>(map.AllJobTokens(), StringComparer.Ordinal),
                EscalationReason: reason ?? "full matrix selected",
                UnmatchedFiles: unmatchedFiles);
        }

        // derived_targets: a selected test project (from Layer 1 or Layer 2) can pull in extra
        // jobs/tests. Iterate to a fixpoint so a test->test edge whose target has its own derived
        // rule is followed; a no-growth pass terminates (cycle-safe).
        ApplyDerivedTargets(map, testProjects, jobs, ref selectsAll, ref reason);

        if (selectsAll)
        {
            return new SelectionResult(
                SelectsAll: true,
                TestProjects: new HashSet<string>(_allTestProjects, StringComparer.Ordinal),
                Jobs: new HashSet<string>(map.AllJobTokens(), StringComparer.Ordinal),
                EscalationReason: reason ?? "full matrix selected",
                UnmatchedFiles: unmatchedFiles);
        }

        return new SelectionResult(
            SelectsAll: false,
            TestProjects: testProjects,
            Jobs: jobs,
            EscalationReason: null,
            UnmatchedFiles: unmatchedFiles);
    }

    // A changed file is "Layer-1-owned" when it lives under a project directory in Aspire.slnx -- the
    // Layer 1 graph then attributes it to that project, so it does not need the run-all fallback.
    private bool IsLayer1Owned(string file)
    {
        foreach (var dir in _projectDirectories)
        {
            var prefix = dir.EndsWith('/') ? dir : dir + "/";
            if (file.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Applies derived_targets to the selected test set until it stabilises. Each pass adds the
    // targets of every derived rule whose keyed test is currently selected; a pass that adds nothing
    // ends the loop (so cycles such as A->B, B->A terminate).
    private static void ApplyDerivedTargets(
        TriggerMap map,
        HashSet<string> testProjects,
        HashSet<string> jobs,
        ref bool selectsAll,
        ref string? reason)
    {
        if (map.DerivedTargets.Count == 0)
        {
            return;
        }

        var changed = true;
        while (changed && !selectsAll)
        {
            var beforeTests = testProjects.Count;
            var beforeJobs = jobs.Count;

            foreach (var derived in map.DerivedTargets)
            {
                // If ANY of the rule's triggering tests is selected, add its targets.
                if (derived.Tests.Any(t => testProjects.Contains(StripTestPrefix(t))))
                {
                    ApplyTargets(derived.Targets, map, testProjects, jobs, ref selectsAll, ref reason, derived.Tests.FirstOrDefault() ?? "");
                }
            }

            changed = testProjects.Count != beforeTests || jobs.Count != beforeJobs;
        }
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
        var localSelectsAll = selectsAll;
        string? localReason = reason;

        foreach (var target in targets)
        {
            AddTarget(target, map, testProjects, jobs, ref localSelectsAll, ref localReason, file, visitedGroups: null);
        }

        selectsAll = localSelectsAll;
        reason = localReason;
    }

    // Routes a single target into the result sets. Group names expand recursively (a group member
    // may itself be a group name), tracking visited groups so a cyclic group reference terminates.
    private static void AddTarget(
        string target,
        TriggerMap map,
        HashSet<string> testProjects,
        HashSet<string> jobs,
        ref bool selectsAll,
        ref string? reason,
        string file,
        HashSet<string>? visitedGroups)
    {
        if (target == "ALL")
        {
            selectsAll = true;
            reason ??= $"a rule matching '{file}' selects ALL";
        }
        else if (map.Groups.TryGetValue(target, out var members))
        {
            visitedGroups ??= new HashSet<string>(StringComparer.Ordinal);
            if (!visitedGroups.Add(target))
            {
                // Already expanding this group higher in the recursion: a cycle. Stop.
                return;
            }

            foreach (var member in members)
            {
                AddTarget(member, map, testProjects, jobs, ref selectsAll, ref reason, file, visitedGroups);
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

    private static string StripTestPrefix(string target) =>
        target.StartsWith("test:", StringComparison.Ordinal) ? target["test:".Length..] : target;
}
