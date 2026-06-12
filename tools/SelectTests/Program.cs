// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Aspire.SelectTests;

// Entry point for the selective-CI tool. Runs BEFORE enumerate-tests and computes the subset of
// test projects (and the non-.NET jobs) relevant to a PR's changed files, by unioning:
//   Layer 1 — the MSBuild ProjectGraph reverse-dependency closure (GraphAffectedProjects), and
//   Layer 2 — the curated eng/test-trigger-map.yml resolved by TestSelector.
// With --enforce and a non-ALL selection it writes an OverrideProjectToBuild props file
// (--before-build-props) so the subsequent enumerate-tests `-test` build enumerates ONLY the
// selected projects. In audit mode (no --enforce) it writes the run_* job booleans and an advisory
// "would-have-been-skipped" summary but no props, so enumerate-tests runs the full matrix unchanged.
// See docs/ci/test-trigger-selector-design.md.

var repoRootOption = new Option<string>("--repo-root")
{
    Description = "Repository root (where .git and Aspire.slnx live).",
    DefaultValueFactory = _ => Directory.GetCurrentDirectory()
};

var mapOption = new Option<string?>("--map")
{
    Description = "Path to eng/test-trigger-map.yml. Defaults to <repo-root>/eng/test-trigger-map.yml."
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
    repoRootOption, mapOption, fromOption, toOption, changedFilesOption,
    skipLayer1Option, forceAllOption, enforceOption, beforeBuildPropsOption
})
{
    rootCommand.Options.Add(option);
}

rootCommand.SetAction(parseResult =>
{
    var repoRoot = Path.GetFullPath(parseResult.GetValue(repoRootOption)!);
    var mapPath = parseResult.GetValue(mapOption)
        ?? Path.Combine(repoRoot, "eng", "test-trigger-map.yml");
    var from = parseResult.GetValue(fromOption);
    var to = parseResult.GetValue(toOption);
    var changedFilesPath = parseResult.GetValue(changedFilesOption);
    var skipLayer1 = parseResult.GetValue(skipLayer1Option);
    var forceAll = parseResult.GetValue(forceAllOption);
    var enforce = parseResult.GetValue(enforceOption);
    var beforeBuildProps = parseResult.GetValue(beforeBuildPropsOption);

    return Selection.Run(new RunOptions(
        repoRoot, mapPath, from, to, changedFilesPath,
        skipLayer1, forceAll, enforce, beforeBuildProps));
});

return rootCommand.Parse(args).Invoke();

