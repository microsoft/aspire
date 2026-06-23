// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.SelectTests;

// Entry point for the selective-CI tool. Runs BEFORE enumerate-tests and computes the subset of
// test projects (and the non-.NET jobs) relevant to a PR's changed files, by unioning:
//   Layer 1 — the MSBuild ProjectGraph reverse-dependency closure (GraphAffectedProjects), and
//   Layer 2 — the curated eng/github-ci/test-trigger-map.yml resolved by TestSelector.
// With --enforce and a non-ALL selection it writes an OverrideProjectToBuild props file
// (--before-build-props) so the subsequent enumerate-tests `-test` build enumerates ONLY the
// selected projects. In audit mode (no --enforce) it writes the run_* job booleans and an advisory
// "would-have-been-skipped" summary but no props, so enumerate-tests runs the full matrix unchanged.
// See docs/ci/test-trigger-selector-design.md.

var repoRootOption = new Option<string>("--repo-root")
{
    Description = "Repository root (where .git lives).",
    DefaultValueFactory = _ => Directory.GetCurrentDirectory()
};

var mapOption = new Option<string?>("--map")
{
    Description = "Path to eng/github-ci/test-trigger-map.yml. Defaults to <repo-root>/eng/github-ci/test-trigger-map.yml."
};

var slnxOption = new Option<string?>("--slnx")
{
    Description = "Path to the solution that defines the project universe. Defaults to <repo-root>/Aspire.slnx."
};

var fromOption = new Option<string?>("--from")
{
    Description = "Base git ref to diff from (e.g. the PR base SHA). Required unless --changed-files is given."
};

var toOption = new Option<string?>("--to")
{
    Description = "Head git ref to diff to. Defaults to the working tree when --from is given without --to."
};

var changedFilesOption = new Option<string?>("--changed-files")
{
    Description = "Path to a newline-delimited list of changed repo-relative paths (instead of --from/--to)."
};

var skipLayer1Option = new Option<bool>("--skip-layer1")
{
    Description = "Skip the Layer 1 graph closure (Layer 2 / curated map only)."
};

var forceAllOption = new Option<bool>("--force-all")
{
    Description = "Kill switch: force the full matrix regardless of changed files."
};

var enforceOption = new Option<bool>("--enforce")
{
    Description = "Restrict the build to the selected projects (writes --before-build-props). Without this " +
                  "(audit mode), no props are written and enumerate-tests runs the full matrix unchanged."
};

var beforeBuildPropsOption = new Option<string?>("--before-build-props")
{
    Description = "Where to write the OverrideProjectToBuild props consumed by eng/Build.props " +
                  "($(BeforeBuildPropsPath)). Written only with --enforce and a non-ALL selection; " +
                  "otherwise nothing is written so enumerate-tests enumerates everything."
};

var rootCommand = new RootCommand("Select the relevant CI test subset for a PR's changed files.");
foreach (var option in new Option[]
{
    repoRootOption, mapOption, slnxOption, fromOption, toOption, changedFilesOption,
    skipLayer1Option, forceAllOption, enforceOption, beforeBuildPropsOption
})
{
    rootCommand.Options.Add(option);
}

rootCommand.SetAction(parseResult =>
{
    var repoRoot = Path.GetFullPath(parseResult.GetValue(repoRootOption)!);
    var mapPath = parseResult.GetValue(mapOption)
        ?? Path.Combine(repoRoot, "eng", "github-ci", "test-trigger-map.yml");
    var slnxPath = parseResult.GetValue(slnxOption)
        ?? Path.Combine(repoRoot, "Aspire.slnx");
    var from = parseResult.GetValue(fromOption);
    var to = parseResult.GetValue(toOption);
    var changedFilesPath = parseResult.GetValue(changedFilesOption);
    var skipLayer1 = parseResult.GetValue(skipLayer1Option);
    var forceAll = parseResult.GetValue(forceAllOption);
    var enforce = parseResult.GetValue(enforceOption);
    var beforeBuildProps = parseResult.GetValue(beforeBuildPropsOption);

    return Selection.Run(new RunOptions(
        repoRoot, mapPath, slnxPath, from, to, changedFilesPath,
        skipLayer1, forceAll, enforce, beforeBuildProps));
});

return rootCommand.Parse(args).Invoke();

internal sealed record RunOptions(
    string RepoRoot,
    string MapPath,
    string SlnxPath,
    string? From,
    string? To,
    string? ChangedFilesPath,
    bool SkipLayer1,
    bool ForceAll,
    bool Enforce,
    string? BeforeBuildProps);

internal static class Selection
{
    public static int Run(RunOptions options)
    {
        var trace = new SelectionTrace();
        try
        {
            return RunCore(options, trace);
        }
        catch (Exception ex)
        {
            // Augment the failure with WHAT it was processing and HOW to re-run it, then rethrow so the
            // CI step still exits non-zero -- a crash must never be downgraded to a silent under-select.
            WriteFailureDiagnostics(options, trace, ex);
            throw;
        }
    }

