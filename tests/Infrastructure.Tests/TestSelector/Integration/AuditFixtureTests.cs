// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using TestSelector;
using TestSelector.Analyzers;
using TestSelector.Models;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Integration;

/// <summary>
/// Replays a curated set of real PRs against the live audit rules and asserts the
/// expected outcome / categories / mapped test projects. Each fixture row is one
/// hand-validated PR; if a rule edit changes any row's outcome, that row's
/// [Theory] case turns red. This is the regression net for the rules file at
/// <c>eng/scripts/test-selection-rules.audit.json</c>.
///
/// We deliberately drive the evaluation through the per-component public APIs
/// (IgnorePathFilter + CategoryMapper + ProjectMappingResolver + CriticalFileDetector)
/// instead of calling <see cref="TestEvaluator.EvaluateAsync"/>. That keeps the
/// test hermetic — no <c>dotnet-affected</c> subprocess, no dependency on a real
/// git ref pair. The rescue step is replicated inline so the per-component logic
/// stays in sync with <see cref="TestEvaluator"/>; the rescue regression tests in
/// <see cref="EndToEndEvaluationTests"/> pin the rescue behavior in the real
/// evaluator.
/// </summary>
public class AuditFixtureTests
{
    private static readonly Lazy<AuditFixtureEntry[]> s_fixtures = new(LoadFixtures);

    public static IEnumerable<TheoryDataRow<int>> AuditFixturePrNumbers()
    {
        foreach (var f in s_fixtures.Value)
        {
            // Pass only the PR number across the theory boundary (xUnit needs the
            // arguments to be trivially serializable). The test re-looks up the
            // full fixture row from the lazy cache.
            yield return new TheoryDataRow<int>(f.PrNumber)
            {
                TestDisplayName = $"#{f.PrNumber} {Truncate(f.Title, 60)} [{f.ScenarioTag}]"
            };
        }
    }

