// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.SelectTests;

/// <summary>
/// Options that override normal selection.
/// </summary>
/// <param name="ForceAll">
/// The kill switch: the <c>run-full-ci</c> PR label (or a non-PR build with no diff base) forces the
/// whole matrix to run regardless of which files changed.
/// </param>
public sealed record SelectorOptions(bool ForceAll = false);

/// <summary>
/// Why a single test project or job was selected — the trigger that pulled it in, so the PR comment
/// and step summary can explain the selection instead of listing bare names. One selected item can
/// have several causes (e.g. a job hit directly by a changed file <em>and</em> derived from a
/// selected test); the selector records all of them.
/// </summary>
public enum CauseKind
{
    /// <summary>A changed file matched a <c>conventions</c> capture pattern (<see cref="Cause.Trigger"/> is the file).</summary>
    Convention,

    /// <summary>A changed file matched a <c>path_rules</c> glob (<see cref="Cause.Trigger"/> is the file; <see cref="Cause.Reason"/> is the rule's <c>reason</c>).</summary>
    PathRule,

    /// <summary>An affected production project matched an <c>affected_project_rules</c> glob (<see cref="Cause.Trigger"/> is the project name).</summary>
    AffectedProject,

    /// <summary>The Layer 1 MSBuild graph marked this test project affected by a changed source file (<see cref="Cause.Trigger"/> is the project name).</summary>
    Layer1Graph,

    /// <summary>A selected test project pulled this in via <c>derived_targets</c> (<see cref="Cause.Trigger"/> is the triggering test project).</summary>
    DerivedFromTest,
}

/// <summary>
/// A single reason a test project or job was selected.
/// </summary>
/// <param name="Kind">Which selection mechanism fired.</param>
/// <param name="Trigger">
/// The thing that triggered it: a changed file path, an affected project name, or — for
/// <see cref="CauseKind.DerivedFromTest"/> — the selected test project that pulled this in.
/// </param>
/// <param name="Reason">The curated <c>reason</c> string from the map rule, when the rule carries one.</param>
/// <param name="Path">
/// The full decision path that led to the selection, when one is available. For
/// <see cref="CauseKind.Layer1Graph"/> this is the seed changed file followed by the
/// reverse-dependency project chain (e.g. <c>["src/Foo/Bar.cs", "Aspire.Foo", "Aspire.Mid",
/// "Aspire.Mid.Tests"]</c>), so the summary can show HOW the change reached the test. Null for causes
/// that have no multi-hop path (a direct file/rule match is already fully described by
/// <see cref="Trigger"/>).
/// </param>
public sealed record Cause(CauseKind Kind, string Trigger, string? Reason = null, IReadOnlyList<string>? Path = null);

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
/// <param name="TestCauses">
/// Per-selected-test-project attribution: why each entry of <see cref="TestProjects"/> was selected.
/// Empty when <see cref="SelectsAll"/> is true (the whole matrix runs; <see cref="EscalationReason"/>
/// is the single explanation).
/// </param>
/// <param name="JobCauses">
/// Per-selected-job attribution (keyed by the full <c>job:</c> token): why each entry of
/// <see cref="Jobs"/> was selected. Empty when <see cref="SelectsAll"/> is true.
/// </param>
public sealed record SelectionResult(
    bool SelectsAll,
    IReadOnlySet<string> TestProjects,
    IReadOnlySet<string> Jobs,
    string? EscalationReason,
    IReadOnlySet<string> UnmatchedFiles,
    IReadOnlyDictionary<string, IReadOnlyList<Cause>> TestCauses,
    IReadOnlyDictionary<string, IReadOnlyList<Cause>> JobCauses);