    private static int RunCore(RunOptions options, SelectionTrace trace)
    {
        // The universe an ALL selection expands to, and the existence guard for test: targets and
        // Layer 1 affected test projects: the test projects in Aspire.slnx (tests/<Name>/<Name>.csproj
        // with a .Tests suffix). Derived from the slnx -- NOT from an enumerated matrix -- because the
        // selector now runs BEFORE enumerate-tests. Maps each test project name to its repo-relative
        // .csproj path so a selected name can be written as an OverrideProjectToBuild item.
        trace.EnterStage("load test projects from slnx");
        var testProjectsByName = LoadTestProjects(options.SlnxPath);
        var allTestProjects = testProjectsByName.Keys.ToHashSet(StringComparer.Ordinal);

        // The prefilter (the map's `prefilter` block): read the CI skip-gate patterns file at runtime
        // and drop matching changed files before BOTH layers, except the keep_routed carve-outs. So an
        // excluded file influences no selection. See ChangedFileFilter for why this must gate Layer 1 too.
        trace.EnterStage("load trigger map and prefilter");
        var changedFileFilter = ChangedFileFilter.Create(options.RepoRoot, TriggerMap.Load(options.MapPath).Prefilter);

        // Under --force-all the selector returns ALL regardless of the diff (see below), so skip
        // resolving changed files and the Layer 1 graph closure entirely. Resolving them is not just
        // wasted work: --force-all is the path taken when there is no usable diff base (or the
        // run-full-ci label kill switch fired), so ResolveChangedFiles would otherwise throw for lack of a
        // --from/--changed-files input.
        trace.EnterStage("resolve changed files");
        var rawChangedFiles = options.ForceAll
            ? Array.Empty<string>()
            : ResolveChangedFiles(options);

        // Split the raw change set into excluded (reported for audit transparency) and the filtered
        // set that actually drives Layer 2. RunLayer1 applies the same filter to Layer 1's own git
        // diff, so both layers see the identical post-prefilter change set.
        var excludedFiles = rawChangedFiles
            .Where(changedFileFilter.IsExcluded)
            .ToList();
        var changedFiles = rawChangedFiles
            .Where(f => !changedFileFilter.IsExcluded(f))
            .ToList();

        trace.EnterStage("compute Layer 1 affected-projects graph");
        var layer1 = (options.ForceAll || options.SkipLayer1)
            ? AffectedResult.Empty
            : RunLayer1(options, changedFileFilter, trace);
        var layer1Affected = layer1.AffectedProjects;

        // When Layer 1 is skipped there is no graph attribution, so the project-directory set must be
        // empty too. Otherwise TestSelector would treat files under those dirs as "Layer-1-owned" and
        // suppress the run-all fallback even though nothing attributed them -- a silent under-selection.
        var projectDirectories = options.SkipLayer1
            ? Array.Empty<string>()
            : LoadProjectDirectories(options.SlnxPath);

        trace.EnterStage("select (Layer 2 trigger map + Layer 1 union)");
        var selector = new TestSelector(options.MapPath, allTestProjects, projectDirectories);
        var result = selector.Select(changedFiles, layer1Affected, new SelectorOptions(options.ForceAll), layer1.AttributedPaths, layer1.Paths);

        trace.EnterStage("write summary and outputs");
        WriteSummary(options, result, allTestProjects, changedFiles, layer1Affected, excludedFiles);
        WriteJobBooleans(options, result);
        WriteSelectionComment(options, result, allTestProjects, changedFiles);
        WriteSelectionJson(options, result, allTestProjects, changedFiles, layer1Affected, excludedFiles);

        // Enforce + a non-ALL selection restricts the downstream enumerate-tests build to the selected
        // test projects via an OverrideProjectToBuild props file. A selection with ZERO buildable test
        // projects (e.g. an extension-only / polyglot-only change whose only targets are non-.NET jobs)
        // must NOT write an empty restriction: an empty ProjectToBuild makes the enumerate build fall
        // back to the whole solution (and fail on non-test tooling projects). Instead we signal
        // has_dotnet_tests=false so tests.yml skips enumerate-tests entirely and emits an empty matrix;
        // the selected Layer 2 jobs still run via the run_* booleans. In audit mode, or when the
        // selection is ALL, write nothing so enumerate-tests enumerates the full matrix unchanged.
        var buildableSelected = result.TestProjects.Count(testProjectsByName.ContainsKey);
        var restrictBuild = options.Enforce && !result.SelectsAll && options.BeforeBuildProps is not null && buildableSelected > 0;
        if (restrictBuild)
        {
            WriteBeforeBuildProps(options.BeforeBuildProps!, result.TestProjects, testProjectsByName);
        }

        // Tell the workflow whether a restriction props file was written (and where). Empty means
        // "enumerate everything" -- the workflow then omits /p:BeforeBuildPropsPath. Named after its
        // payload (an OverrideProjectToBuild item set) rather than the generic BeforeBuildPropsPath
        // hook it rides on, which other writers (ToolsetBootstrap.props, ClassModeTestProjects.props)
        // also use.
        WriteGitHubOutput("project_override_props", restrictBuild ? options.BeforeBuildProps! : "");

        // has_dotnet_tests is false only for an enforcing, non-ALL selection that selects no buildable
        // test project. tests.yml gates enumerate-tests on it: false skips the build and yields an empty
        // .NET test matrix (no test shards run) while the run_* job booleans still gate the non-.NET
        // jobs. ALL and audit always enumerate the full matrix.
        var hasDotnetTests = !options.Enforce || result.SelectsAll || buildableSelected > 0;
        WriteGitHubOutput("has_dotnet_tests", hasDotnetTests ? "true" : "false");

        return 0;
    }

    // Repo-relative, '/'-separated paths of the test projects in Aspire.slnx, keyed by project name
    // (the .csproj base name == the matrix projectName == the map's test: target). The universe is
    // the tests/<Name>/<Name>.csproj projects whose name ends in ".Tests"; the other tests/ projects
    // (Aspire.TestUtilities, TestingAppHost1, testproject, ...) are shared fixtures/helpers, not test
    // projects, and are excluded so they are never selected or enumerated on their own.
    private static IReadOnlyDictionary<string, string> LoadTestProjects(string slnxPath)
    {
        if (!File.Exists(slnxPath))
        {
            throw new InvalidOperationException($"Solution was not found: {slnxPath}");
        }

        var slnx = File.ReadAllText(slnxPath);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        // <Project Path="tests/Foo.Tests/Foo.Tests.csproj" /> -- normalize separators, keep tests/ + .Tests.
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(slnx, "Path=\"([^\"]+\\.csproj)\""))
        {
            var relPath = m.Groups[1].Value.Replace('\\', '/');
            if (!relPath.StartsWith("tests/", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(relPath);
            if (name.EndsWith(".Tests", StringComparison.Ordinal))
            {
                map[name] = relPath;
            }
        }

        return map;
    }

    // Writes the MSBuild props file that eng/Build.props imports via $(BeforeBuildPropsPath): an
    // OverrideProjectToBuild item per selected test project, which REPLACES the default ProjectToBuild
    // set so the `-test` build (and thus the canonical test-matrix enumeration) covers only these.
    // Same shape as eng/scripts/generate-specialized-test-projects-list.sh emits for quarantine/outerloop.
    private static void WriteBeforeBuildProps(
        string path,
        IReadOnlySet<string> selectedTestProjects,
        IReadOnlyDictionary<string, string> testProjectsByName)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sb = new StringBuilder();
        sb.AppendLine("<Project>");
        sb.AppendLine("  <ItemGroup>");
        foreach (var name in selectedTestProjects.OrderBy(n => n, StringComparer.Ordinal))
        {
            // A selected name not in the slnx test-project set is not a buildable test project (e.g. a
            // production project name from project_rules); it contributes no OverrideProjectToBuild item.
            if (testProjectsByName.TryGetValue(name, out var relPath))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    <OverrideProjectToBuild Include=\"$(RepoRoot){relPath}\" />");
            }
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");