    [Theory]
    [MemberData(nameof(AuditFixturePrNumbers))]
    public void AuditFixture_ResolvesToExpectedOutcomeAndCategories(int prNumber)
    {
        var fixture = s_fixtures.Value.FirstOrDefault(f => f.PrNumber == prNumber)
            ?? throw new InvalidOperationException($"Fixture for PR #{prNumber} not found");

        var rulesPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.audit.json");
        var config = TestSelectorConfig.LoadFromJson(File.ReadAllText(rulesPath));

        var actual = EvaluateAgainstRules(config, fixture.ChangedFiles);

        Assert.Equal(fixture.Expected.Outcome, actual.Outcome);
        Assert.Equal(
            fixture.Expected.Categories.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            actual.Categories.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            fixture.Expected.MappedTestProjects.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            actual.MappedTestProjects.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    // Shared test helpers / app-host fixtures are referenced by many test projects via
    // ProjectReference (e.g. tests/Aspire.Hosting.Tests, tests/Aspire.Hosting.Testing.Tests),
    // so dotnet-affected can fan a change out to the real dependents. They are wired into the
    // integrations jobCategory so the change is "accounted for" and routed through dotnet-affected
    // instead of matching no rule and forcing a conservative RunAll (fallback_unmatched). If the
    // integrations when-globs regress, this turns red by reverting to fallback_unmatched.
    [Theory]
    [InlineData("tests/Aspire.TestUtilities/AspireTestExtensions.cs")]
    [InlineData("tests/Aspire.Components.Common.TestUtilities/ConformanceTests.cs")]
    [InlineData("tests/testproject/TestProject.AppHost/Program.cs")]
    [InlineData("tests/TestingAppHost1/TestingAppHost1.AppHost/Program.cs")]
    public void AuditRules_SharedTestHelperChange_RoutedToIntegrations(string changedFile)
    {
        var rulesPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.audit.json");
        var config = TestSelectorConfig.LoadFromJson(File.ReadAllText(rulesPath));

        var actual = EvaluateAgainstRules(config, [changedFile]);

        Assert.Equal("selective", actual.Outcome);
        Assert.Contains("integrations", actual.Categories);
    }

    /// <summary>
    /// Replicates eval_rules.py / TestEvaluator step ordering using the public
    /// component APIs. Returns the same shape the Python fixture generator
    /// computes, so the assertions are apples-to-apples.
    /// </summary>
    private static EvaluationResult EvaluateAgainstRules(TestSelectorConfig config, IReadOnlyList<string> changedFiles)
    {
        var ignoreFilter = new IgnorePathFilter(config.Ignore);
        var criticalDetector = new CriticalFileDetector(config.RunEverything);
        var categoryMapper = new CategoryMapper(config.JobCategories);
        var mappingResolver = new ProjectMappingResolver(config.Mappings);

        // Edges (e.g. the cli_e2e runtime/build couplings) are projected onto the mapping shape so
        // the same glob/{name}/exclude resolver matches their from-globs. The category/type tags are
        // dropped for resolution; the per-edge category is recovered separately below.
        var edgeMappings = config.Edges
            .Select(e => new SelectionMapping { From = e.From, To = e.To, Exclude = e.Exclude })
            .ToList();
        var edgeResolver = new ProjectMappingResolver(edgeMappings);

        // Step 1+2: split into ignored/active.
        var (ignored, active) = ignoreFilter.SplitFiles(changedFiles);

        // Step 2b: rescue ignored files that would actually fire some jobCategory (matches a
        // jobCategory.when AND not matched by that category's exclude) or an edge.from. Mirrors
        // TestEvaluator.RescueTriggerFiles.
        var rescued = new List<string>();
        foreach (var f in ignored)
        {
            var status = categoryMapper.GetCategoriesWithDetails(new[] { f }).CategoryStatus;
            if (status.Values.Any(triggered => triggered) || edgeResolver.Matches(f))
            {
                rescued.Add(f);
            }
        }
        var activeList = active.Concat(rescued).ToList();

        // Step 3: all ignored → skip.
        if (activeList.Count == 0)
        {
            return new EvaluationResult("skip", Array.Empty<string>(), Array.Empty<string>());
        }

        // Step 4: runEverything match → trigger_all.
        var critical = criticalDetector.FindFirstCriticalFile(activeList);
        if (critical.File is not null)
        {
            return new EvaluationResult("trigger_all", Array.Empty<string>(), Array.Empty<string>());
        }

        // Step 5: jobCategory triggers (integrations / extension / polyglot).
        var (jobCategories, jobCategoryMatchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(activeList);
        var firedCategories = jobCategories.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();

        // Step 6: source-to-test mappings. Only mappings contribute to MappedTestProjects;
        // edges resolve to test projects in the engine but are not part of the audit's mapped set.
        var mappingDetails = mappingResolver.ResolveAllWithDetails(activeList);
        var mappedProjects = mappingDetails.TestProjects.ToArray();
        var mappingMatchedFiles = mappingDetails.MatchedFiles;

        // Step 7: resolve edges for file accounting and to recover which edge categories fired.
        // An edge category (e.g. cli_e2e) fires iff one of its from-globs matched an active file —
        // the hermetic stand-in for the engine's "edge.To ends up in the selected set" projection.
        var edgeMatchedFiles = edgeResolver.ResolveAllWithDetails(activeList).MatchedFiles;
        foreach (var edge in config.Edges)
        {
            if (string.IsNullOrEmpty(edge.Category) || firedCategories.Contains(edge.Category))
            {
                continue;
            }

            var singleEdge = new ProjectMappingResolver(
                new[] { new SelectionMapping { From = edge.From, To = edge.To, Exclude = edge.Exclude } });
            if (activeList.Any(singleEdge.Matches))
            {
                firedCategories.Add(edge.Category);
            }
        }

        // Step 8: unmatched active files → fallback_unmatched. A file is "explained" if any
        // jobCategory, mapping, or edge claimed it.
        var unmatched = activeList
            .Where(f => !jobCategoryMatchedFiles.Contains(f)
                && !mappingMatchedFiles.Contains(f)
                && !edgeMatchedFiles.Contains(f))
            .ToList();
        if (unmatched.Count > 0)
        {
            return new EvaluationResult("fallback_unmatched", firedCategories.ToArray(), mappedProjects);
        }

        return new EvaluationResult("selective", firedCategories.ToArray(), mappedProjects);
    }

    private static AuditFixtureEntry[] LoadFixtures()
    {
        // Files copied from TestSelector\TestData\*.json land in TestSelectorData\ at the
        // output root — see Infrastructure.Tests.csproj for the Link rationale.
        var path = Path.Combine(AppContext.BaseDirectory, "TestSelectorData", "audit-fixtures.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Audit fixture not found at {path}. Did the csproj Content item for TestSelector\\TestData\\*.json get removed?");
        }
        return JsonSerializer.Deserialize<AuditFixtureEntry[]>(File.ReadAllText(path), s_jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize fixtures at {path}");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Aspire.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find repository root (Aspire.slnx)");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly record struct EvaluationResult(string Outcome, IReadOnlyList<string> Categories, IReadOnlyList<string> MappedTestProjects);

    public sealed record AuditFixtureEntry(
        int PrNumber,
        string Title,
        string Url,
        string[] ChangedFiles,
        string ScenarioTag,
        bool PreviouslyFallback,
        AuditFixtureExpected Expected);

    public sealed record AuditFixtureExpected(
        string Outcome,
        string[] Categories,
        string[] MappedTestProjects,
        string Comment);
}
