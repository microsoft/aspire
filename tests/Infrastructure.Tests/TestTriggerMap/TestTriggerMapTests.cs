// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.SelectTests;
using Microsoft.Extensions.FileSystemGlobbing;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Keeps the curated <c>eng/github-ci/test-trigger-map.yml</c> honest against repo reality.
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

        // affected_project_rules: each entry needs at least one project name glob and at least one target.
        foreach (var rule in s_map.AffectedProjectRules)
        {
            var label = rule.Projects.Count > 0 ? rule.Projects[0] : "(no projects)";
            if (rule.Projects.Count == 0 || rule.Projects.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"affected_project_rules entry '{label}' has an empty project glob");
            }
            if (rule.Targets.Count == 0 || rule.Targets.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"affected_project_rules entry '{label}' has no targets");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void EverySourceProjectIsReachableByLayer1OrACuratedRule()
    {
        // The graph closure is owned by the Layer 1 graph (GraphAffectedProjects), which discovers
        // projects from Aspire.slnx. So a src project is "covered" if it is in the solution (∴ Layer 1 sees it)
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

    // Repo-relative '/'-separated project paths listed in Aspire.slnx (the Layer 1 graph root).
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
        // A matrix test project that is NOT in Aspire.slnx is invisible to Layer 1 (the graph only
        // walks the solution), so a change to a production dependency could never fan into it --
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
    public void EveryAffectedProjectRuleGlobMatchesASolutionProject()
    {
        // affected_project_rules key off the affected PROJECT set (Layer 1), matched by project-name
        // glob. Layer 1 can only ever report a project that is in Aspire.slnx, so a project glob
        // that matches no solution project name would silently select nothing — assert each matches
        // at least one. Project Name == the .csproj base name (what Layer 1 emits).
        var solutionProjectNames = LoadSolutionProjectPaths()
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToHashSet(StringComparer.Ordinal);

        var dead = s_map.AffectedProjectRules
            .SelectMany(r => r.Projects)
            .Distinct(StringComparer.Ordinal)
            .Where(pattern => !solutionProjectNames.Any(name =>
                System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: false)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(dead.Count == 0,
            $"affected_project_rules globs matching no project in Aspire.slnx: {string.Join(", ", dead)}");
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
        var output = GitCli.Run(RepoRoot.Path, "ls-files");
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
        // The job: vocabulary is small and curated, and must exactly match the targets referenced by
        // the map. Each token resolves either to a standalone workflow file or to job id(s) in
        // tests.yml (see the Target vocabulary table in test-trigger-map.md). Assert the map references
        // only known jobs, and that the thing each one points at still exists — so a renamed/removed
        // workflow or job fails loudly.
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
            ["job:winget-installer"] = () => JobExists("prepare_winget_installer_artifacts"),
            ["job:homebrew-installer"] = () => JobExists("prepare_homebrew_installer_artifacts"),
            ["job:nix-package"] = () => JobExists("nix_package"),
            ["job:deployment-e2e"] = () => WorkflowExists("deployment-tests.yml"),
        };

        var referenced = s_map.AllReferencedTargets()
            .Where(t => t.StartsWith("job:", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var unknown = referenced.Where(j => !expected.ContainsKey(j)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0, $"job: targets not in the known vocabulary: {string.Join(", ", unknown)}");

        var unreferenced = expected.Keys.Except(referenced).Order(StringComparer.Ordinal).ToList();
        Assert.True(unreferenced.Count == 0, $"known job: targets not referenced by the map: {string.Join(", ", unreferenced)}");

        var broken = expected.Where(kvp => referenced.Contains(kvp.Key) && !kvp.Value())
            .Select(kvp => kvp.Key).Order(StringComparer.Ordinal).ToList();
        Assert.True(broken.Count == 0, $"job: targets whose workflow/job no longer exists: {string.Join(", ", broken)}");
    }

    [Fact]
    public void DocumentedJobTargetVocabularyMatchesReferencedMapJobs()
    {
        var referenced = s_map.AllReferencedTargets()
            .Where(t => t.StartsWith("job:", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var documentation = File.ReadAllText(Path.Combine(RepoRoot.Path, "docs", "ci", "test-trigger-map.md"));
        var targetVocabulary = TextBetween(documentation, "## Target vocabulary", "## Rule categories");
        var documented = System.Text.RegularExpressions.Regex
            .Matches(targetVocabulary, @"^\| `(job:[a-z0-9-]+)` \|", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var map = File.ReadAllText(Path.Combine(RepoRoot.Path, "eng", "github-ci", "test-trigger-map.yml"));
        var mapVocabulary = TextBetween(map, "# Target vocabulary:", "version: 1");
        var documentedInMap = System.Text.RegularExpressions.Regex
            .Matches(mapVocabulary, @"^#\s+(job:[a-z0-9-]+)\s+", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(referenced.Order(StringComparer.Ordinal), documented.Order(StringComparer.Ordinal));
        Assert.Equal(referenced.Order(StringComparer.Ordinal), documentedInMap.Order(StringComparer.Ordinal));
    }

    public static TheoryData<string, string[]> AuditedLoosePathCases => new()
    {
        {
            ".github/workflows/prepare-installer-artifacts.yml",
            ["test:Infrastructure.Tests", "job:winget-installer", "job:homebrew-installer"]
        },
        {
            ".github/scripts/assert-extension-e2e-bridge-vsix.ps1",
            ["job:extension-unit"]
        },
        {
            ".vscode/launch.json",
            ["job:extension-unit"]
        },
        {
            ".vscode/tasks.json",
            ["job:extension-unit"]
        },
        {
            "eng/test-retry-patterns.json",
            ["test:Infrastructure.Tests"]
        },
        {
            "eng/scripts/aspire-skills-bundle.common.ps1",
            ["test:Infrastructure.Tests"]
        },
        {
            "eng/scripts/smoke-installed-cli.ps1",
            ["job:winget-installer"]
        },
        {
            "eng/scripts/smoke-installed-cli.sh",
            ["job:homebrew-installer"]
        },
        {
            "eng/scripts/load-cli-e2e-images.sh",
            ["test:Aspire.Cli.EndToEnd.Tests"]
        },
    };

    [Theory]
    [MemberData(nameof(AuditedLoosePathCases))]
    public void AuditedLoosePathSelectsExactConsumerSetWithoutRunAllFallback(string path, string[] expectedTargets)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll, $"{path} unexpectedly selected ALL: {result.EscalationReason}");
        Assert.Empty(result.UnmatchedFiles);

        var actualTargets = result.TestProjects.Select(name => $"test:{name}")
            .Concat(result.Jobs)
            .Order(StringComparer.Ordinal);
        Assert.Equal(expectedTargets.Order(StringComparer.Ordinal), actualTargets);
    }

    [Theory]
    [InlineData(".gitattributes")]
    [InlineData("eng/scripts/gha-testreport.ps1")]
    [InlineData("eng/scripts/split-test-projects-for-ci.ps1")]
    [InlineData("tools/ExtractTestPartitions/Program.cs")]
    public void BroadCiInputHasAnExplicitRunAllRule(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.True(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
    }

    [Theory]
    [InlineData("eng/scripts/verify-cli-npm-package.ps1")]
    [InlineData("eng/scripts/verify-cli-tool-nupkg.ps1")]
    [InlineData("eng/scripts/stabilization-smoke-init-restore.sh")]
    [InlineData("eng/generate-catalog.ps1")]
    [InlineData("eng/scripts/update-aspire-skills-bundle.ps1")]
    [InlineData("eng/scripts/verify-aspire-skills-bundle.ps1")]
    [InlineData("eng/scripts/cli-starter-validation.ps1")]
    public void PathHandledOutsideSelectorDoesNotForceTestSelection(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Empty(result.TestProjects);
        Assert.Empty(result.Jobs);
    }

    // Golden-scenario regression test for the PR #19486 selector bug (renamed-file handling).
    //
    // PR #19486 (https://github.com/microsoft/aspire/pull/19486) renamed several `eng/scripts/*`
    // helpers and, in the SAME commit, updated their own `test-trigger-map.yml` entries to reference
    // the new filenames. Two of those renames are pure/near-pure (git's default -M50% rename detector
    // recognizes them: R095 and R054 similarity); the third (`verify-aspire-skills-bundle.ps1`) was
    // rewritten heavily enough in the same commit (115 lines removed, 154 added -> ~30% content
    // similarity) that git's default -M50% threshold reports it as a plain delete+add, not a rename
    // (confirmed with `git diff -M10%`, which *does* detect it at R030).
    //
    // Because the map's own path_rule/ignore entries for these paths move to the new filenames in the
    // same commit, every old path -- rename-detected or not -- matches nothing at HEAD, so each is
    // treated as an ordinary unmatched leftover and forces the run-all fallback. This is empirically
    // verified against the real PR diff (base fd9bbf76b4, head 1e46e48122): the selector correctly
    // selects ALL for the same reasons production reported at that head SHA.
    //
    // An earlier version of this fix exempted the git-rename-detected old paths from the fallback
    // (trusting that their new paths' own evaluation already accounted for the moved content). That
    // exemption was removed after an audit found it could silently under-select tests for
    // cross-directory moves out of a shared-glob directory, and for renames into a destination the
    // prefilter drops before anything evaluates it (see docs/ci/test-trigger-map.md). Selecting ALL
    // for this in-place-rename-with-moved-map-entry case is the accepted, safe tradeoff -- an
    // unnecessary full run is strictly better than a silently skipped test.
    //
    // This test starts from the REAL map (its entries still reference the OLD names, same as checked
    // out on disk today) and builds a temp copy with those three entries rewritten to the NEW names --
    // simulating the map already updated for the rename, exactly as PR #19486's own commit left it (the
    // rename and the matching map update landed together). Replaying the rename diff (both the old and
    // new path of each file, as git reports for an R-record) against that up-to-date map reproduces the
    // production run at PR #19486's head SHA: the new paths are fully covered, but the old paths --
    // still present in the diff -- match nothing, which is what forces the run-all fallback below.
    [Fact]
    public void SameCommitRenameWithMapEntryMovedToNewPathForcesRunAll()
    {
        const string oldCommonPath = "eng/scripts/aspire-skills-bundle.common.ps1";
        const string newCommonPath = "eng/scripts/aspire-skills-bundles.common.ps1";
        const string oldUpdatePath = "eng/scripts/update-aspire-skills-bundle.ps1";
        const string newUpdatePath = "eng/scripts/update-aspire-skills-bundles.ps1";
        const string oldVerifyPath = "eng/scripts/verify-aspire-skills-bundle.ps1";
        const string newVerifyPath = "eng/scripts/verify-aspire-skills-bundles.ps1";

        var realMapText = File.ReadAllText(Path.Combine(RepoRoot.Path, "eng", "github-ci", "test-trigger-map.yml"));
        Assert.Contains(oldCommonPath, realMapText);
        Assert.Contains(oldUpdatePath, realMapText);
        Assert.Contains(oldVerifyPath, realMapText);

        var simulatedMapText = realMapText
            .Replace(oldCommonPath, newCommonPath, StringComparison.Ordinal)
            .Replace(oldUpdatePath, newUpdatePath, StringComparison.Ordinal)
            .Replace(oldVerifyPath, newVerifyPath, StringComparison.Ordinal);

        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var tempMapPath = Path.Combine(tempDir.FullName, "test-trigger-map.yml");
            File.WriteAllText(tempMapPath, simulatedMapText);

            var changedFiles = new[]
            {
                oldCommonPath, newCommonPath,
                oldUpdatePath, newUpdatePath,
                oldVerifyPath, newVerifyPath,
            };

            // Aspire.Cli (production) and Aspire.Cli.Tests (its test project) both changed, mirroring the
            // production-code portion of PR #19486's actual diff (agent-init/telemetry commands).
            var result = SelectWithRealMap(
                changedFiles,
                layer1Affected: ["Aspire.Cli", "Aspire.Cli.Tests"],
                mapPathOverride: tempMapPath);

            Assert.True(result.SelectsAll, $"expected all three old paths to force the run-all fallback, but selection was: {result.EscalationReason}");
            Assert.Contains(oldCommonPath, result.UnmatchedFiles);
            Assert.Contains(oldUpdatePath, result.UnmatchedFiles);
            Assert.Contains(oldVerifyPath, result.UnmatchedFiles);
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void SetupForTestsSelectionOutputsAreConsistentWithMap()
    {
        // The non-.NET job gates flow: SelectTests emits a `selection` JSON object keyed run_<job>
        // (job:winget-installer -> run_winget_installer); tests.yml's setup_for_tests unpacks each via
        // fromJSON(steps.select_tests.outputs.selection).run_<job> into a job output; and each gated job
        // reads needs.setup_for_tests.outputs.run_<job> == 'true'. A name that drifts anywhere in that
        // chain silently empties the output, the if: reads false, and the job NEVER RUNS behind a green
        // check -- the under-selection selective CI must never do. Pin the set-level invariants against
        // the real workflow + real map (per-job correctness is EachGatedJobConditionUsesItsOwnSelectionBoolean).
        var testsYml = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "tests.yml"));

        // Outputs setup_for_tests unpacks from the selection object, e.g.
        //   run_polyglot: ${{ fromJSON(steps.select_tests.outputs.selection).run_polyglot }}
        var declared = System.Text.RegularExpressions.Regex
            .Matches(testsYml, @"fromJSON\(steps\.select_tests\.outputs\.selection\)\.(run_[a-z0-9_]+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // run_<job> outputs consumed by a gate, e.g. needs.setup_for_tests.outputs.run_polyglot.
        var consumed = System.Text.RegularExpressions.Regex
            .Matches(testsYml, @"needs\.setup_for_tests\.outputs\.(run_[a-z0-9_]+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // run_<job> keys SelectTests can actually emit, derived from the real map's job: vocabulary --
        // mirrors Program.WriteJobBooleans ("run_" + token without "job:", '-' -> '_').
        var emittable = s_map.AllReferencedTargets()
            .Where(t => t.StartsWith("job:", StringComparison.Ordinal))
            .Select(t => "run_" + t["job:".Length..].Replace('-', '_'))
            .ToHashSet(StringComparer.Ordinal);
        var expectedAdvisoryJobs = s_map.AllReferencedTargets()
            .Where(t => t.StartsWith("job:", StringComparison.Ordinal))
            .Where(t => !declared.Contains("run_" + t["job:".Length..].Replace('-', '_')))
            .ToHashSet(StringComparer.Ordinal);
        var advisoryJobField = typeof(Selection).GetField(
            "s_advisoryJobTargets",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(advisoryJobField);
        var configuredAdvisoryJobs = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            advisoryJobField.GetValue(null));

        // Sanity: the regexes actually matched, so a pattern slip can't make the asserts vacuously pass.
        Assert.Contains("run_winget_installer", declared);
        Assert.Contains("run_winget_installer", consumed);
        Assert.Equal(
            expectedAdvisoryJobs.Order(StringComparer.Ordinal),
            configuredAdvisoryJobs.Keys.Order(StringComparer.Ordinal));

        // Finding 1: every unpacked output is a key the tool emits. A typo'd fromJSON property or a
        // renamed/removed map token leaves the output permanently empty (job silently skipped).
        var declaredNotEmittable = declared.Except(emittable).Order(StringComparer.Ordinal).ToList();
        Assert.True(declaredNotEmittable.Count == 0,
            $"setup_for_tests unpacks selection keys SelectTests never emits (no matching job: in the map): {string.Join(", ", declaredNotEmittable)}");

        // Finding 2: every gate reads an output setup_for_tests declares. A new gated job whose if:
        // references run_<job> but whose unpack line was forgotten reads an empty output -> never runs.
        var consumedNotDeclared = consumed.Except(declared).Order(StringComparer.Ordinal).ToList();
        Assert.True(consumedNotDeclared.Count == 0,
            $"job if: conditions read run_* outputs that setup_for_tests never unpacks: {string.Join(", ", consumedNotDeclared)}");

        // Dead output: a run_* setup_for_tests exposes that no gate reads is a leftover (or a job wired
        // to the wrong var). Every unpacked output must gate at least one job.
        var declaredUnused = declared.Except(consumed).Order(StringComparer.Ordinal).ToList();
        Assert.True(declaredUnused.Count == 0,
            $"setup_for_tests unpacks run_* outputs that no job if: consumes: {string.Join(", ", declaredUnused)}");
    }

    [Fact]
    public void EachGatedJobConditionUsesItsOwnSelectionBoolean()
    {
        // The set-level test above proves the run_* names line up, but not that each job gates on ITS
        // OWN boolean. The names are uniform (job:winget-installer -> run_winget_installer), so a
        // copy-paste leaving a job reading a sibling's var (e.g. the WinGet prep job on
        // run_homebrew_installer) passes every set check yet runs the wrong job for a change. Pin the
        // job-id -> run_<job> binding for the tests.yml jobs each gated job: token resolves to.
        //
        // This list is hand-maintained intent (like EveryJobTargetResolvesToExistingWorkflowOrJob's
        // job-id mapping): a new gated job must be added here too. The check is substring containment,
        // so it catches a job NOT referencing its own var; it intentionally tolerates extra vars -- the
        // extension jobs additionally OR-in run_extension_e2e because extension_e2e_tests needs the
        // VSIX and the corepack bootstrap they produce.
        var testsYml = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "tests.yml"));

        var bindings = new[]
        {
            ("polyglot_validation", "run_polyglot"),
            ("typescript_sdk_tests", "run_typescript_sdk"),
            ("typescript_api_compat", "run_typescript_api_compat"),
            ("extension_tests_win", "run_extension_unit"),
            ("extension_bootstrap_linux", "run_extension_unit"),
            ("extension_e2e_tests", "run_extension_e2e"),
            ("prepare_winget_installer_artifacts", "run_winget_installer"),
            ("prepare_homebrew_installer_artifacts", "run_homebrew_installer"),
            ("nix_package", "run_nix_package"),
        };

        var wrong = new List<string>();
        foreach (var (jobId, runVar) in bindings)
        {
            var block = JobBlock(testsYml, jobId);
            Assert.True(block is not null, $"job '{jobId}' not found in tests.yml");
            if (!block!.Contains($"needs.setup_for_tests.outputs.{runVar}", StringComparison.Ordinal))
            {
                wrong.Add($"{jobId} (expected {runVar})");
            }
        }

        Assert.True(wrong.Count == 0,
            $"gated jobs whose if: does not reference their own selection boolean: {string.Join("; ", wrong)}");
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

    [Fact]
    public void PrefilterPatternsFileExists()
    {
        // The prefilter reads its pattern list from this file at runtime (SSOT with the ci.yml skip
        // gate). A typo'd path would make ChangedFileFilter.Create throw at runtime; pin it here.
        Assert.NotNull(s_map.Prefilter);
        Assert.False(string.IsNullOrWhiteSpace(s_map.Prefilter!.PatternsFile));
        Assert.True(File.Exists(Path.Combine(RepoRoot.Path, s_map.Prefilter.PatternsFile!)),
            $"prefilter.patterns_file does not exist: {s_map.Prefilter.PatternsFile}");
    }

    [Fact]
    public void PrefilterKeepRoutedGlobsAreLiveAndCoverSelectorRoutedDirs()
    {
        // keep_routed are the carve-outs: files the patterns file lists but the selector routes to a
        // target, so they must NOT be dropped. Each must match a real file (catch typos), and the two
        // selector-routed dirs must be covered so a workflow/pipeline change still runs Infrastructure
        // .Tests in a mixed PR.
        Assert.NotNull(s_map.Prefilter);
        var keepRouted = s_map.Prefilter!.KeepRouted;

        var dead = keepRouted.Where(g => !GlobMatchesAnyTrackedFile(g)).Order(StringComparer.Ordinal).ToList();
        Assert.True(dead.Count == 0, $"prefilter.keep_routed globs matching no tracked file: {string.Join(", ", dead)}");

        foreach (var required in new[] { ".github/workflows/**", "eng/pipelines/**" })
        {
            Assert.Contains(required, keepRouted);
        }
    }

    [Fact]
    public void EveryTestsSharedFileIsCsAttributedOrRoutedToAllOrExcluded()
    {
        // tests/Shared has both link-compiled *.cs (Layer 1 attributes them precisely, so the run-all
        // fallback treats them as owned — they need no curated rule) and non-source build/fixture infra
        // (props/targets/packages/Docker/Playwright/certs -> ALL). A tests/Shared file that is NEITHER a
        // *.cs NOR matched by an ALL path rule NOR a doc (excluded by the prefilter) would silently select
        // no targeted work: it is not under a project dir (so directory containment can't attribute a
        // loose file there) and would force the run-all fallback. Pin the invariant so a new file type
        // added under tests/Shared can't quietly expand to ALL. (.md files are dropped by the prefilter's
        // **.md.)
        var allGlobs = s_map.PathRules
            .Where(r => r.Targets.Contains("ALL", StringComparer.Ordinal))
            .SelectMany(r => r.Paths)
            .ToList();

        bool RoutedToAll(string file) => allGlobs.Any(g => TestTriggerMap.GlobMatches(g, file));

        var orphaned = s_trackedFiles
            .Where(f => f.StartsWith("tests/Shared/", StringComparison.Ordinal))
            .Where(f => !f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.EndsWith(".md", StringComparison.Ordinal))
            .Where(f => !RoutedToAll(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(orphaned.Count == 0,
            $"tests/Shared files that are neither *.cs (Layer 1-attributed), *.md (prefiltered), nor routed " +
            $"to ALL (they would silently select nothing): {string.Join(", ", orphaned)}");
    }

    [Fact]
    public void EveryLocalActionUsedByAWorkflowIsRoutedToAll()
    {
        // A local composite action (.github/actions/<name>) is not a project, so Layer 1 never attributes
        // a change to it. An action that no path rule matches therefore forces the run-all fallback, but
        // its CI-wide impact should be explicit rather than appearing as an unattributed audit warning.
        // The map routes .github/actions/** -> ALL to cover this; pin the invariant so a new action
        // referenced from a workflow can't lose that explicit ownership if the rule is narrowed.
        // Failure mode: drop the .github/actions/** ALL rule and this goes red.
        var allGlobs = s_map.PathRules
            .Where(r => r.Targets.Contains("ALL", StringComparer.Ordinal))
            .SelectMany(r => r.Paths)
            .ToList();

        bool RoutedToAll(string file) => allGlobs.Any(g => TestTriggerMap.GlobMatches(g, file));

        // `uses: ./.github/actions/<name>` (with optional surrounding quotes / a trailing @ref) names a
        // local composite action whose definition lives at .github/actions/<name>/action.yml.
        var usesLocalAction = new System.Text.RegularExpressions.Regex(
            @"uses:\s*['""]?\./\.github/actions/(?<name>[^\s'""@]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var workflowsDir = Path.Combine(RepoRoot.Path, ".github", "workflows");
        var referencedActions = Directory.EnumerateFiles(workflowsDir, "*.yml")
            .SelectMany(f => usesLocalAction.Matches(File.ReadAllText(f)).Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Sanity: the scan finds the actions we know are referenced, so a regex slip can't make this
        // assertion vacuously pass.
        Assert.Contains("enumerate-tests", referencedActions);

        var unrouted = referencedActions
            .Where(name => !RoutedToAll($".github/actions/{name}/action.yml"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(unrouted.Count == 0,
            $"local actions referenced by a workflow but not routed to ALL (a change to them would select " +
            $"ALL only through the unattributed fallback): {string.Join(", ", unrouted)}");
    }

    private static SelectionResult SelectWithRealMap(string path)
        => SelectWithRealMap([path], []);

    /// <summary>
    /// Same as the single-path overload, but exercises the full <see cref="TestSelector.Select"/>
    /// signature (multiple changed files and Layer 1 affected projects) against the real map and real
    /// <c>Aspire.slnx</c> project directories.
    /// </summary>
    /// <param name="mapPathOverride">
    /// When set, load the map from this path instead of the checked-out
    /// <c>eng/github-ci/test-trigger-map.yml</c>. Used to simulate "the real map as it will look after
    /// a specific in-flight PR's own map.yml edit lands", without depending on that PR having merged.
    /// </param>
    private static SelectionResult SelectWithRealMap(
        IReadOnlyCollection<string> paths,
        IReadOnlyCollection<string> layer1Affected,
        string? mapPathOverride = null)
    {
        var projectPaths = LoadSolutionProjectPaths();
        var testProjects = projectPaths
            .Where(projectPath => projectPath.StartsWith("tests/", StringComparison.Ordinal))
            .Select(projectPath => Path.GetFileNameWithoutExtension(projectPath)!)
            .Where(name => name.EndsWith(".Tests", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var projectDirectories = projectPaths
            .Select(projectPath => Path.GetDirectoryName(projectPath)!.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        var mapPath = mapPathOverride ?? Path.Combine(RepoRoot.Path, "eng", "github-ci", "test-trigger-map.yml");
        var selector = new TestSelector(mapPath, testProjects, projectDirectories);

        return selector.Select(paths, layer1Affected, new SelectorOptions());
    }

    private static string TextBetween(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");

        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end >= 0, $"End marker not found after {startMarker}: {endMarker}");

        return text[start..end];
    }

    // Text of a top-level tests.yml job block: from "\n  <id>:" up to the next line indented exactly
    // two spaces that starts a new key (the next job), or end of file. Top-level jobs sit at two-space
    // indent and everything inside a job is indented further, so a two-space `word:` reliably delimits
    // the block. Returns null when the job id is absent.
    private static string? JobBlock(string workflow, string jobId)
    {
        var marker = $"\n  {jobId}:";
        var start = workflow.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var next = System.Text.RegularExpressions.Regex.Match(
            workflow[(start + marker.Length)..],
            @"\n  [A-Za-z0-9_]+:");
        return next.Success
            ? workflow.Substring(start, marker.Length + next.Index)
            : workflow[start..];
    }
}
