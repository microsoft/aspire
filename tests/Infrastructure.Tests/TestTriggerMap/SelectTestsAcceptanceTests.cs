// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.SelectTests;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Behavior spec for the <see cref="TestSelector"/> engine. These tests drive the selector with
/// small SYNTHETIC maps (a temp <c>map.yml</c> + a fake matrix + fake project dirs), so they assert
/// the resolution mechanisms — conventions, overrides, ignore, Layer-1 attribution, the run-all
/// fallback, derived targets, and group expansion — without coupling to the contents of the real
/// <c>eng/test-trigger-map.yml</c>. A thin set of real-map invariant smokes (computed from the
/// filesystem, never hardcoding project names) lives at the end; structural invariants of the real
/// map are covered by <see cref="TestTriggerMapTests"/>.
/// </summary>
public sealed class SelectTestsAcceptanceTests
{
    // A synthetic map exercising every section. Test projects referenced here are supplied (or
    // withheld) via the per-test matrix to drive the existence guard.
    private const string SyntheticMap = """
        version: 1
        groups:
          MIXED: [test:GroupTest, job:group-job]
          ONLYTESTS: [test:T1, test:T2]
          ONLYJOBS: [job:j1, job:j2]
          OUTER: [test:T1, INNER]
          INNER: [job:inner-job, test:T2]
          CYCA: [test:T1, CYCB]
          CYCB: [test:T2, CYCA]
          HASALL: [ALL]
        conventions:
          - pattern: tests/<name>/**
            target: test:<name>
          - pattern: src/Aspire.Hosting.<name>/**
            target: test:Aspire.Hosting.<name>.Tests
          - pattern: src/Components/<name>/**
            target: test:<name>.Tests
        ignore:
          - src/Shared/Inert/**
          - src/Both/**
        path_rules:
          - paths: [global.json]
            targets: [ALL]
          - paths: [src/Aspire.Hosting.Azure.*/**]
            targets: [test:Aspire.Hosting.Azure.Tests]
          - paths: [src/Aspire.Hosting.*/**]
            targets: [job:hostjob]
          - paths: [src/CuratedThing/**]
            targets: [job:cjob]
          - paths: [eng/installer/**]
            targets: [test:InstallerTests, job:installer]
          - paths: [src/Both/**]
            targets: [test:RealOne]
          - paths: [src/grp/**]
            targets: [MIXED]
          - paths: [src/grp2/**]
            targets: [MIXED]
          - paths: [src/allgrp/**]
            targets: [HASALL]
          - paths: [src/cyc/**]
            targets: [CYCA]
          - paths: [src/outer/**]
            targets: [OUTER]
          - paths: [src/bogus/**]
            targets: [BOGUS, test:RealOne]
        derived_targets:
          - tests: [test:CliTests, test:CliTestsTwo]
            targets: [job:cli-starter]
          - tests: [test:ChainA]
            targets: [test:ChainB]
          - tests: [test:ChainB]
            targets: [job:chain-job]
          - tests: [test:GrpTrigger]
            targets: [MIXED]
          - tests: [test:DerCycA]
            targets: [test:DerCycB]
          - tests: [test:DerCycB]
            targets: [test:DerCycA]
        affected_project_rules:
          - projects: [Aspire.ProjCli, Aspire.Managed]
            targets: [job:projjob]
          - projects: [Aspire.Hosting.Proj*]
            targets: [job:hostjob]
          - projects: [Aspire.ProjSelectsTest]
            targets: [test:RealOne]
          - projects: [Aspire.ProjChain]
            targets: [test:ChainA]
          - projects: [Aspire.ProjGroup]
            targets: [MIXED]
          - projects: [Aspire.ProjAll]
            targets: [ALL]
        """;

    // The matrix universe for the synthetic tests. Deliberately omits Aspire.Hosting.Ghost.Tests
    // and Aspire.Hosting.Azure.CosmosDB.Tests so the existence guard / override paths are exercised.
    private static readonly string[] s_matrix =
    [
        "Aspire.Hosting.Foo.Tests", "Foo.Tests", "Aspire.Hosting.Azure.Tests",
        "Aspire.Hosting.Azure.Kusto.Tests", "GroupTest", "T1", "T2", "InstallerTests",
        "CliTests", "CliTestsTwo", "ChainA", "ChainB", "RealOne", "GrpTrigger", "DerCycA", "DerCycB",
        "SelfProj", "Layer1Only",
    ];

