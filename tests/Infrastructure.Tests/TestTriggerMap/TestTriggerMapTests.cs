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
    public void RulesAreWellFormed()
    {
        // Structural hygiene: a path rule with no globs, or globs but no targets, is dead weight
        // that silently selects nothing. (Overlapping globs ACROSS path_rules are expected and
        // fine -- one path may map to several targets via several rules -- so there is no
        // duplicate-glob check here.)
        var problems = new List<string>();

        foreach (var rule in s_map.PathRules)
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

        // conventions: each entry needs a pattern with a <name> placeholder and a target that also
        // carries <name> (so the capture is actually substituted). Duplicate patterns are slips.
        foreach (var convention in s_map.Conventions)
        {
            if (string.IsNullOrWhiteSpace(convention.Pattern) || !convention.Pattern.Contains("<name>", StringComparison.Ordinal))
            {
                problems.Add($"conventions entry '{convention.Pattern}' has no <name> placeholder");
            }
            if (string.IsNullOrWhiteSpace(convention.Target) || !convention.Target.Contains("<name>", StringComparison.Ordinal))
            {
                problems.Add($"conventions pattern '{convention.Pattern}' has a target that does not substitute <name>");
            }
        }

        var dupeConventionPatterns = s_map.Conventions
            .GroupBy(c => c.Pattern, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
        if (dupeConventionPatterns.Count > 0)
        {
            problems.Add($"duplicate conventions patterns: {string.Join(", ", dupeConventionPatterns)}");
        }

        // ignore globs must be non-empty.
        if (s_map.Ignore.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("an ignore glob is empty");
        }

        // derived_targets: each entry needs at least one test: trigger and at least one target.
        foreach (var derived in s_map.DerivedTargets)
        {
            if (derived.Tests.Count == 0 || derived.Tests.Any(t => string.IsNullOrWhiteSpace(t) || !t.StartsWith("test:", StringComparison.Ordinal)))
            {
                problems.Add($"derived_targets entry has an invalid/empty tests list: [{string.Join(", ", derived.Tests)}]");
            }
            if (derived.Targets.Count == 0 || derived.Targets.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"derived_targets entry [{string.Join(", ", derived.Tests)}] has no targets");
            }
        }

        // project_rules: each entry needs at least one project name glob and at least one target.
        foreach (var rule in s_map.ProjectRules)
        {
            var label = rule.Projects.Count > 0 ? rule.Projects[0] : "(no projects)";
            if (rule.Projects.Count == 0 || rule.Projects.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"project_rules entry '{label}' has an empty project glob");
            }
            if (rule.Targets.Count == 0 || rule.Targets.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"project_rules entry '{label}' has no targets");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void EverySourceProjectIsReachableByLayer1OrACuratedRule()
    {
        // The graph closure is owned by dotnet-affected (Layer 1), which discovers projects from
        // Aspire.slnx. So a src project is "covered" if it is in the solution (∴ Layer 1 sees it)
        // OR matched by a curated glob (the deliberately out-of-slnx ones — e.g. the template
        // placeholders that crash discovery — are covered by loose_file_deps). A new src project
        // that is neither in the solution nor curated would silently never run any test, so it
        // must fail here.
        var inSolution = LoadSolutionProjectPaths();

        var selecting = new Matcher(StringComparison.Ordinal);
        foreach (var glob in s_map.AllSelectingGlobs())
        {
            selecting.AddInclude(glob);
        }
        var curatedCovered = selecting.Match(s_trackedFiles).Files
            .Select(f => f.Path)
            .ToHashSet(StringComparer.Ordinal);

        var uncovered = s_trackedFiles
            .Where(f => f.StartsWith("src/", StringComparison.Ordinal) && f.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(csproj => !inSolution.Contains(csproj) && !curatedCovered.Contains(csproj))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(uncovered.Count == 0,
            $"src projects neither in Aspire.slnx nor matched by a curated rule: {string.Join(", ", uncovered)}");
    }

    // Repo-relative '/'-separated project paths listed in Aspire.slnx (the dotnet-affected root).
    private static IReadOnlySet<string> LoadSolutionProjectPaths()
    {
        var slnx = File.ReadAllText(Path.Combine(RepoRoot.Path, "Aspire.slnx"));
        // <Project Path="src/Foo/Foo.csproj" /> — paths use '/' in the slnx already.
        return System.Text.RegularExpressions.Regex.Matches(slnx, "Path=\"([^\"]+\\.csproj)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void EveryTestProjectIsInTheSolutionSoLayer1CanSelectIt()
    {
        // A matrix test project that is NOT in Aspire.slnx is invisible to Layer 1 (dotnet-affected
        // only walks the solution), so a change to a production dependency could never fan into it --
        // it would silently never run in enforcing mode. Require every tests/<Name>/<Name>.csproj to
        // be in the solution. (This invariant is what the Infrastructure.Tests / Aspire.Hosting.Maui
        // .Tests additions satisfied; before them, both were silent Layer-1 blind spots.) Add to the
        // allow-list only for a project deliberately kept out of PR CI, with a reason.
        var allowList = new HashSet<string>(StringComparer.Ordinal)
        {
            // (none today)
        };

        var inSolution = LoadSolutionProjectPaths();
        var testsRoot = Path.Combine(RepoRoot.Path, "tests");

        var missing = Directory.EnumerateDirectories(testsRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => $"tests/{name}/{name}.csproj")
            .Where(rel => File.Exists(Path.Combine(RepoRoot.Path, rel)))
            .Where(rel => !inSolution.Contains(rel) && !allowList.Contains(rel))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            $"test projects not in Aspire.slnx (Layer 1 cannot select them, so a production-dependency " +
            $"change would silently skip their tests): {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryProjectRuleGlobMatchesASolutionProject()
    {
        // project_rules key off the affected PROJECT set (Layer 1), matched by project-name glob.
        // dotnet-affected can only ever report a project that is in Aspire.slnx, so a project glob
        // that matches no solution project name would silently select nothing — assert each matches
        // at least one. Project Name == the .csproj base name (what dotnet-affected emits).
        var solutionProjectNames = LoadSolutionProjectPaths()
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToHashSet(StringComparer.Ordinal);

        var dead = s_map.ProjectRules
            .SelectMany(r => r.Projects)
            .Distinct(StringComparer.Ordinal)
            .Where(pattern => !solutionProjectNames.Any(name =>
                System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: false)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(dead.Count == 0,
            $"project_rules globs matching no project in Aspire.slnx: {string.Join(", ", dead)}");
    }

    [Fact]
    public void EveryGlobMatchesAtLeastOneTrackedFile()
    {
        // Every rule glob (catch-all, path-rule paths, convention patterns, ignore globs) must match
        // a real, git-tracked file. A typo'd path or a renamed/removed source folder would otherwise
        // sit in the map selecting nothing — a silent hole the selector can't see.
        var globs = s_map.AllSelectingGlobs()
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
    public void GroupsResolveToValidTargets()
    {
        // Named groups (e.g. CLI_BUNDLE) expand into concrete test:/job: targets by a consumer.
        // Each member must be a well-formed test: or job: target (existence is covered by
        // EveryTestTargetNamesAnExistingTestProject / EveryJobTargetMapsToAnExistingWorkflowOrJob,
        // which include group members). The real map keeps groups flat (no nesting); the selector
        // engine supports recursive expansion, exercised by the synthetic acceptance tests.
        foreach (var (name, members) in s_map.Groups)
        {
            Assert.True(members.Count > 0, $"group {name} is empty");

            var bad = members.Where(m => !m.StartsWith("test:", StringComparison.Ordinal) && !m.StartsWith("job:", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToList();
            Assert.True(bad.Count == 0, $"group {name} has members that are neither test: nor job:: {string.Join(", ", bad)}");

            var dupes = members.GroupBy(m => m, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
            Assert.True(dupes.Count == 0, $"group {name} has duplicate members: {string.Join(", ", dupes)}");
        }

        // Every group-like token used as a target (uppercase, not test:/job:) is either the
        // ALL sentinel or a defined group — so a typo'd group reference fails loudly.
        var undefined = s_map.AllReferencedTargets()
            .Where(t => !t.StartsWith("test:", StringComparison.Ordinal) && !t.StartsWith("job:", StringComparison.Ordinal))
            .Where(t => t != "ALL" && !s_map.Groups.ContainsKey(t))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.True(undefined.Count == 0, $"undefined group references: {string.Join(", ", undefined)}");
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
            ["job:winget-installer"] = () => JobExists("prepare_winget_installer_artifacts"),
            ["job:homebrew-installer"] = () => JobExists("prepare_homebrew_installer_artifacts"),
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
