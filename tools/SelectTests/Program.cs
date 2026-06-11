// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.SelectTests;

// Entry point for the selective-CI tool. Computes the subset of the test matrix (and the non-.NET
// jobs) relevant to a PR's changed files, by unioning:
//   Layer 1 — the MSBuild ProjectGraph reverse-dependency closure from `dotnet-affected`, and
//   Layer 2 — the curated docs/ci/test-trigger-map.yml resolved by TestSelector.
// It runs in audit mode: it writes the selection + a would-have-been-skipped summary, but emits
// the full, unfiltered matrix unless --enforce is passed, so CI keeps running everything while the
// map's accuracy is validated. See docs/ci/test-trigger-selector-design.md.

var repoRootOption = new Option<string>("--repo-root")
{
    Description = "Repository root (where .git and Aspire.slnx live).",
    DefaultValueFactory = _ => Directory.GetCurrentDirectory()
};

var mapOption = new Option<string?>("--map")
{
    Description = "Path to docs/ci/test-trigger-map.yml. Defaults to <repo-root>/docs/ci/test-trigger-map.yml."
};

var matrixOption = new Option<string>("--matrix")
{
    Description = "Path to the enumerate-tests all_tests JSON ({\"include\":[...]}).",
    Required = true
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

var dotnetAffectedOption = new Option<string?>("--dotnet-affected")
{
    Description = "Path to a standalone dotnet-affected executable. Defaults to the local tool ('dotnet tool run dotnet-affected')."
};

var skipLayer1Option = new Option<bool>("--skip-layer1")
{
    Description = "Skip the dotnet-affected graph closure (Layer 2 / curated map only)."
};

var forceAllOption = new Option<bool>("--force-all")
{
    Description = "Kill switch: force the full matrix regardless of changed files."
};

var enforceOption = new Option<bool>("--enforce")
{
    Description = "Emit the filtered matrix. Without this (audit mode), the full matrix is emitted unchanged."
};

var outputOption = new Option<string?>("--output")
{
    Description = "Where to write the (possibly filtered) matrix JSON. Defaults to stdout."
};

var rootCommand = new RootCommand("Select the relevant CI test subset for a PR's changed files.");
foreach (var option in new Option[]
{
    repoRootOption, mapOption, matrixOption, fromOption, toOption, changedFilesOption,
    dotnetAffectedOption, skipLayer1Option, forceAllOption, enforceOption, outputOption
})
{
    rootCommand.Options.Add(option);
}

rootCommand.SetAction(parseResult =>
{
    var repoRoot = Path.GetFullPath(parseResult.GetValue(repoRootOption)!);
    var mapPath = parseResult.GetValue(mapOption)
        ?? Path.Combine(repoRoot, "docs", "ci", "test-trigger-map.yml");
    var matrixPath = parseResult.GetValue(matrixOption)!;
    var from = parseResult.GetValue(fromOption);
    var to = parseResult.GetValue(toOption);
    var changedFilesPath = parseResult.GetValue(changedFilesOption);
    var dotnetAffected = parseResult.GetValue(dotnetAffectedOption);
    var skipLayer1 = parseResult.GetValue(skipLayer1Option);
    var forceAll = parseResult.GetValue(forceAllOption);
    var enforce = parseResult.GetValue(enforceOption);
    var output = parseResult.GetValue(outputOption);

    return Selection.Run(new RunOptions(
        repoRoot, mapPath, matrixPath, from, to, changedFilesPath,
        dotnetAffected, skipLayer1, forceAll, enforce, output));
});

return rootCommand.Parse(args).Invoke();

internal sealed record RunOptions(
    string RepoRoot,
    string MapPath,
    string MatrixPath,
    string? From,
    string? To,
    string? ChangedFilesPath,
    string? DotnetAffected,
    bool SkipLayer1,
    bool ForceAll,
    bool Enforce,
    string? Output);

internal static class Selection
{
    public static int Run(RunOptions options)
    {
        var matrix = JsonNode.Parse(File.ReadAllText(options.MatrixPath))
            ?? throw new InvalidOperationException($"Matrix file '{options.MatrixPath}' is empty.");
        var includeEntries = (matrix["include"] as JsonArray)
            ?? throw new InvalidOperationException("Matrix JSON has no 'include' array.");

        // The matrix projectName values are the universe an ALL selection expands to.
        var allTestProjects = includeEntries
            .Select(e => (string?)e?["projectName"])
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        var changedFiles = ResolveChangedFiles(options);

        var layer1Affected = (options.ForceAll || options.SkipLayer1)
            ? Array.Empty<string>()
            : RunLayer1(options);

        var projectDirectories = LoadProjectDirectories(options.RepoRoot);

        var selector = new TestSelector(options.MapPath, allTestProjects, projectDirectories);
        var result = selector.Select(changedFiles, layer1Affected, new SelectorOptions(options.ForceAll));

        WriteSummary(options, result, allTestProjects, changedFiles, layer1Affected);
        WriteJobBooleans(options, result);

        // Audit mode (default): keep running the full matrix while the map is validated.
        var emitFiltered = options.Enforce && !result.SelectsAll;
        WriteMatrix(options, matrix, includeEntries, result, emitFiltered);

        return 0;
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

    // Repo-relative, '/'-separated directories of every project in Aspire.slnx -- the universe
    // dotnet-affected walks. The selector treats a changed file under one of these dirs as
    // "Layer-1-owned" (attributed by the graph tool), so it never triggers the run-all fallback.
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

    // Layer 1: dotnet-affected builds the MSBuild ProjectGraph and reports every project hit by the
    // diff — the union of *changed* (incl. foreign <Compile Include> consumers) and *affected*
    // (downstream dependents). We return the full set of project names: the selector intersects the
    // test projects into the matrix and matches the production projects against project_rules.
    private static IReadOnlyCollection<string> RunLayer1(RunOptions options)
    {
        var outDir = Directory.CreateTempSubdirectory("aspire-affected");
        try
        {
            // --filter-file-path scopes discovery to the solution; raw filesystem discovery crashes
            // on the template placeholder projects. (The older --solution-path alias is obsolete.)
            //
            // NOTE: dotnet-affected reads file blobs at the --from commit through a libgit2-backed
            // virtual filesystem to detect package/file changes. That read returns invalid content
            // inside a git *worktree* (where .git is a file, not a directory), which makes the
            // MSBuild evaluation of Arcade's directory-walking props throw. CI uses a normal
            // checkout so --from/--to works there; in a local worktree use --skip-layer1.
            var affectedArgs = new List<string>
            {
                "--repository-path", options.RepoRoot,
                "--filter-file-path", "Aspire.slnx",
                "--format", "json",
                "--output-dir", outDir.FullName,
                "--output-name", "affected",
            };
            if (options.From is not null)
            {
                affectedArgs.Add("--from");
                affectedArgs.Add(options.From);
            }
            if (options.To is not null)
            {
                affectedArgs.Add("--to");
                affectedArgs.Add(options.To);
            }

            // By default invoke the local tool from .config/dotnet-tools.json ('dotnet tool run
            // dotnet-affected'), so CI just needs 'dotnet tool restore'. --dotnet-affected overrides
            // with an explicit standalone executable (e.g. a global install).
            string fileName;
            List<string> args;
            if (options.DotnetAffected is not null)
            {
                fileName = options.DotnetAffected;
                args = affectedArgs;
            }
            else
            {
                fileName = "dotnet";
                args = new List<string> { "tool", "run", "dotnet-affected" };
                args.AddRange(affectedArgs);
            }

            // MSBuildLocator (inside dotnet-affected) resolves the SDK via DOTNET_ROOT; point it at
            // the repo-local SDK so the graph evaluation matches the rest of CI.
            var env = new Dictionary<string, string>();
            var localSdk = Path.Combine(options.RepoRoot, ".dotnet");
            if (Directory.Exists(localSdk))
            {
                env["DOTNET_ROOT"] = localSdk;
                env["PATH"] = localSdk + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
            }

            string stdout;
            int exitCode;
            string stderr;
            try
            {
                stdout = RunProcess(fileName, args, options.RepoRoot, out exitCode, out stderr, env);
            }
            catch (Exception ex)
            {
                return Layer1Failed(options, $"could not start dotnet-affected: {ex.Message}");
            }

            if (exitCode != 0)
            {
                return Layer1Failed(options, $"dotnet-affected exited {exitCode}: {stderr}{stdout}");
            }

            // affected.json: [{ "Name": "...", "FilePath": "..." }]
            var jsonPath = Path.Combine(outDir.FullName, "affected.json");
            if (!File.Exists(jsonPath))
            {
                // dotnet-affected writes nothing when no project is affected.
                return Array.Empty<string>();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            return doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("Name", out var n) ? n.GetString() : null)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal);
        }
        finally
        {
            outDir.Delete(recursive: true);
        }
    }

    // Enforcing mode must fail loudly if the graph closure is unavailable (under-selecting would
    // silently skip real tests). Audit mode tolerates it: the curated map still selects and the
    // full matrix runs anyway.
    private static IReadOnlyCollection<string> Layer1Failed(RunOptions options, string detail)
    {
        if (options.Enforce)
        {
            throw new InvalidOperationException(
                $"Layer 1 (dotnet-affected) failed and --enforce is set: {detail}");
        }

        Console.Error.WriteLine(
            $"warning: skipping Layer 1 (graph closure): {detail}{Environment.NewLine}" +
            "Ensure 'dotnet tool restore' ran (or pass --dotnet-affected) before enforcing.");
        return Array.Empty<string>();
    }

    private static void WriteMatrix(
        RunOptions options,
        JsonNode matrix,
        JsonArray includeEntries,
        SelectionResult result,
        bool emitFiltered)
    {
        JsonNode outputMatrix;
        if (!emitFiltered)
        {
            outputMatrix = matrix;
        }
        else
        {
            var filtered = new JsonArray();
            foreach (var entry in includeEntries.ToList())
            {
                var name = (string?)entry?["projectName"];
                if (name is not null && result.TestProjects.Contains(name))
                {
                    // Detach from the source array before re-parenting into the filtered one.
                    filtered.Add(entry!.DeepClone());
                }
            }
            outputMatrix = new JsonObject { ["include"] = filtered };
        }

        var json = outputMatrix.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        if (options.Output is null)
        {
            Console.WriteLine(json);
        }
        else
        {
            File.WriteAllText(options.Output, json);
        }
    }

    // Per-job booleans for the if: conditions gating the non-.NET jobs. job:extension-e2e ->
    // run_extension_e2e, etc. Emitted for every job the map knows, so unselected ones are 'false'.
    // In audit mode (default) every boolean is forced 'true' to match the full matrix that
    // WriteMatrix emits: audit computes and reports the real selection (see WriteSummary) but then
    // runs everything, so the non-.NET jobs must not be gated off either.
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"- layer 1 (dotnet-affected): {(options.SkipLayer1 || options.ForceAll ? "skipped" : $"{layer1Affected.Count} affected project(s) (production + test)")}");
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
        // The would-have-been-skipped set is the whole point of audit mode: what selective CI drops.
        AppendProjectList(sb, "Would have been skipped", skipped);

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
            var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (summaryPath is not null)
            {
                File.AppendAllText(summaryPath, builder.ToString());
            }
            else
            {
                Console.Error.Write(builder.ToString());
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