    private static TestSelector Selector(IEnumerable<string>? projectDirs = null)
    {
        var dir = Directory.CreateTempSubdirectory("selecttests");
        var path = Path.Combine(dir.FullName, "map.yml");
        File.WriteAllText(path, SyntheticMap);
        return new TestSelector(
            path,
            s_matrix.ToHashSet(StringComparer.Ordinal),
            (projectDirs ?? []).ToHashSet(StringComparer.Ordinal));
    }

    private static SelectionResult Select(string[] files, string[]? layer1 = null, IEnumerable<string>? projectDirs = null)
        => Selector(projectDirs).Select(files, layer1 ?? [], new SelectorOptions());

    // --- A. Convention matching -------------------------------------------------------------

    [Fact]
    public void HostingConventionSelectsSameNamedTest()
    {
        var r = Select(["src/Aspire.Hosting.Foo/Bar.cs"]);

        Assert.Contains("Aspire.Hosting.Foo.Tests", r.TestProjects);
        Assert.False(r.SelectsAll);
    }

    [Fact]
    public void ComponentsConventionSelectsSameNamedTest()
    {
        var r = Select(["src/Components/Foo/Bar.cs"]);

        Assert.Contains("Foo.Tests", r.TestProjects);
        Assert.False(r.SelectsAll);
    }

    [Fact]
    public void ConventionExistenceGuardDropsTestNotInMatrix()
    {
        // No Aspire.Hosting.Ghost.Tests in the matrix; the dir is Layer-1-owned so it does not hit
        // the fallback — the point is the convention emits no phantom target.
        var r = Select(["src/Aspire.Hosting.Ghost/Bar.cs"], projectDirs: ["src/Aspire.Hosting.Ghost"]);

        Assert.DoesNotContain("Aspire.Hosting.Ghost.Tests", r.TestProjects);
        Assert.Empty(r.TestProjects);
        Assert.False(r.SelectsAll);
    }

    [Fact]
    public void ConventionGuardMissOnUnownedSrcFileForcesRunAll()
    {
        // The convention matches src/Components/Ghost/** -> test:Ghost.Tests, but Ghost.Tests is absent
        // from the matrix (existence guard drops it), no path_rule matches, and the file is not
        // Layer-1-owned. It must fall to the run-all fallback, not silently select nothing -- the
        // dangerous false-negative direction.
        var r = Select(["src/Components/Ghost/Bar.cs"]);

        Assert.True(r.SelectsAll);
    }

    [Fact]
    public void CoreHostingDirIsNotMatchedByConvention()
    {
        // src/Aspire.Hosting (no dotted suffix) must NOT match src/Aspire.Hosting.<name>. Owned so
        // it does not fall to run-all; the assertion is that no convention test is added.
        var r = Select(["src/Aspire.Hosting/Bar.cs"], projectDirs: ["src/Aspire.Hosting"]);

        Assert.Empty(r.TestProjects);
        Assert.False(r.SelectsAll);
    }

    [Fact]
    public void ConventionCapturesDottedSegment()
    {
        var r = Select(["src/Aspire.Hosting.Azure.Kusto/Bar.cs"]);

        Assert.Contains("Aspire.Hosting.Azure.Kusto.Tests", r.TestProjects);
    }

    [Fact]
    public void ConventionIsAdditiveOverLayer1()
    {
        var r = Select(["src/Components/Foo/Bar.cs"], layer1: ["Layer1Only"]);

        Assert.Contains("Foo.Tests", r.TestProjects);
        Assert.Contains("Layer1Only", r.TestProjects);
    }

    [Fact]
    public void ConventionMatchPreventsRunAllFallback()
    {
        // Not Layer-1-owned and not ignored: only the convention match keeps this src file out of
        // the run-all fallback.
        var r = Select(["src/Components/Foo/Bar.cs"]);

        Assert.False(r.SelectsAll);
        Assert.Contains("Foo.Tests", r.TestProjects);
        Assert.DoesNotContain("src/Components/Foo/Bar.cs", r.UnmatchedFiles);
    }

    [Fact]
    public void ConventionMatchesDeeplyNestedFile()
    {
        var r = Select(["src/Components/Foo/a/b/c/Deep.cs"]);

        Assert.Contains("Foo.Tests", r.TestProjects);
    }

    // --- B. Convention overrides ------------------------------------------------------------