/// <summary>
/// Filters the full CI matrix down to the subset relevant to a PR's changed files, using the
/// curated <c>eng/github-ci/test-trigger-map.yml</c> (Layer 2) unioned with a graph-derived affected set
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

    /// <param name="mapPath">Path to <c>eng/github-ci/test-trigger-map.yml</c>.</param>
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
    /// <param name="layer1AttributedPaths">
    /// The changed repo-relative paths the Layer 1 graph actually attributed to a project
    /// (<see cref="AffectedResult.AttributedPaths"/>). Such a file is Layer-1-owned even when it is not
    /// under a project directory (e.g. a link-compiled <c>src/Shared</c> file), so it does not trip the
    /// run-all fallback. Empty when Layer 1 did not run (<c>--skip-layer1</c> / <c>--force-all</c>).
    /// </param>
    /// <param name="layer1Paths">
    /// Per-affected-project decision paths from Layer 1 (<see cref="AffectedResult.Paths"/>), keyed by
    /// project base name. When a selected test project has an entry, its <see cref="CauseKind.Layer1Graph"/>
    /// cause carries the seed file + reverse-dependency chain so the summary can show the full path. Null
    /// when Layer 1 did not run or produced no paths.
    /// </param>
    public SelectionResult Select(
        IReadOnlyCollection<string> changedFiles,
        IReadOnlyCollection<string> layer1Affected,
        SelectorOptions options,
        IReadOnlySet<string>? layer1AttributedPaths = null,
        IReadOnlyDictionary<string, AffectedPath>? layer1Paths = null)
    {
        var map = TriggerMap.Load(_mapPath);
        var attributedPaths = layer1AttributedPaths ?? new HashSet<string>(StringComparer.Ordinal);

        // name -> the reasons it was selected. The key set IS the selected set; the lists carry the
        // attribution surfaced in the PR comment / step summary.
        var testCauses = new Dictionary<string, List<Cause>>(StringComparer.Ordinal);
        var jobCauses = new Dictionary<string, List<Cause>>(StringComparer.Ordinal);
        var unmatchedFiles = new HashSet<string>(StringComparer.Ordinal);
        var selectsAll = false;
        string? reason = null;

        // Kill switch: the run-full-ci label forces the whole matrix regardless of which files changed.
        if (options.ForceAll)
        {
            selectsAll = true;
            reason = "kill switch: the run-full-ci label forces the full matrix";
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
                        AddCause(testCauses, project, new Cause(CauseKind.Convention, file));
                        fileMatched = true;
                    }
                }
            }

            // path_rules: a glob set -> a target set (test: / job: / group / ALL).
            foreach (var rule in map.PathRules)
            {
                if (rule.Paths.Any(g => TriggerMap.GlobMatches(g, file)))
                {
                    ApplyTargets(rule.Targets, map, testCauses, jobCauses, ref selectsAll, ref reason,
                        new Cause(CauseKind.PathRule, file, rule.Reason));
                    fileMatched = true;
                }
            }

            // ignore: files Layer 2 deliberately accounts for with no target (Layer 1 covers them, or
            // they are inert). They must not trigger the run-all fallback below.
            var ignored = map.Ignore.Any(g => TriggerMap.GlobMatches(g, file));

            // Layer-1-owned: either the graph actually attributed this changed file to a project
            // (the authoritative signal — covers link-compiled src/Shared / tests/Shared / Components
            // /Common files that are NOT under any project directory), or the file sits under a project
            // directory in Aspire.slnx (the directory heuristic, which also covers deleted files and the
            // old side of a cross-project rename that the graph index can no longer see at HEAD). Either
            // way the file relies on Layer 1 and must never force ALL.
            var layer1Owned = attributedPaths.Contains(file) || IsLayer1Owned(file);

            if (fileMatched || ignored || layer1Owned)
            {
                // Accounted for by some layer; nothing more to do for this file.
                continue;
            }

            // A true leftover: matched by no Layer 2 rule, not ignored, not a graph-owned project file,
            // and not dropped by the prefilter (which already removes changes that need no CI at all,
            // e.g. docs). Fail safe and force the full matrix: a missed test is a silent regression, an
            // extra full run is merely slower. This covers a new shared source dir, a new top-level
            // directory, or any loose dependency nobody has mapped yet -- the kind of change made
            // without knowing the selector exists. The file is also reported in the audit summary so a
            // curated rule (or a prefilter entry, if it truly needs no CI) can narrow it later.
            unmatchedFiles.Add(file);
            selectsAll = true;
            reason ??= $"run-all fallback: '{file}' is neither Layer-1-owned nor matched by a Layer 2 rule";
        }

        // Layer 1: the graph tool reports the full affected set (production + test projects). The
        // affected TEST projects are always part of the answer; the production names drive
        // project_rules below.
        foreach (var project in layer1Affected)
        {
            if (_allTestProjects.Contains(project))
            {
                // Attach the graph decision path (seed file + reverse-dependency chain) when Layer 1
                // produced one, so the summary can render HOW the change reached this test.
                IReadOnlyList<string>? path = null;
                if (layer1Paths is not null && layer1Paths.TryGetValue(project, out var affectedPath))
                {
                    path = BuildLayer1CausePath(affectedPath);
                }

                AddCause(testCauses, project, new Cause(CauseKind.Layer1Graph, project, Path: path));
            }
        }

        // affected_project_rules: an affected PRODUCTION project (matched by name glob) pulls in
        // jobs/tests. This replaces the duplicated src/<Project>/** path globs the job rules used to
        // carry, and follows the graph's transitive closure (a dependency change marks the project
        // affected). Keyed on the affected-project set, so it contributes nothing when Layer 1
        // produced none (e.g. --skip-layer1) -- the path_rules still cover the loose-file triggers.
        //
        // Match ONLY production project names: Layer 1 reports production AND test projects, and the
        // affected test projects are already selected via the intersection above. Without this filter
        // an affected matrix test name (e.g. "Aspire.Hosting.Python.Tests") would match a production
        // glob like "Aspire.Hosting*" and spuriously fire production jobs (ats-diffs / extension-e2e /
        // typescript-api-compat / deployment-e2e) for a TEST-ONLY change. See test-trigger-map.yml's
        // affected_project_rules comment ("matched against the affected PRODUCTION projects").
        var affectedProductionProjects = layer1Affected
            .Where(name => !_allTestProjects.Contains(name))
            .ToList();
        foreach (var rule in map.AffectedProjectRules)
        {
            // Attribute the rule to the first affected project that matched it, so the cause names a
            // concrete project rather than the rule's whole glob set.
            var matchedProject = affectedProductionProjects.FirstOrDefault(name => rule.Projects.Any(p => TriggerMap.ProjectNameMatches(p, name)));
            if (matchedProject is not null)
            {
                ApplyTargets(rule.Targets, map, testCauses, jobCauses, ref selectsAll, ref reason,
                    new Cause(CauseKind.AffectedProject, matchedProject, rule.Reason));
            }
        }

        if (selectsAll)
        {
            return SelectsAllResult(reason, unmatchedFiles);
        }

        // derived_targets: a selected test project (from Layer 1 or Layer 2) can pull in extra
        // jobs/tests. Iterate to a fixpoint so a test->test edge whose target has its own derived
        // rule is followed; a no-growth pass terminates (cycle-safe).
        ApplyDerivedTargets(map, testCauses, jobCauses, ref selectsAll, ref reason);

        if (selectsAll)
        {
            return SelectsAllResult(reason, unmatchedFiles);
        }

        return new SelectionResult(
            SelectsAll: false,
            TestProjects: testCauses.Keys.ToHashSet(StringComparer.Ordinal),
            Jobs: jobCauses.Keys.ToHashSet(StringComparer.Ordinal),
            EscalationReason: null,
            UnmatchedFiles: unmatchedFiles,
            TestCauses: Freeze(testCauses),
            JobCauses: Freeze(jobCauses));

        // ALL = full matrix + all jobs. Replace any partial set so the result is exactly the universe
        // the caller passed in (the matrix and the map's full job vocabulary). Per-item causes are not
        // tracked under ALL: the whole matrix runs and EscalationReason is the single explanation.
        SelectionResult SelectsAllResult(string? escalationReason, IReadOnlySet<string> unmatched) =>
            new(
                SelectsAll: true,
                TestProjects: new HashSet<string>(_allTestProjects, StringComparer.Ordinal),
                Jobs: new HashSet<string>(map.AllJobTokens(), StringComparer.Ordinal),
                EscalationReason: escalationReason ?? "full matrix selected",
                UnmatchedFiles: unmatched,
                TestCauses: s_emptyCauses,
                JobCauses: s_emptyCauses);
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Cause>> s_emptyCauses =
        new Dictionary<string, IReadOnlyList<Cause>>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<Cause>> Freeze(Dictionary<string, List<Cause>> causes) =>
        causes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Cause>)kv.Value, StringComparer.Ordinal);

    // Builds the rendered decision path for a Layer 1 cause: the seed changed file followed by the
    // reverse-dependency project chain (directly-changed project ... affected test). The result reads
    // left-to-right as "this file changed this project, which is depended on by ..., reaching this test".
    private static IReadOnlyList<string> BuildLayer1CausePath(AffectedPath affectedPath)
    {
        var path = new List<string>(affectedPath.ProjectChain.Count + 1) { affectedPath.ChangedFile };
        path.AddRange(affectedPath.ProjectChain);
        return path;
    }

    // Records one reason a test project / job was selected, de-duplicating identical causes (e.g. the
    // same derived rule re-fires across fixpoint passes).
    private static void AddCause(Dictionary<string, List<Cause>> causes, string key, Cause cause)
    {
        if (!causes.TryGetValue(key, out var list))
        {
            list = new List<Cause>();
            causes[key] = list;
        }

        if (!list.Contains(cause))
        {
            list.Add(cause);
        }
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
        Dictionary<string, List<Cause>> testCauses,
        Dictionary<string, List<Cause>> jobCauses,
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
            var beforeTests = testCauses.Count;
            var beforeJobs = jobCauses.Count;

            foreach (var derived in map.DerivedTargets)
            {
                // If ANY of the rule's triggering tests is selected, add its targets and attribute
                // them to the (first) selected triggering test, so the cause reads "via test X".
                var triggeringTest = derived.Tests
                    .Select(StripTestPrefix)
                    .FirstOrDefault(testCauses.ContainsKey);
                if (triggeringTest is not null)
                {
                    ApplyTargets(derived.Targets, map, testCauses, jobCauses, ref selectsAll, ref reason,
                        new Cause(CauseKind.DerivedFromTest, triggeringTest, derived.Reason));
                }
            }

            changed = testCauses.Count != beforeTests || jobCauses.Count != beforeJobs;
        }
    }

    private static void ApplyTargets(
        IEnumerable<string> targets,
        TriggerMap map,
        Dictionary<string, List<Cause>> testCauses,
        Dictionary<string, List<Cause>> jobCauses,
        ref bool selectsAll,
        ref string? reason,
        Cause cause)
    {
        var localSelectsAll = selectsAll;
        string? localReason = reason;

        foreach (var target in targets)
        {
            AddTarget(target, map, testCauses, jobCauses, ref localSelectsAll, ref localReason, cause, visitedGroups: null);
        }

        selectsAll = localSelectsAll;
        reason = localReason;
    }

    // Routes a single target into the result sets. Group names expand recursively (a group member
    // may itself be a group name), tracking visited groups so a cyclic group reference terminates.
    // The cause propagates unchanged to every test/job leaf the target expands to.
    private static void AddTarget(
        string target,
        TriggerMap map,
        Dictionary<string, List<Cause>> testCauses,
        Dictionary<string, List<Cause>> jobCauses,
        ref bool selectsAll,
        ref string? reason,
        Cause cause,
        HashSet<string>? visitedGroups)
    {
        if (target == "ALL")
        {
            selectsAll = true;
            reason ??= $"a rule matching '{cause.Trigger}' selects ALL";
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
                AddTarget(member, map, testCauses, jobCauses, ref selectsAll, ref reason, cause, visitedGroups);
            }
        }
        else if (target.StartsWith("test:", StringComparison.Ordinal))
        {
            AddCause(testCauses, StripTestPrefix(target), cause);
        }
        else if (target.StartsWith("job:", StringComparison.Ordinal))
        {
            AddCause(jobCauses, target, cause);
        }
    }

    private static string StripTestPrefix(string target) =>
        target.StartsWith("test:", StringComparison.Ordinal) ? target["test:".Length..] : target;
}
