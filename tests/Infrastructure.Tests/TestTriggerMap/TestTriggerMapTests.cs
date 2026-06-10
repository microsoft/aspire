// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.FileSystemGlobbing;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Keeps the curated <c>docs/ci/test-trigger-map.yml</c> honest against repo reality.
/// The map is hand-maintained, so these tests fail loudly when a project/job is renamed
/// or removed, when a curated path is typo'd, or when a new source project is added that
/// no rule maps to.
/// </summary>
public sealed class TestTriggerMapTests
{
    private static readonly TestTriggerMap s_map = TestTriggerMap.Load(RepoRoot.Path);

    // Repo-relative, '/'-separated tracked file paths (git ls-files). Source of truth for
    // "does this glob match a real file" and "what source projects exist". Loaded once.
    private static readonly IReadOnlyList<string> s_trackedFiles = LoadTrackedFiles();

    [Fact]
    public void MapLoadsWithExpectedVersion()
    {
        Assert.Equal(1, s_map.Version);
    }

    [Fact]
    public void LinkCompiledFilesSelectTheirConsumingTests()
    {
        // A test project that <Compile Include>s a foreign src/ file depends on that file even
        // though there is no ProjectReference edge. If the file changes, the map must still
        // select that test — otherwise the test silently never runs for that change. This is the
        // exact failure the shared_compiled_source section exists to prevent; assert the map
        // covers every such (file -> test) edge discovered from the test .csproj files.
        var gaps = new List<string>();

        foreach (var testCsproj in s_trackedFiles.Where(IsTestProjectFile))
        {
            var projectName = Path.GetFileNameWithoutExtension(testCsproj);
            var projectDir = testCsproj[..testCsproj.LastIndexOf('/')];

            foreach (var compiledFile in ForeignSourceCompileIncludes(testCsproj, projectDir))
            {
                var selected = s_map.SelectTestProjects(compiledFile, out var selectsAll);
                if (!selectsAll && !selected.Contains(projectName))
                {
                    gaps.Add($"{compiledFile} -> {projectName}");
                }
            }
        }

        gaps.Sort(StringComparer.Ordinal);
        Assert.True(gaps.Count == 0,
            $"test projects that <Compile Include> a src file the map does not select on change:{Environment.NewLine}{string.Join(Environment.NewLine, gaps)}");
    }

    private static bool IsTestProjectFile(string path) =>
        path.StartsWith("tests/", StringComparison.Ordinal) &&
        path.EndsWith(".csproj", StringComparison.Ordinal) &&
        // tests/<Name>/<Name>.csproj — the matrix project, not nested helper csprojs.
        path.Count(c => c == '/') == 2 &&
        path[(path.LastIndexOf('/') + 1)..] == $"{path.Split('/')[1]}.csproj";