    [Fact]
    public void OverrideCoversConventionMissDir()
    {
        // No Aspire.Hosting.Azure.CosmosDB.Tests in the matrix; the override maps the Azure family
        // to the aggregate Azure test.
        var r = Select(["src/Aspire.Hosting.Azure.CosmosDB/Bar.cs"]);

        Assert.Contains("Aspire.Hosting.Azure.Tests", r.TestProjects);
        Assert.DoesNotContain("Aspire.Hosting.Azure.CosmosDB.Tests", r.TestProjects);
        Assert.False(r.SelectsAll);
    }

    [Fact]
    public void OverrideCoexistsWithConvention()
    {
        var r = Select(["src/Aspire.Hosting.Azure.Kusto/Bar.cs"]);

        Assert.Contains("Aspire.Hosting.Azure.Kusto.Tests", r.TestProjects); // convention
        Assert.Contains("Aspire.Hosting.Azure.Tests", r.TestProjects);       // override
    }

    // --- C. Ignore --------------------------------------------------------------------------

    [Fact]
    public void IgnoredFileContributesNoTargets()
    {
        var r = Select(["src/Shared/Inert/Thing.cs"]);

        Assert.Empty(r.TestProjects);
        Assert.Empty(r.Jobs);
    }

    [Fact]
    public void IgnoredSrcFileDoesNotForceRunAllOrReportUnmatched()
    {
        // Not Layer-1-owned, but ignored -> must not escalate to ALL and must not be flagged.
        var r = Select(["src/Shared/Inert/Thing.cs"]);

        Assert.False(r.SelectsAll);
        Assert.DoesNotContain("src/Shared/Inert/Thing.cs", r.UnmatchedFiles);
    }

    [Fact]
    public void IgnoreDoesNotSuppressAnExplicitMatch()
    {
        // src/Both/** is in BOTH the ignore list and a loose_file_deps rule. Ignore only prevents
        // the fallback; the explicit rule's target still applies.
        var r = Select(["src/Both/Thing.cs"]);

        Assert.Contains("RealOne", r.TestProjects);
        Assert.False(r.SelectsAll);
    }

    // --- D. Layer-1 attribution & run-all fallback ------------------------------------------

    [Fact]
    public void OrphanSrcFileForcesRunAll()
    {
        // Not owned, not matched, not ignored, under src/ -> the run-all fallback.
        var r = Select(["src/Orphan/Thing.cs"]);

        Assert.True(r.SelectsAll);
        Assert.NotNull(r.EscalationReason);
        Assert.Contains("src/Orphan/Thing.cs", r.UnmatchedFiles);
    }

    [Fact]
    public void Layer1OwnedSrcFileDoesNotForceRunAll()
    {
        // Same shape as the orphan, but the dir is a project in the (fake) slnx -> Layer-1-owned.
        var r = Select(["src/OwnedProj/Thing.cs"], projectDirs: ["src/OwnedProj"]);

        Assert.False(r.SelectsAll);
        Assert.Empty(r.TestProjects);
        Assert.DoesNotContain("src/OwnedProj/Thing.cs", r.UnmatchedFiles);
    }

    [Fact]
    public void Layer1OwnedPrefixRequiresPathBoundary()
    {
        // "src/OwnedProj" must not own the sibling "src/OwnedProjExtra/..." -- ownership matches on a
        // trailing-separator prefix. A bare StartsWith would falsely own the sibling and suppress its
        // run-all fallback (a silent under-selection); this is the regression that guard prevents.
        var r = Select(["src/OwnedProjExtra/Thing.cs"], projectDirs: ["src/OwnedProj"]);

        Assert.True(r.SelectsAll);
        Assert.Contains("src/OwnedProjExtra/Thing.cs", r.UnmatchedFiles);
    }

    [Fact]
    public void LeftoverNonSrcFileIsAuditOnly()
    {
        var r = Select(["docs/architecture/notes.md"]);

        Assert.False(r.SelectsAll);
        Assert.Contains("docs/architecture/notes.md", r.UnmatchedFiles);
    }

    [Fact]
    public void Layer1AffectedProjectsAreUnionedIn()
    {
        var r = Select(["src/OwnedProj/Thing.cs"], layer1: ["T1", "T2"], projectDirs: ["src/OwnedProj"]);

        Assert.Contains("T1", r.TestProjects);
        Assert.Contains("T2", r.TestProjects);
    }

    [Fact]
    public void RunAllGlobForcesAllWithNoUnmatched()
    {
        var r = Select(["global.json"]);

        Assert.True(r.SelectsAll);
        Assert.Empty(r.UnmatchedFiles);
    }

    // --- E. Derived targets (fixpoint) ------------------------------------------------------