internal sealed record RunOptions(
    string RepoRoot,
    string MapPath,
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
        // The universe an ALL selection expands to, and the existence guard for test: targets and
        // Layer 1 affected test projects: the test projects in Aspire.slnx (tests/<Name>/<Name>.csproj
        // with a .Tests suffix). Derived from the slnx -- NOT from an enumerated matrix -- because the
        // selector now runs BEFORE enumerate-tests. Maps each test project name to its repo-relative
        // .csproj path so a selected name can be written as an OverrideProjectToBuild item.
        var testProjectsByName = LoadTestProjects(options.RepoRoot);
        var allTestProjects = testProjectsByName.Keys.ToHashSet(StringComparer.Ordinal);

        // Under --force-all the selector returns ALL regardless of the diff (see below), so skip
        // resolving changed files and the Layer 1 graph closure entirely. Resolving them is not just
        // wasted work: --force-all is the path taken when there is no usable diff base (or the
        // [full ci] kill switch fired), so ResolveChangedFiles would otherwise throw for lack of a
        // --from/--changed-files input.
        var changedFiles = options.ForceAll
            ? Array.Empty<string>()
            : ResolveChangedFiles(options);

        var layer1Affected = (options.ForceAll || options.SkipLayer1)
            ? Array.Empty<string>()
            : RunLayer1(options);

        var projectDirectories = LoadProjectDirectories(options.RepoRoot);

        var selector = new TestSelector(options.MapPath, allTestProjects, projectDirectories);
        var result = selector.Select(changedFiles, layer1Affected, new SelectorOptions(options.ForceAll));

        WriteSummary(options, result, allTestProjects, changedFiles, layer1Affected);
        WriteJobBooleans(options, result);
        WriteSelectionComment(options, result, allTestProjects);

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
        // "enumerate everything" -- the workflow then omits /p:BeforeBuildPropsPath.
        WriteGitHubOutput("before_build_props", restrictBuild ? options.BeforeBuildProps! : "");

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
    private static IReadOnlyDictionary<string, string> LoadTestProjects(string repoRoot)
    {
        var slnxPath = Path.Combine(repoRoot, "Aspire.slnx");
        if (!File.Exists(slnxPath))
        {
            throw new InvalidOperationException($"Aspire.slnx was not found under repository root: {repoRoot}");
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
        var range = options.To is null ? new[] { options.From } : new[] { options.From, options.To };
        var args = new List<string> { "diff", "--name-only" };
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
    private static IReadOnlyCollection<string> LoadProjectDirectories(string repoRoot)
    {
        var slnxPath = Path.Combine(repoRoot, "Aspire.slnx");
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
    private static IReadOnlyCollection<string> RunLayer1(RunOptions options)
    {
        try
        {
            // MSBuildLocator must register the SDK's MSBuild assemblies before any Microsoft.Build type
            // is loaded. GraphAffectedProjects.Compute is the only thing that references the engine, and
            // it is not JITted until the call below, so registering here (once) is in time.
            GraphAffectedProjects.EnsureMSBuildRegistered();

            return GraphAffectedProjects.Compute(options.RepoRoot, options.From, options.To, options.ChangedFilesPath);
        }
        catch (Exception ex)
        {
            return Layer1Failed(ex.Message);
        }
    }

    // Layer 1 is not optional: under-selecting would silently skip real tests. Any failure to compute
    // the graph closure is fatal — surface it rather than masking it behind an empty (under-selecting)
    // result.
    private static IReadOnlyCollection<string> Layer1Failed(string detail) =>
        throw new InvalidOperationException($"Layer 1 (affected-projects graph) failed: {detail}");

    // Per-job booleans for the if: conditions gating the non-.NET jobs. job:extension-e2e ->
    // run_extension_e2e, etc. Emitted for every job the map knows, so unselected ones are 'false'.
    // In audit mode (default) every boolean is forced 'true' because enumerate-tests still runs the
    // full matrix: audit computes and reports the real selection (see WriteSummary) but runs
    // everything, so the non-.NET jobs must not be gated off either.
    private static void WriteJobBooleans(RunOptions options, SelectionResult result)
    {
        var githubOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        var allJobs = TriggerMap.Load(options.MapPath).AllJobTokens().ToHashSet(StringComparer.Ordinal);

        // Audit mode emits run-all (every job true), mirroring the unfiltered matrix.
        var auditRunAll = !options.Enforce;

        var lines = allJobs
            .OrderBy(j => j, StringComparer.Ordinal)
            .Select(job =>
            {
                var name = "run_" + job["job:".Length..].Replace('-', '_');
                // On ALL (or in audit mode), every job runs too.
                var value = (auditRunAll || result.SelectsAll || result.Jobs.Contains(job)) ? "true" : "false";
                return $"{name}={value}";
            })
            .ToList();

        if (githubOutput is not null)
        {
            File.AppendAllLines(githubOutput, lines);
        }
        else
        {
            foreach (var line in lines)
            {
                Console.Error.WriteLine(line);
            }
        }
    }

    // Builds the sticky PR comment: a terse, scannable view of exactly what runs for this PR -- the
    // selected test projects and the selected jobs, nothing else. Deliberately omits the audit detail
    // (options, changed files, would-have-skipped) that the job step summary carries; reviewers want a
    // quick "what runs", not the full trace. Written to SELECT_TESTS_COMMENT_FILE when set.
    private static void WriteSelectionComment(RunOptions options, SelectionResult result, IReadOnlySet<string> allTestProjects)
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

        if (result.SelectsAll)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"**Runs the full test matrix + all jobs (ALL)** — {result.EscalationReason}");
        }
        else
        {
            var tests = result.TestProjects.OrderBy(p => p, StringComparer.Ordinal).ToList();
            var jobs = result.Jobs
                .Select(j => j.StartsWith("job:", StringComparison.Ordinal) ? j["job:".Length..] : j)
                .OrderBy(j => j, StringComparer.Ordinal)
                .ToList();

            sb.AppendLine(CultureInfo.InvariantCulture, $"**Test projects ({tests.Count} / {allTestProjects.Count})**");
            sb.AppendLine();
            if (tests.Count == 0)
            {
                sb.AppendLine("_none — no .NET test projects run for this change._");
            }
            else
            {
                foreach (var t in tests)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- `{t}`");
                }
            }
            sb.AppendLine();

            sb.AppendLine(CultureInfo.InvariantCulture, $"**Jobs ({jobs.Count})**");
            sb.AppendLine();
            if (jobs.Count == 0)
            {
                sb.AppendLine("_none_");
            }
            else
            {
                foreach (var j in jobs)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- `{j}`");
                }
            }
        }
        sb.AppendLine();

        var dir = Path.GetDirectoryName(Path.GetFullPath(commentPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(commentPath, sb.ToString());
    }

    private static void WriteSummary(
        RunOptions options,
        SelectionResult result,
        IReadOnlySet<string> allTestProjects,
        IReadOnlyCollection<string> changedFiles,
        IReadOnlyCollection<string> layer1Affected)
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

        sb.AppendLine(CultureInfo.InvariantCulture, $"- selected test projects: {selected.Count} / {allTestProjects.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- triggered jobs: {(result.Jobs.Count == 0 ? "(none)" : string.Join(", ", result.Jobs.OrderBy(j => j, StringComparer.Ordinal)))}");
        sb.AppendLine();
        AppendProjectList(sb, "Selected test projects", selected);
        // In enforcing mode the unselected projects are actually skipped; in audit mode the full matrix
        // still runs, so they only "would have been" skipped.
        AppendProjectList(sb, options.Enforce ? "Skipped (not run)" : "Would have been skipped", skipped);

        WriteOut(sb);

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