        File.WriteAllText(path, sb.ToString());
    }

    // Appends a single key=value line to $GITHUB_OUTPUT (when set), so the workflow can read it as a
    // step output. Falls back to stderr for local runs.
    private static void WriteGitHubOutput(string key, string value)
    {
        var githubOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        var line = $"{key}={value}";
        if (githubOutput is not null)
        {
            File.AppendAllLines(githubOutput, new[] { line });
        }
        else
        {
            Console.Error.WriteLine(line);
        }
    }

    // Layer 2 needs the actual changed file paths (it glob-matches them), independent of the
    // project-name closure that Layer 1 produces.
    private static IReadOnlyCollection<string> ResolveChangedFiles(RunOptions options)
    {
        if (options.ChangedFilesPath is not null)
        {
            return File.ReadAllLines(options.ChangedFilesPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }

        if (options.From is null)
        {
            throw new InvalidOperationException("Provide either --changed-files or --from (with optional --to).");
        }

        // git emits forward-slash, repo-relative paths on every OS, which is exactly what the map
        // globs expect. `<from> <to>` diffs the two refs; omitting <to> diffs against the work tree.
        // --no-renames decomposes a rename into a delete (old path) + add (new path) so BOTH sides
        // are glob-matched. Without it, git's default rename detection reports only the destination,
        // so a file moved OUT of a mapped directory (e.g. eng/clipack/foo -> eng/elsewhere) would hide
        // the old path and silently skip that directory's mapped tests. Layer 1 captures both sides via
        // -M; this keeps Layer 2 consistent.
        var range = options.To is null ? new[] { options.From } : new[] { options.From, options.To };
        // -c core.quotePath=false: with the default (true), git octal-escapes and double-quotes any
        // path with non-ASCII bytes (e.g. "src/caf\303\251.cs"). That escaped string is not the real
        // repo-relative path, so the map globs below would silently miss it. Forcing quotePath off makes
        // git emit the literal UTF-8 path, which is what the globs expect. (Layer 1's diff does the same.)
        var args = new List<string> { "-c", "core.quotePath=false", "diff", "--name-only", "--no-renames" };
        args.AddRange(range);

        var stdout = RunProcess("git", args, options.RepoRoot, out var exitCode, out var stderr);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git diff failed ({exitCode}): {stderr}");
        }

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // Repo-relative, '/'-separated directories of every project in Aspire.slnx -- the universe the
    // Layer 1 graph walks. The selector treats a changed file under one of these dirs as
    // "Layer-1-owned" (attributed by the graph), so it never triggers the run-all fallback.
    private static IReadOnlyCollection<string> LoadProjectDirectories(string slnxPath)
    {
        if (!File.Exists(slnxPath))
        {
            return Array.Empty<string>();
        }

        var slnx = File.ReadAllText(slnxPath);
        // <Project Path="src/Foo/Foo.csproj" /> -- normalize separators and take the directory.
        return System.Text.RegularExpressions.Regex.Matches(slnx, "Path=\"([^\"]+\\.csproj)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Select(p => p.Contains('/', StringComparison.Ordinal) ? p[..p.LastIndexOf('/')] : p)
            .Where(d => d.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    // Layer 1: build the MSBuild ProjectGraph from Aspire.slnx (HEAD-only) and report every project
    // hit by the diff — the union of *changed* (incl. cross-project linked-file consumers) and
    // *affected* (downstream dependents). We return the full set of project names: the selector
    // intersects the test projects into the matrix and matches the production projects against
    // project_rules. See GraphAffectedProjects for why this replaced dotnet-affected.
    private static AffectedResult RunLayer1(RunOptions options, ChangedFileFilter filter, SelectionTrace trace)
    {
        try
        {
            // MSBuildLocator must register the SDK's MSBuild assemblies before any Microsoft.Build type
            // is loaded. GraphAffectedProjects.Compute is the only thing that references the engine, and
            // it is not JITted until the call below, so registering here (once) is in time.
            GraphAffectedProjects.EnsureMSBuildRegistered();

            return GraphAffectedProjects.Compute(options.RepoRoot, options.SlnxPath, options.From, options.To, options.ChangedFilesPath, filter, trace);
        }
        catch (Exception ex)
        {
            return Layer1Failed(ex);
        }
    }

    // Layer 1 is not optional: under-selecting would silently skip real tests. Any failure to compute
    // the graph closure is fatal — surface it rather than masking it behind an empty (under-selecting)
    // result. Preserve the original exception as InnerException so the crash diagnostics' stack trace
    // points at where the MSBuild graph actually failed, not at this wrapper.
    private static AffectedResult Layer1Failed(Exception inner) =>
        throw new InvalidOperationException($"Layer 1 (affected-projects graph) failed: {inner.Message}", inner);

    // On an unhandled crash, emit a diagnostics block to the step summary (and stderr) so the failure is
    // debuggable from the run alone: which stage it died in, the concrete item in hand (when known), the
    // exception, and the exact inputs needed to re-run locally. The block is appended even when the
    // summary already has partial content; the caller rethrows so the step still fails.
    private static void WriteFailureDiagnostics(RunOptions options, SelectionTrace trace, Exception ex)
    {
        var changeSource = options.ChangedFilesPath is not null
            ? $"changed-files {options.ChangedFilesPath}"
            : options.From is not null
                ? $"git diff {options.From}{(options.To is null ? " (working tree)" : $"..{options.To}")}"
                : "(none -- force-all or unset)";

        var sb = new StringBuilder();
        sb.AppendLine("## SelectTests FAILED");
        sb.AppendLine();
        sb.AppendLine("The selector crashed before completing. The CI step fails by design — a crash must");
        sb.AppendLine("never be downgraded to a silent under-selection.");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- failing stage: {trace.Stage}");
        if (!string.IsNullOrEmpty(trace.Item))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- processing: `{trace.Item}`");
        }

        // Type + message on one line; the full stack follows in a collapsible for the deep cases.
        sb.AppendLine(CultureInfo.InvariantCulture, $"- error: {ex.GetType().Name}: {ex.Message}");
        sb.AppendLine();
        sb.AppendLine("### Inputs (to reproduce)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- mode: {(options.Enforce ? "enforcing" : "audit")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- change source: {changeSource}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- repo root: {options.RepoRoot}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- slnx: {options.SlnxPath}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- map: {options.MapPath}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- force-all: {options.ForceAll}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- skip-layer1: {options.SkipLayer1}");
        sb.AppendLine();
        sb.AppendLine("<details><summary>stack trace</summary>");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(ex.ToString());
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        var markdown = sb.ToString();

        // Echo to stderr first and unconditionally: it is the most reliable surface (a local run has no
        // step summary) and must never be skipped by a failure writing the summary file.
        Console.Error.Write(markdown);

        // The step summary write is best-effort. This runs from the top-level catch handler, so a failure
        // here (e.g. GITHUB_STEP_SUMMARY points at an unwritable path) must NOT throw a new exception that
        // masks the original failure the caller is about to rethrow.
        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summaryPath is not null)
        {
            try
            {
                File.AppendAllText(summaryPath, markdown);
            }
            catch (Exception writeEx)
            {
                Console.Error.WriteLine($"[SelectTests] could not write diagnostics to GITHUB_STEP_SUMMARY: {writeEx.Message}");
            }
        }
    }

    // The non-.NET job gate, emitted as ONE JSON object under the `selection` output instead of one
    // run_* output per job, so neither this tool nor the select-tests action enumerates the concrete
    // jobs. tests.yml's setup_for_tests unpacks it once into per-job outputs, e.g.
    //   run_extension_e2e: ${{ fromJSON(steps.select_tests.outputs.selection).run_extension_e2e }}
    // Keyed by run_<job> (strip "job:", '-' -> '_'); values are real JSON booleans. A single object
    // means a NEW trigger-map job needs no change here or in the action -- only tests.yml's unpack +
    // the new job's if:. Every job the map knows is present, so an unselected one reads false (not
    // missing -> never a silently-empty gate).
    // In audit mode (default) every value is forced true because enumerate-tests still runs the FULL
    // matrix: audit computes and reports the real selection (see WriteSummary) but runs everything, so
    // the non-.NET jobs must not be gated off either.
    private static void WriteJobBooleans(RunOptions options, SelectionResult result)
    {
        var allJobs = TriggerMap.Load(options.MapPath).AllJobTokens().ToHashSet(StringComparer.Ordinal);

        // Audit mode emits run-all (every job true), mirroring the unfiltered matrix.
        var auditRunAll = !options.Enforce;

        var selection = new JsonObject();
        foreach (var job in allJobs.OrderBy(j => j, StringComparer.Ordinal))
        {
            var name = "run_" + job["job:".Length..].Replace('-', '_');
            // On ALL (or in audit mode), every job runs too.
            var value = auditRunAll || result.SelectsAll || result.Jobs.Contains(job);
            selection[name] = value;
        }

        // ToJsonString() is single-line, which the key=value $GITHUB_OUTPUT format requires.
        WriteGitHubOutput("selection", selection.ToJsonString());
    }

    // Writes the durable, machine-readable record of the selection to SELECT_TESTS_JSON_FILE (when set):
    // the mode, the inputs needed to reproduce, and EVERY selected test/job with its full per-item cause
    // list (including the Layer 1 decision path). This is the artifact a maintainer downloads weeks later
    // to answer "why did this run?" without re-running CI, and that tests can assert against. Built with
    // JsonObject (not a serializer) to avoid reflection/trimming concerns and to mirror WriteJobBooleans.
    private static void WriteSelectionJson(
        RunOptions options,
        SelectionResult result,
        IReadOnlySet<string> allTestProjects,
        IReadOnlyCollection<string> changedFiles,
        IReadOnlyCollection<string> layer1Affected,
        IReadOnlyCollection<string> excludedFiles)
    {
        var jsonPath = Environment.GetEnvironmentVariable("SELECT_TESTS_JSON_FILE");
        if (string.IsNullOrEmpty(jsonPath))
        {
            return;
        }

        var changeSource = options.ChangedFilesPath is not null
            ? $"changed-files {options.ChangedFilesPath}"
            : options.From is not null
                ? $"git diff {options.From}{(options.To is null ? " (working tree)" : $"..{options.To}")}"
                : "(none -- force-all or unset)";

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["mode"] = options.Enforce ? "enforcing" : "audit",
            ["selectsAll"] = result.SelectsAll,
            ["escalationReason"] = result.EscalationReason,
            ["inputs"] = new JsonObject
            {
                ["changeSource"] = changeSource,
                ["repoRoot"] = options.RepoRoot,
                ["slnx"] = options.SlnxPath,
                ["map"] = options.MapPath,
                ["forceAll"] = options.ForceAll,
                ["skipLayer1"] = options.SkipLayer1,
            },
            ["changedFiles"] = ToJsonArray(changedFiles.OrderBy(f => f, StringComparer.Ordinal)),
            ["excludedFiles"] = ToJsonArray(excludedFiles.OrderBy(f => f, StringComparer.Ordinal)),
            ["unattributedFiles"] = ToJsonArray(result.UnmatchedFiles.OrderBy(f => f, StringComparer.Ordinal)),
            ["layer1AffectedProjects"] = ToJsonArray(layer1Affected.OrderBy(p => p, StringComparer.Ordinal)),
            ["testProjects"] = ItemsWithCauses(result.TestProjects, result.TestCauses),
            // The unselected matrix projects, so the artifact records what was skipped, not only what ran.
            ["skippedTestProjects"] = ToJsonArray(
                allTestProjects.Except(result.TestProjects, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)),
            ["jobs"] = ItemsWithCauses(result.Jobs, result.JobCauses),
        };

        var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        static JsonArray ToJsonArray(IEnumerable<string> values)
        {
            var array = new JsonArray();
            foreach (var value in values)
            {
                array.Add(value);
            }

            return array;
        }

        // One object per selected item: its name plus the ordered cause list. The key set of `causes`
        // IS the selected set, so iterate the selected names and look each up (empty causes under ALL).
        static JsonArray ItemsWithCauses(IReadOnlySet<string> items, IReadOnlyDictionary<string, IReadOnlyList<Cause>> causes)
        {
            var array = new JsonArray();
            foreach (var name in items.OrderBy(n => n, StringComparer.Ordinal))
            {
                var causeArray = new JsonArray();
                if (causes.TryGetValue(name, out var list))
                {
                    foreach (var cause in list.OrderBy(c => CausePriority(c.Kind)))
                    {
                        JsonArray? path = null;
                        if (cause.Path is { Count: > 0 })
                        {
                            path = new JsonArray();
                            foreach (var hop in cause.Path)
                            {
                                path.Add(hop);
                            }
                        }

                        causeArray.Add(new JsonObject
                        {
                            ["kind"] = cause.Kind.ToString(),
                            ["trigger"] = cause.Trigger,
                            ["reason"] = cause.Reason,
                            ["path"] = path,
                        });
                    }
                }

                array.Add(new JsonObject
                {
                    ["name"] = name,
                    ["causes"] = causeArray,
                });
            }

            return array;
        }
    }

    // Builds the sticky PR comment. Structure: WHAT runs first (the flat lists of selected jobs and
    // test projects, so a reader sees the full impact at a glance even with many changed files), then
    // HOW it was chosen (the per-trigger grouping). Grouping by trigger -- rather than listing each
    // project with its reason appended -- keeps the "why" readable when one change fans out to dozens
    // of projects: the reason is stated once per trigger, not repeated per project. Deliberately omits
    // the step-summary audit detail (options, changed-file list, would-have-skipped). Written to
    // SELECT_TESTS_COMMENT_FILE when set.
    private static void WriteSelectionComment(
        RunOptions options,
        SelectionResult result,
        IReadOnlySet<string> allTestProjects,
        IReadOnlyCollection<string> changedFiles)
    {
        var commentPath = Environment.GetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE");
        if (string.IsNullOrEmpty(commentPath))
        {
            return;
        }

        var sb = new StringBuilder();
        // Audit mode is advisory (the full matrix runs regardless), so call it out in the title;
        // enforcing is the normal case and needs no qualifier.
        sb.AppendLine(options.Enforce ? "## Tests selector" : "## Tests selector (audit mode)");
        sb.AppendLine();

        if (!options.Enforce)
        {
            // Audit mode runs the full matrix and every job regardless of the selection, so the lists
            // below are advisory: they are what selective CI WOULD run once ENFORCE_SELECTION is on.
            // Say so explicitly, otherwise a reader could mistake the subset for what actually ran.
            sb.AppendLine("_The full test matrix and all jobs still run in audit mode. The tests and jobs below are what selective CI **would** run under enforcement._");
            sb.AppendLine();
        }

        if (result.SelectsAll)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"**Runs the full test matrix + all jobs (ALL)** — {result.EscalationReason}");
            sb.AppendLine();
            WriteCommentFile(commentPath, sb.ToString());
            return;
        }

        var tests = result.TestProjects.OrderBy(p => p, StringComparer.Ordinal).ToList();
        // Keep the full job: tokens for cause lookup; strip the prefix only for display.
        var jobs = result.Jobs.OrderBy(j => j, StringComparer.Ordinal).ToList();

        var fileWord = changedFiles.Count == 1 ? "changed file" : "changed files";
        var jobWord = jobs.Count == 1 ? "job" : "jobs";
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**{tests.Count} / {allTestProjects.Count} test projects · {jobs.Count} {jobWord}**, from {changedFiles.Count} {fileWord}.");
        sb.AppendLine();

        // WHAT runs -- the flat lists up front. A reviewer scanning a large selection sees the complete
        // set of projects and jobs without reading the per-trigger breakdown below. Test projects come
        // first because they are the primary thing a reviewer cares about; the non-.NET jobs follow.
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Selected test projects ({tests.Count} / {allTestProjects.Count})");
        sb.AppendLine();
        sb.AppendLine(tests.Count == 0
            ? "_none — no .NET test projects run for this change._"
            : string.Join(", ", tests.Select(t => $"`{t}`")));
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"### Selected jobs ({jobs.Count})");
        sb.AppendLine();
        sb.AppendLine(jobs.Count == 0
            ? "_none_"
            : string.Join(", ", jobs.Select(j => $"`{StripJobPrefix(j)}`")));
        sb.AppendLine();

        // HOW it was chosen -- the per-trigger grouping.
        AppendSelectionRationale(sb, result, tests, jobs);

        sb.AppendLine();
        WriteCommentFile(commentPath, sb.ToString());
    }

    // Renders the "how these were chosen" section: every selected test project grouped under each
    // trigger (changed file / affected project / derived test) that pulled it in, plus a per-job
    // reasons table. Full attribution is preserved -- a project selected by several triggers appears
    // under each -- so nothing is hidden, but each trigger's reason is written once instead of being
    // repeated on every project line.
    private static void AppendSelectionRationale(
        StringBuilder sb,
        SelectionResult result,
        IReadOnlyList<string> tests,
        IReadOnlyList<string> jobs)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        // Collapse the rationale by default: the comment leads with WHAT runs (the flat lists above),
        // and a reviewer who wants to know WHY expands this. A blank line after </summary> is required
        // so GitHub renders the markdown (headings, table, nested <details>) inside the block.
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>How these were chosen — grouped by what changed</summary>");
        sb.AppendLine();

        // Invert TestCauses (project -> causes) into trigger groups (trigger -> projects). Causes that
        // share a trigger collapse into one group: a changed file and its graph fan-out group together
        // so a single source edit shows its whole closure under one heading.
        var groups = new Dictionary<string, Dictionary<string, Cause>>(StringComparer.Ordinal);
        foreach (var project in tests)
        {
            if (!result.TestCauses.TryGetValue(project, out var causes))
            {
                continue;
            }

            foreach (var cause in causes)
            {
                var key = CauseGroupKey(cause);
                if (!groups.TryGetValue(key, out var members))
                {
                    members = new Dictionary<string, Cause>(StringComparer.Ordinal);
                    groups[key] = members;
                }

                // If the same project reaches a group via more than one cause, keep the most direct one
                // (lowest priority) so its bucket/hop annotation reflects the closest path.
                if (!members.TryGetValue(project, out var existing) || CausePriority(cause.Kind) < CausePriority(existing.Kind))
                {
                    members[project] = cause;
                }
            }
        }

        // Largest group first -- the biggest fan-out is the most useful thing to see when triaging a
        // large selection. Ties broken by key for a stable order.
        var orderedGroups = groups
            .OrderByDescending(g => g.Value.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        // Headline: when one trigger drives a large share of a big selection, call it out so the reader
        // immediately sees where the bulk of the work comes from.
        if (orderedGroups.Count > 0 && tests.Count >= 10 && orderedGroups[0].Value.Count >= 5)
        {
            var top = orderedGroups[0];
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"⚠️ {top.Value.Count} of the {tests.Count} selected test projects come from a single change — {CauseGroupDescriptor(top.Key)}.");
            sb.AppendLine();
        }

        foreach (var (key, members) in orderedGroups)
        {
            sb.AppendLine(CauseGroupHeader(key));

            // Within a group, separate the projects whose change is direct (the file lives in them, or
            // a path rule named them) from those reached transitively through the project graph -- the
            // two have different review weight, and stating the mechanism once per bucket avoids
            // repeating it per project.
            var direct = members.Where(m => m.Value.Kind is CauseKind.Convention or CauseKind.PathRule).Select(m => m.Key).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var graph = members.Where(m => m.Value.Kind is CauseKind.Layer1Graph).OrderBy(m => m.Key, StringComparer.Ordinal).ToList();
            var other = members.Where(m => m.Value.Kind is not (CauseKind.Convention or CauseKind.PathRule or CauseKind.Layer1Graph)).Select(m => m.Key).OrderBy(p => p, StringComparer.Ordinal).ToList();

            if (direct.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"→ {RenderProjectList(direct.Select(p => $"`{p}`").ToList(), "directly")}");
            }
            if (graph.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"→ {RenderProjectList(graph.Select(MemberWithHops).ToList(), "via the project graph")}");
            }
            if (other.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"→ {RenderProjectList(other.Select(p => $"`{p}`").ToList(), other.Count == 1 ? "test" : "tests")}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("#### Job reasons");
        sb.AppendLine();
        if (jobs.Count == 0)
        {
            sb.AppendLine("_none_");
        }
        else
        {
            sb.AppendLine("| Job | Triggered by |");
            sb.AppendLine("|---|---|");
            foreach (var token in jobs)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| `{StripJobPrefix(token)}` | {JobCausesText(result.JobCauses, token)} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("</details>");
    }

    private static string StripJobPrefix(string token)
        => token.StartsWith("job:", StringComparison.Ordinal) ? token["job:".Length..] : token;

    // Renders a bucket's projects. Small lists go inline (scannable); large ones collapse into a
    // <details> so a 30-project fan-out doesn't dominate the comment.
    private static string RenderProjectList(IReadOnlyList<string> items, string label)
    {
        const int InlineLimit = 12;
        var joined = string.Join(", ", items);
        return items.Count <= InlineLimit
            ? $"**{items.Count}** {label}: {joined}"
            : $"**{items.Count}** {label}\n<details><summary>show {items.Count}</summary>\n\n{joined}\n</details>";
    }

    // A graph-selected project, annotated with its hop count when the change reached it through more
    // than one project edge (a near vs. far dependency is useful review signal).
    private static string MemberWithHops(KeyValuePair<string, Cause> member)
    {
        if (member.Value is { Kind: CauseKind.Layer1Graph, Path: { Count: > 0 } path })
        {
            // path = [seedFile, project0, ..., affectedTest]; edges between projects = count - 2.
            var hops = path.Count - 2;
            if (hops > 1)
            {
                return $"`{member.Key}` ({hops} hops)";
            }
        }

        return $"`{member.Key}`";
    }

    // The trigger a cause groups under. Direct file matches and graph fan-out from the same seed file
    // share a "file:" key so they render under one heading; affected-project and derived-test causes
    // get their own keyed groups.
    private static string CauseGroupKey(Cause cause) => cause.Kind switch
    {
        CauseKind.Convention or CauseKind.PathRule => $"file:{cause.Trigger}",
        CauseKind.Layer1Graph => $"file:{(cause.Path is { Count: > 0 } path ? path[0] : "(changed source)")}",
        CauseKind.AffectedProject => $"affected:{cause.Trigger}",
        CauseKind.DerivedFromTest => $"derived:{cause.Trigger}",
        _ => $"other:{cause.Trigger}",
    };

    // The heading shown for a group key.
    private static string CauseGroupHeader(string key)
    {
        var sep = key.IndexOf(':', StringComparison.Ordinal);
        var (kind, value) = (key[..sep], key[(sep + 1)..]);
        return kind switch
        {
            "file" => $"**{FileEmoji(value)} `{value}`** *({FileChangeKind(value)})*",
            "affected" => $"**📦 affected project `{value}`**",
            "derived" => $"**🧪 derived from test `{value}`**",
            _ => $"**`{value}`**",
        };
    }

    // A one-line descriptor of a group key, for the headline call-out.
    private static string CauseGroupDescriptor(string key)
    {
        var sep = key.IndexOf(':', StringComparison.Ordinal);
        var (kind, value) = (key[..sep], key[(sep + 1)..]);
        return kind switch
        {
            "file" => $"`{value}`",
            "affected" => $"affected project `{value}`",
            "derived" => $"derived from test `{value}`",
            _ => $"`{value}`",
        };
    }

    private static string FileChangeKind(string path)
        => path.StartsWith("src/", StringComparison.Ordinal) ? "changed source"
            : path.StartsWith("tests/", StringComparison.Ordinal) ? "changed test"
            : "changed";

    private static string FileEmoji(string path)
        => path.StartsWith("src/", StringComparison.Ordinal) ? "🔧"
            : path.StartsWith("tests/", StringComparison.Ordinal) ? "🧪"
            : "📄";

    private static void WriteCommentFile(string commentPath, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(commentPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(commentPath, content);
    }

    // The full set of causes for one job, de-duplicated and priority-ordered, for the job-reasons
    // table. Every cause is shown (no truncation) so a reviewer sees exactly what pulled the job in.
    //
    // A job is often pulled in by several INDEPENDENT triggers (e.g. a changed path rule AND an affected
    // production project AND a selected test that derives it). Comma-joining those on one line reads as a
    // single causal chain -- "affected project Aspire.Cli, selected test Aspire.Cli.Tests" looks like one
    // flows through the other, when they are unrelated reasons. So render each independent trigger as its
    // own bulleted line, collapsing only the homogeneous changed-file causes (every path that matched a
    // rule/convention) into one comma-joined segment. A single trigger needs no bullet.
    private static string JobCausesText(IReadOnlyDictionary<string, IReadOnlyList<Cause>> causes, string key)
    {
        if (!causes.TryGetValue(key, out var list) || list.Count == 0)
        {
            return "_unattributed_";
        }

        var ordered = list.OrderBy(c => CausePriority(c.Kind)).ToList();

        // Changed-file causes (a path matched a convention/path rule) are the same KIND of reason, so
        // they read fine comma-joined as one segment. Distinct kinds (affected project, selected test)
        // each get their own line.
        var fileCauses = ordered
            .Where(c => c.Kind is CauseKind.Convention or CauseKind.PathRule)
            .Select(ShortCause)
            .Distinct()
            .ToList();
        var otherCauses = ordered
            .Where(c => c.Kind is not (CauseKind.Convention or CauseKind.PathRule))
            .Select(ShortCause)
            .Distinct()
            .ToList();

        var segments = new List<string>();
        if (fileCauses.Count > 0)
        {
            segments.Add(string.Join(", ", fileCauses));
        }
        segments.AddRange(otherCauses);

        // A single trigger reads fine on its own; 2+ get "• " bullets so they cannot run together (a
        // literal bullet glyph -- a markdown "-" list does not render inside a GitHub table cell).
        return segments.Count == 1
            ? segments[0]
            : string.Join("<br>", segments.Select(s => $"• {s}"));
    }

    private static void WriteSummary(
        RunOptions options,
        SelectionResult result,
        IReadOnlySet<string> allTestProjects,
        IReadOnlyCollection<string> changedFiles,
        IReadOnlyCollection<string> layer1Affected,
        IReadOnlyCollection<string> excludedFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## SelectTests");
        sb.AppendLine();

        // Options the run was invoked with, so an audit reader can see exactly what produced the
        // selection below (and reproduce it).
        var source = options.ChangedFilesPath is not null
            ? $"changed-files {options.ChangedFilesPath}"
            : $"git diff {options.From}{(options.To is null ? " (working tree)" : $"..{options.To}")}";
        sb.AppendLine("### Options");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- mode: {(options.Enforce ? "enforcing" : "audit (advisory: the full matrix + all jobs run regardless of the selection below)")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- change source: {source}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- force-all (kill switch): {options.ForceAll}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- layer 1 (affected-projects graph): {(options.SkipLayer1 || options.ForceAll ? "skipped" : $"{layer1Affected.Count} affected project(s) (production + test)")}");
        sb.AppendLine();

        // The changed files that came in, so a reader can tell which inputs drove the selection.
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Changed files ({changedFiles.Count})");
        sb.AppendLine();
        sb.AppendLine("<details><summary>show</summary>");
        sb.AppendLine();
        foreach (var file in changedFiles.OrderBy(f => f, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- `{file}`");
        }
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        // Files dropped by the pre-filter (exclude globs): docs/skills/instructions and other
        // no-CI-needed loose files. Shown so an audit reader can see they were intentionally removed
        // from BOTH layers' input (not silently un-attributed).
        if (excludedFiles.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Pre-filtered (excluded) files ({excludedFiles.Count})");
            sb.AppendLine();
            sb.AppendLine("Dropped before selection by the prefilter (CI skip-gate patterns; no CI impact):");
            sb.AppendLine();
            foreach (var file in excludedFiles.OrderBy(f => f, StringComparer.Ordinal))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- `{file}`");
            }
            sb.AppendLine();
        }

        // Files no layer accounted for: matched no curated rule (Layer 2), not ignored, and not a
        // project-owned source file (Layer 1, via the Aspire.slnx project dirs). A src/** file here
        // forced the run-all fallback; a non-src file here is only an audit signal that a loose,
        // non-project dependency may need a curated rule. Always shown, including under ALL.
        var unmatched = result.UnmatchedFiles.OrderBy(f => f, StringComparer.Ordinal).ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Unattributed changed files ({unmatched.Count})");
        sb.AppendLine();
        if (unmatched.Count == 0)
        {
            sb.AppendLine("_none — every changed file was matched by Layer 2, ignored, or Layer-1-owned._");
        }
        else
        {
            sb.AppendLine("Matched by no map rule (Layer 2) and not a project-owned source file");
            sb.AppendLine("(Layer 1). Add a curated rule if any of these gate a test:");
            sb.AppendLine();
            foreach (var file in unmatched)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- `{file}`");
            }
        }
        sb.AppendLine();

        sb.AppendLine("### Selection");
        if (result.SelectsAll)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **selects ALL** — {result.EscalationReason}");
            WriteOut(sb);
            return;
        }

        var selected = result.TestProjects.OrderBy(p => p, StringComparer.Ordinal).ToList();
        var skipped = allTestProjects.Except(result.TestProjects, StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var jobTokens = result.Jobs.OrderBy(j => j, StringComparer.Ordinal).ToList();

        sb.AppendLine(CultureInfo.InvariantCulture, $"- selected test projects: {selected.Count} / {allTestProjects.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- triggered jobs: {(jobTokens.Count == 0 ? "(none)" : string.Join(", ", jobTokens))}");
        sb.AppendLine();

        // Each selected test project / job is listed with the full set of reasons it was selected
        // (the changed file, affected project, graph edge, or selected test that pulled it in, plus
        // the curated rule's reason text). This is the "why" an auditor needs to trust the selection.
        AppendCauseList(sb, "Selected test projects", selected, p => p, result.TestCauses);
        AppendCauseList(
            sb,
            "Triggered jobs",
            jobTokens,
            t => t.StartsWith("job:", StringComparison.Ordinal) ? t["job:".Length..] : t,
            result.JobCauses);
        // In enforcing mode the unselected projects are actually skipped; in audit mode the full matrix
        // still runs, so they only "would have been" skipped.
        AppendProjectList(sb, options.Enforce ? "Skipped (not run)" : "Would have been skipped", skipped);

        WriteOut(sb);

        static void AppendCauseList(
            StringBuilder builder,
            string title,
            IReadOnlyList<string> keys,
            Func<string, string> display,
            IReadOnlyDictionary<string, IReadOnlyList<Cause>> causes)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"<details><summary>{title} ({keys.Count})</summary>");
            builder.AppendLine();
            foreach (var key in keys)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- `{display(key)}`");
                if (causes.TryGetValue(key, out var list))
                {
                    foreach (var cause in list.OrderBy(c => CausePriority(c.Kind)))
                    {
                        builder.AppendLine(CultureInfo.InvariantCulture, $"  - {VerboseCause(cause)}");
                    }
                }
            }
            builder.AppendLine();
            builder.AppendLine("</details>");
            builder.AppendLine();
        }

        static void AppendProjectList(StringBuilder builder, string title, IReadOnlyList<string> projects)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"<details><summary>{title} ({projects.Count})</summary>");
            builder.AppendLine();
            foreach (var p in projects)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {p}");
            }
            builder.AppendLine();
            builder.AppendLine("</details>");
            builder.AppendLine();
        }

        static void WriteOut(StringBuilder builder)
        {
            var markdown = builder.ToString();
            var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (summaryPath is not null)
            {
                File.AppendAllText(summaryPath, markdown);
            }
            else
            {
                Console.Error.Write(markdown);
            }
        }
    }

    // Lower = more "direct" / closer to the change, so it's the cause shown first in the comment and
    // listed first in the summary: a literal changed file beats an affected-project edge beats a
    // graph edge beats a test-derived pull.
    private static int CausePriority(CauseKind kind) => kind switch
    {
        CauseKind.Convention => 0,
        CauseKind.PathRule => 1,
        CauseKind.AffectedProject => 2,
        CauseKind.Layer1Graph => 3,
        CauseKind.DerivedFromTest => 4,
        _ => 5,
    };

    // Terse, one-line cause for the PR comment (no rule reason text).
    private static string ShortCause(Cause cause) => cause.Kind switch
    {
        CauseKind.Convention => $"`{cause.Trigger}`",
        CauseKind.PathRule => $"`{cause.Trigger}`",
        CauseKind.AffectedProject => $"affected project `{cause.Trigger}`",
        // Name the seed changed file (and hop count) rather than the full chain, which the summary
        // carries -- the comment stays scannable. Falls back to a generic label when no path was tracked.
        CauseKind.Layer1Graph => Layer1ShortCause(cause),
        // "selected test" (a noun phrase parallel to "affected project") names the trigger: a
        // derived_targets rule pulls this job in because that test project was selected. Phrasing it as a
        // noun -- not "derived from test X" -- avoids a dangling "from" that reads as if the line above it
        // in the job-reasons cell flows through this test.
        CauseKind.DerivedFromTest => $"selected test `{cause.Trigger}`",
        _ => cause.Trigger,
    };

    // "via graph from `seed.cs`" (+ "(N hops)" when the reverse-dependency chain is more than one edge).
    private static string Layer1ShortCause(Cause cause)
    {
        if (cause.Path is not { Count: > 0 } path)
        {
            return "changed source (graph)";
        }

        // path = [seedFile, project0, ..., affectedTest]; project edges = (count - 1 projects) - 1.
        var hops = path.Count - 2;
        var suffix = hops > 1 ? $" ({hops} hops)" : "";
        return $"via graph from `{path[0]}`{suffix}";
    }

    // Full cause for the step summary, including the curated rule reason when present.
    private static string VerboseCause(Cause cause)
    {
        var head = cause.Kind switch
        {
            CauseKind.Convention => $"convention match `{cause.Trigger}`",
            CauseKind.PathRule => $"path rule `{cause.Trigger}`",
            CauseKind.AffectedProject => $"affected project `{cause.Trigger}`",
            // Render the full decision path (seed file -> ... -> affected test) when available, so the
            // summary explains HOW the change reached the test, not just THAT it did.
            CauseKind.Layer1Graph => cause.Path is { Count: > 0 } path
                ? $"graph closure: {string.Join(" → ", path)}"
                : "affected by changed source (graph closure)",
            CauseKind.DerivedFromTest => $"derived from selected test `{cause.Trigger}`",
            _ => cause.Trigger,
        };

        // Map reasons can be YAML folded scalars spanning lines; collapse to one line for the bullet.
        return string.IsNullOrWhiteSpace(cause.Reason)
            ? head
            : $"{head} — {string.Join(' ', cause.Reason!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}";
    }

    private static string RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        out int exitCode,
        out string stderr,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        exitCode = process.ExitCode;
        stderr = stderrTask.GetAwaiter().GetResult();
        return stdoutTask.GetAwaiter().GetResult();
    }
}