    [Fact]
    public void DerivedTargetFiresForLayer1SelectedTest()
    {
        var r = Select(["src/OwnedProj/Thing.cs"], layer1: ["CliTests"], projectDirs: ["src/OwnedProj"]);

        Assert.Contains("job:cli-starter", r.Jobs);
    }

    [Fact]
    public void DerivedTargetFiresForConventionSelectedTest()
    {
        // tests/<X>/** -> test:<X> (convention); CliTests then derives -> cli-starter.
        var r = Select(["tests/CliTests/Foo.cs"]);

        Assert.Contains("CliTests", r.TestProjects);
        Assert.Contains("job:cli-starter", r.Jobs);
    }

    [Fact]
    public void DerivedRuleFiresForAnyTriggerTestInItsList()
    {
        // The cli-starter rule lists two trigger tests; selecting the second one must fire it too.
        var r = Select(["src/OwnedProj/Thing.cs"], layer1: ["CliTestsTwo"], projectDirs: ["src/OwnedProj"]);

        Assert.Contains("job:cli-starter", r.Jobs);
    }

    [Fact]
    public void TestWithNoDerivedRuleAddsNothing()
    {
        var r = Select(["src/OwnedProj/Thing.cs"], layer1: ["T1"], projectDirs: ["src/OwnedProj"]);

        Assert.Empty(r.Jobs);
    }

    [Fact]
    public void DerivedTargetsReachFixpointThroughTestChain()
    {
        // ChainA -> ChainB -> job:chain-job.
        var r = Select(["src/OwnedProj/Thing.cs"], layer1: ["ChainA"], projectDirs: ["src/OwnedProj"]);

        Assert.Contains("ChainB", r.TestProjects);
        Assert.Contains("job:chain-job", r.Jobs);
    }

    [Fact]
    public void DerivedTargetCycleTerminates()
    {
        // DerCycA -> DerCycB -> DerCycA. Must terminate with both selected.
        var r = Select(["src/OwnedProj/Thing.cs"], layer1: ["DerCycA"], projectDirs: ["src/OwnedProj"]);

        Assert.Contains("DerCycA", r.TestProjects);
        Assert.Contains("DerCycB", r.TestProjects);
    }

    [Fact]
    public void DerivedPassIsNoOpUnderSelectsAll()
    {
        var r = Select(["global.json"], layer1: ["CliTests"]);

        Assert.True(r.SelectsAll);
        // Under ALL, Jobs is the full vocabulary; cli-starter is present because it is a known token,
        // not because the derived pass ran.
        Assert.True(r.TestProjects.SetEquals(s_matrix));
    }

    // --- F. Composition / switches ----------------------------------------------------------

    [Fact]
    public void MultipleFilesUnionTheirTargets()
    {
        var r = Select(["src/Components/Foo/Bar.cs", "eng/installer/x.ps1"]);

        Assert.Contains("Foo.Tests", r.TestProjects);
        Assert.Contains("InstallerTests", r.TestProjects);
        Assert.Contains("job:installer", r.Jobs);
    }

    [Fact]
    public void KillSwitchForcesAll()
    {
        var r = Selector().Select(["src/Components/Foo/Bar.cs"], [], new SelectorOptions(ForceAll: true));

        Assert.True(r.SelectsAll);
        Assert.True(r.TestProjects.SetEquals(s_matrix));
    }

    [Fact]
    public void SelectsAllExpandsToFullMatrixAndAllJobTokens()
    {
        var r = Select(["global.json"]);

        Assert.True(r.TestProjects.SetEquals(s_matrix));
        // Every job token referenced anywhere in the synthetic map is part of the ALL expansion.
        Assert.Contains("job:installer", r.Jobs);
        Assert.Contains("job:hostjob", r.Jobs);
        Assert.Contains("job:cli-starter", r.Jobs);
        Assert.Contains("job:group-job", r.Jobs);
        Assert.Contains("job:projjob", r.Jobs);
    }

    [Fact]
    public void EmptyChangeSelectsNothing()
    {
        var r = Select([]);

        Assert.False(r.SelectsAll);
        Assert.Empty(r.TestProjects);
        Assert.Empty(r.Jobs);
    }

    // --- G. test_self / curated_jobs / loose_file_deps --------------------------------------

    [Fact]
    public void TestSelfChangeRunsThatTest()
    {
        var r = Select(["tests/SelfProj/SomeTest.cs"]);

        Assert.Contains("SelfProj", r.TestProjects);
    }