    // Returns repo-relative '/'-separated src/ files that <projectDir> link-compiles from another
    // project. Files under src/Shared (compiled everywhere) are excluded: run_all_globs covers them.
    private static IEnumerable<string> ForeignSourceCompileIncludes(string csprojRelPath, string projectDir)
    {
        var xml = File.ReadAllText(Path.Combine(RepoRoot.Path, csprojRelPath));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml, "<Compile\\s+Include=\"([^\"]+)\""))
        {
            var include = ExpandMSBuildVars(m.Groups[1].Value.Replace('\\', '/'), projectDir);
            var resolved = NormalizeRepoRelative(projectDir, include);
            if (resolved.StartsWith("src/", StringComparison.Ordinal) &&
                !resolved.StartsWith("src/Shared/", StringComparison.Ordinal))
            {
                yield return resolved;
            }
        }
    }

    // MSBuild properties used in Compile Include paths across the repo's test projects.
    private static string ExpandMSBuildVars(string path, string projectDir) => path
        .Replace("$(RepoRoot)", "")
        .Replace("$(SharedDir)", "src/Shared/")
        .Replace("$(ComponentsDir)", "src/Components/")
        .Replace("$(VendoringDir)", "src/Vendoring/")
        .Replace("$(TestsSharedDir)", "tests/Shared/")
        .Replace("$(MSBuildThisFileDirectory)", projectDir + "/");

    private static string NormalizeRepoRelative(string projectDir, string include)
    {
        // Absolute repo-relative already (after var expansion), else relative to the project dir.
        var combined = include.StartsWith("src/", StringComparison.Ordinal) || include.StartsWith("tests/", StringComparison.Ordinal)
            ? include
            : $"{projectDir}/{include}";

        var parts = new List<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") { continue; }
            if (segment == ".." && parts.Count > 0) { parts.RemoveAt(parts.Count - 1); }
            else if (segment != "..") { parts.Add(segment); }
        }
        return string.Join('/', parts);
    }

    [Fact]
    public void RulesHaveNoEmptyOrDuplicatePaths()
    {
        // Structural hygiene: a path rule with no globs, or globs but no targets, is dead weight
        // that silently selects nothing; a duplicate leaf glob means a source project is mapped
        // twice (a merge/edit slip). Catch all three before they rot the map.
        var problems = new List<string>();

        foreach (var rule in s_map.AllPathRules())
        {
            var label = rule.Paths.Count > 0 ? rule.Paths[0] : "(no paths)";
            if (rule.Paths.Count == 0 || rule.Paths.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"rule '{label}' has an empty path glob");
            }
            if (rule.Targets.Count == 0 || rule.Targets.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"rule '{label}' has no targets");
            }
        }

        foreach (var job in s_map.CuratedJobs)
        {
            if (string.IsNullOrWhiteSpace(job.Target))
            {
                problems.Add("a curated_jobs entry has no target");
            }
            if (job.Paths.Count == 0 || job.Paths.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"curated job '{job.Target}' has an empty path glob");
            }
        }

        var dupeLeafGlobs = s_map.LeafSource.Concat(s_map.SharedCompiledSource)
            .SelectMany(r => r.Paths)
            .GroupBy(p => p, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (dupeLeafGlobs.Count > 0)
        {
            problems.Add($"duplicate leaf/compiled globs: {string.Join(", ", dupeLeafGlobs)}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void EverySourceProjectIsReachableBySomeRule()
    {
        // A new src/ project that no rule maps to would silently never run any test. Require
        // every source .csproj to be matched by at least one selecting glob, so adding an
        // unmapped project fails here instead of going untested in CI.
        var selecting = new Matcher(StringComparison.Ordinal);
        foreach (var glob in s_map.AllSelectingGlobs())
        {
            selecting.AddInclude(glob);
        }
        var covered = selecting.Match(s_trackedFiles).Files
            .Select(f => f.Path)
            .ToHashSet(StringComparer.Ordinal);

        var uncovered = s_trackedFiles
            .Where(f => f.StartsWith("src/", StringComparison.Ordinal) && f.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(csproj => !covered.Contains(csproj))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(uncovered.Count == 0, $"src projects not reachable by any rule: {string.Join(", ", uncovered)}");
    }

    [Fact]
    public void EveryGlobMatchesAtLeastOneTrackedFile()
    {
        // Every rule glob (catch-all, rule paths, curated-job paths, and the known-gaps paths)
        // must match a real, git-tracked file. A typo'd path or a renamed/removed source folder
        // would otherwise sit in the map selecting nothing — a silent hole the selector can't see.
        var globs = s_map.AllSelectingGlobs()
            .Concat(s_map.Gaps.SelectMany(g => g.Paths))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var deadGlobs = globs.Where(g => !GlobMatchesAnyTrackedFile(g)).ToList();

        Assert.True(deadGlobs.Count == 0, $"globs matching no tracked file: {string.Join(", ", deadGlobs)}");
    }

    private static bool GlobMatchesAnyTrackedFile(string glob)
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude(glob);
        return matcher.Match(s_trackedFiles).HasMatches;
    }

    private static IReadOnlyList<string> LoadTrackedFiles()
    {
        var psi = new ProcessStartInfo("git", "ls-files")
        {
            WorkingDirectory = RepoRoot.Path,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 'git ls-files'.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'git ls-files' exited with code {process.ExitCode}.");
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [Fact]
    public void AliasesResolveToValidTestTargets()
    {
        // Aliases (ALL_HOSTING_TESTS / ALL_COMPONENT_TESTS) are expanded into concrete test:
        // targets by a consumer. Each member must be a well-formed test: target (existence is
        // covered by EveryTestTargetNamesAnExistingTestProject, which includes alias members),
        // an alias must not nest another alias, and must be non-empty with no duplicates.
        foreach (var (name, members) in s_map.Aliases)
        {
            Assert.True(members.Count > 0, $"alias {name} is empty");

            var bad = members.Where(m => !m.StartsWith("test:", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToList();
            Assert.True(bad.Count == 0, $"alias {name} has non-test: members: {string.Join(", ", bad)}");

            var dupes = members.GroupBy(m => m, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
            Assert.True(dupes.Count == 0, $"alias {name} has duplicate members: {string.Join(", ", dupes)}");
        }

        // Every alias-like token used as a target (uppercase, not test:/job:) is either the
        // ALL sentinel or a defined alias — so a typo'd alias reference fails loudly.
        var undefined = s_map.AllReferencedTargets()
            .Where(t => !t.StartsWith("test:", StringComparison.Ordinal) && !t.StartsWith("job:", StringComparison.Ordinal))
            .Where(t => t != "ALL" && !s_map.Aliases.ContainsKey(t))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.True(undefined.Count == 0, $"undefined alias references: {string.Join(", ", undefined)}");
    }

    [Fact]
    public void EveryJobTargetMapsToAnExistingWorkflowOrJob()
    {
        // The job: vocabulary is small and curated. Each one resolves either to a standalone
        // workflow file or to job id(s) in tests.yml (see the Target vocabulary table in
        // test-trigger-map.md). Assert the map references only known jobs, and that the thing
        // each one points at still exists — so a renamed/removed workflow or job fails loudly.
        var workflowsDir = Path.Combine(RepoRoot.Path, ".github", "workflows");
        var testsYml = File.ReadAllText(Path.Combine(workflowsDir, "tests.yml"));

        bool WorkflowExists(string file) => File.Exists(Path.Combine(workflowsDir, file));
        bool JobExists(string id) => testsYml.Contains($"\n  {id}:", StringComparison.Ordinal);

        // job: target -> predicate that its evidence still exists.
        var expected = new Dictionary<string, Func<bool>>(StringComparer.Ordinal)
        {
            ["job:polyglot"] = () => WorkflowExists("polyglot-validation.yml") && JobExists("polyglot_validation"),
            ["job:typescript-sdk"] = () => WorkflowExists("typescript-sdk-tests.yml") && JobExists("typescript_sdk_tests"),
            ["job:typescript-api-compat"] = () => WorkflowExists("typescript-api-compat.yml") && JobExists("typescript_api_compat"),
            ["job:extension-unit"] = () => JobExists("extension_tests_win") && JobExists("extension_bootstrap_linux"),
            ["job:extension-e2e"] = () => WorkflowExists("extension-e2e-tests.yml"),
            ["job:cli-starter"] = () => JobExists("cli_starter_validation_windows"),
            ["job:api-diffs"] = () => WorkflowExists("generate-api-diffs.yml"),
            ["job:ats-diffs"] = () => WorkflowExists("generate-ats-diffs.yml"),
            ["job:deployment-e2e"] = () => WorkflowExists("deployment-tests.yml"),
        };

        var referenced = s_map.AllReferencedTargets()
            .Where(t => t.StartsWith("job:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unknown = referenced.Where(j => !expected.ContainsKey(j)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0, $"job: targets not in the known vocabulary: {string.Join(", ", unknown)}");

        var broken = expected.Where(kvp => referenced.Contains(kvp.Key) && !kvp.Value())
            .Select(kvp => kvp.Key).Order(StringComparer.Ordinal).ToList();
        Assert.True(broken.Count == 0, $"job: targets whose workflow/job no longer exists: {string.Join(", ", broken)}");
    }

    [Fact]
    public void EveryTestTargetNamesAnExistingTestProject()
    {
        // test:<X> means the matrix project tests/<X> (run-tests.yml entry). A rename or
        // removal that the curated map misses would silently stop selecting that project,
        // so require tests/<X>/<X>.csproj to exist on disk.
        var missing = s_map.AllReferencedTargets()
            .Where(t => t.StartsWith("test:", StringComparison.Ordinal))
            .Select(t => t["test:".Length..])
            .Distinct(StringComparer.Ordinal)
            .Where(name => !File.Exists(Path.Combine(RepoRoot.Path, "tests", name, $"{name}.csproj")))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, $"test: targets with no tests/<X>/<X>.csproj: {string.Join(", ", missing)}");
    }
}