    [Fact]
    public void FileMatchingBothJobGlobAndConventionGetsBoth()
    {
        // src/Aspire.Hosting.Foo/** matches the convention AND the src/Aspire.Hosting.*/** job glob.
        var r = Select(["src/Aspire.Hosting.Foo/Bar.cs"]);

        Assert.Contains("Aspire.Hosting.Foo.Tests", r.TestProjects);
        Assert.Contains("job:hostjob", r.Jobs);
    }

    [Fact]
    public void LooseFileDepSelectsTestAndJob()
    {
        var r = Select(["eng/installer/manifest.yml"]);

        Assert.Contains("InstallerTests", r.TestProjects);
        Assert.Contains("job:installer", r.Jobs);
    }

    // --- I. Named-group mechanism -----------------------------------------------------------

    [Fact]
    public void GroupExpandsToMixedMembers()
    {
        var r = Select(["src/grp/Thing.cs"]);

        Assert.Contains("GroupTest", r.TestProjects);
        Assert.Contains("job:group-job", r.Jobs);
    }

    [Fact]
    public void SameGroupFromTwoRulesIsDeduped()
    {
        var r = Select(["src/grp/A.cs", "src/grp2/B.cs"]);

        Assert.Contains("GroupTest", r.TestProjects);
        Assert.Contains("job:group-job", r.Jobs);
    }

    [Fact]
    public void GroupExpandsFromDerivedRule()
    {
        var r = Select(["src/OwnedProj/Thing.cs"], layer1: ["GrpTrigger"], projectDirs: ["src/OwnedProj"]);

        Assert.Contains("GroupTest", r.TestProjects);
        Assert.Contains("job:group-job", r.Jobs);
    }

    [Fact]
    public void NestedGroupsExpandRecursively()
    {
        // OUTER -> [test:T1, INNER]; INNER -> [job:inner-job, test:T2]. The inner group's members
        // must be reached through the recursive expansion.
        var r = Select(["src/outer/Thing.cs"]);

        Assert.Contains("T1", r.TestProjects);
        Assert.Contains("T2", r.TestProjects);
        Assert.Contains("job:inner-job", r.Jobs);
    }

    [Fact]
    public void CyclicGroupExpansionTerminates()
    {
        // CYCA -> [test:T1, CYCB]; CYCB -> [test:T2, CYCA]. Must terminate with both tests.
        var r = Select(["src/cyc/Thing.cs"]);

        Assert.Contains("T1", r.TestProjects);
        Assert.Contains("T2", r.TestProjects);
    }

    [Fact]
    public void UnknownTargetTokenIsIgnored()
    {
        // The rule targets [BOGUS, test:RealOne]; BOGUS is neither a group nor test:/job: -> ignored.
        var r = Select(["src/bogus/Thing.cs"]);

        Assert.Contains("RealOne", r.TestProjects);
        Assert.DoesNotContain("BOGUS", r.TestProjects);
    }

    [Fact]
    public void GroupContainingAllForcesSelectsAll()
    {
        var r = Select(["src/allgrp/Thing.cs"]);

        Assert.True(r.SelectsAll);
    }

    // --- J. project_rules (affected-project -> targets) --------------------------------------

    [Fact]
    public void ProjectRuleFiresForAffectedProductionProject()
    {
        // An affected production project (not a matrix test) drives the rule's job.
        var r = Select([], layer1: ["Aspire.ProjCli"]);

        Assert.Contains("job:projjob", r.Jobs);
    }

    [Fact]
    public void ProjectRuleProductionNameIsNotSelectedAsATest()
    {
        // The production name is matched by project_rules but is not a matrix project, so it must
        // not leak into the selected test set.
        var r = Select([], layer1: ["Aspire.ProjCli"]);

        Assert.DoesNotContain("Aspire.ProjCli", r.TestProjects);
    }

    [Fact]
    public void ProjectRuleMatchesByNameGlob()
    {
        // Aspire.Hosting.Proj* matches an affected hosting project by NAME (not path).
        var r = Select([], layer1: ["Aspire.Hosting.ProjRedis"]);

        Assert.Contains("job:hostjob", r.Jobs);
    }

    [Fact]
    public void ProjectRuleFiresForAnyProjectInItsList()
    {
        // The projjob rule lists two projects; the second one must fire it too.
        var r = Select([], layer1: ["Aspire.Managed"]);

        Assert.Contains("job:projjob", r.Jobs);
    }

    [Fact]
    public void ProjectRuleIsAdditiveWithLayer1Tests()
    {
        // A real matrix test in the affected set is still selected alongside the project rule's job.
        var r = Select([], layer1: ["Aspire.ProjCli", "T1"]);

        Assert.Contains("T1", r.TestProjects);
        Assert.Contains("job:projjob", r.Jobs);
    }

    [Fact]
    public void ProjectRuleSelectedTestFeedsDerivedTargets()
    {
        // Aspire.ProjChain -> test:ChainA (project_rule); ChainA -> ChainB -> job:chain-job (derived).
        var r = Select([], layer1: ["Aspire.ProjChain"]);

        Assert.Contains("ChainA", r.TestProjects);
        Assert.Contains("ChainB", r.TestProjects);
        Assert.Contains("job:chain-job", r.Jobs);
    }

    [Fact]
    public void ProjectRuleExpandsGroup()
    {
        var r = Select([], layer1: ["Aspire.ProjGroup"]);

        Assert.Contains("GroupTest", r.TestProjects);
        Assert.Contains("job:group-job", r.Jobs);
    }

    [Fact]
    public void ProjectRuleCanForceAll()
    {
        var r = Select([], layer1: ["Aspire.ProjAll"]);

        Assert.True(r.SelectsAll);
    }

    [Fact]
    public void UnmatchedAffectedProjectAddsNothing()
    {
        var r = Select([], layer1: ["Aspire.Unrelated"]);

        Assert.False(r.SelectsAll);
        Assert.Empty(r.TestProjects);
        Assert.Empty(r.Jobs);
    }

    // Layer 1 reports the full affected set, which can include tests/ projects that are NOT in the
    // runnable matrix (shared fixtures/helpers like a TestFixtures or testproject project). Those names
    // are intersected with the matrix before selection, so they must never be selected as a test (only
    // an affected_project_rule may reference such a name by name). Failure mode: a non-runnable project
    // leaking into the matrix.
    [Fact]
    public void Layer1AffectedNonMatrixTestNameIsNotSelected()
    {
        var r = Select([], layer1: ["TestFixtures.Shared", "testproject"]);

        Assert.False(r.SelectsAll);
        Assert.Empty(r.TestProjects);
        Assert.Empty(r.Jobs);
    }

    // --- H. Real-map invariant smoke (computed from the filesystem; no hardcoded names) ------

    [Fact]
    public void RealMapLoadsAndConventionSelectsAComponentsTestWithoutSelectingAll()
    {
        var mapPath = Path.Combine(RepoRoot.Path, "eng", "test-trigger-map.yml");
        var matrix = EnumerateMatrixTestProjects();
        var projectDirs = LoadProjectDirectories();
        var selector = new TestSelector(mapPath, matrix, projectDirs);

        // Pick a real src/Components/<dir> whose same-named test exists; the convention must select
        // exactly that test name (derived from the dir, not hardcoded) and must not force ALL.
        var (componentDir, expectedTest) = FirstComponentWithSameNamedTest(matrix);

        var r = selector.Select([$"{componentDir}/__probe__.cs"], [], new SelectorOptions());

        Assert.Contains(expectedTest, r.TestProjects);
        Assert.False(r.SelectsAll);
    }

    private static (string Dir, string Test) FirstComponentWithSameNamedTest(IReadOnlyCollection<string> matrix)
    {
        var componentsRoot = Path.Combine(RepoRoot.Path, "src", "Components");
        foreach (var dir in Directory.EnumerateDirectories(componentsRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            var test = $"{dir}.Tests";
            if (matrix.Contains(test))
            {
                return ($"src/Components/{dir}", test);
            }
        }

        throw new InvalidOperationException("No src/Components/<dir> with a same-named test project was found.");
    }

    private static IReadOnlyCollection<string> EnumerateMatrixTestProjects()
    {
        var testsDir = Path.Combine(RepoRoot.Path, "tests");
        return Directory.EnumerateDirectories(testsDir)
            .Select(Path.GetFileName)
            .Where(name => name is not null && File.Exists(Path.Combine(testsDir, name!, $"{name}.csproj")))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyCollection<string> LoadProjectDirectories()
    {
        var slnx = File.ReadAllText(Path.Combine(RepoRoot.Path, "Aspire.slnx"));
        return System.Text.RegularExpressions.Regex.Matches(slnx, "Path=\"([^\"]+\\.csproj)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Select(p => p.Contains('/', StringComparison.Ordinal) ? p[..p.LastIndexOf('/')] : p)
            .Where(d => d.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }
}
