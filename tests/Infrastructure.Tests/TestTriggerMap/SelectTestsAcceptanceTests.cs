// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.SelectTests;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Behavior spec for the <see cref="TestSelector"/> tool, expressed as a contract:
/// given a set of changed files (and, where relevant, the Layer 1 / <c>dotnet-affected</c>
/// set injected as project names), assert on the returned <see cref="SelectionResult"/>
/// (selected test projects, jobs, whether the full matrix is forced, and the unmatched files).
///
/// The selector only owns Layer 2 (the curated <c>docs/ci/test-trigger-map.yml</c>): the
/// graph-derived project closure (leaf/core/test-hub fan-out) is computed at runtime by
/// <c>dotnet-affected</c> and supplied to <see cref="TestSelector.Select"/>, so those edges are
/// exercised here by injecting the Layer 1 set rather than by the map.
/// </summary>
public sealed class SelectTestsAcceptanceTests
{
    private static readonly string s_mapPath = Path.Combine(RepoRoot.Path, "docs", "ci", "test-trigger-map.yml");
    private static readonly IReadOnlyCollection<string> s_allTestProjects = EnumerateMatrixTestProjects();

    private static SelectionResult Select(params string[] changedFiles)
        => new TestSelector(s_mapPath, s_allTestProjects).Select(changedFiles, [], new SelectorOptions());

    private static SelectionResult SelectWithLayer1(string[] changedFiles, string[] layer1Affected)
        => new TestSelector(s_mapPath, s_allTestProjects).Select(changedFiles, layer1Affected, new SelectorOptions());

    // --- run_all catch-all -> full matrix ---------------------------------------------------

    [Fact]
    public void BuildInfraChangeSelectsAll()
    {
        var result = Select("global.json");

        Assert.True(result.SelectsAll);
        Assert.NotNull(result.EscalationReason);
    }

    [Fact]
    public void SelectsAllExpandsToFullMatrixAndAllJobs()
    {
        var result = Select("global.json");

        Assert.True(result.TestProjects.SetEquals(s_allTestProjects),
            "SelectsAll must expand TestProjects to the full matrix.");
        Assert.Contains("job:polyglot", result.Jobs);
        Assert.Contains("job:extension-e2e", result.Jobs);
    }

    [Fact]
    public void BuildOrchestrationProjSelectsAll()
    {
        // eng/OuterPreBuild.proj is build-wide project-name validation -> err toward ALL.
        var result = Select("eng/OuterPreBuild.proj");

        Assert.True(result.SelectsAll);
    }

    // --- shared source (no owning csproj) ---------------------------------------------------

    [Fact]
    public void ComponentsCommonSelectsAllComponents()
    {
        // src/Components/Common is compiled into many client components (group expansion).
        var result = Select("src/Components/Common/HealthChecksExtensions.cs");

        Assert.Contains("Aspire.Npgsql.Tests", result.TestProjects);
        Assert.False(result.SelectsAll);
    }

    [Fact]
    public void VendoredOtelSelectsRedisAndKafka()
    {
        var result = Select("src/Vendoring/OpenTelemetry.Shared/SomeShared.cs");

        Assert.Contains("Aspire.StackExchange.Redis.Tests", result.TestProjects);
        Assert.Contains("Aspire.Confluent.Kafka.Tests", result.TestProjects);
    }

    // --- shared_compiled_source is FILE-granular --------------------------------------------

    [Fact]
    public void LinkCompiledFileSelectsItsConsumers()
    {
        // The shared constants file is link-compiled into several Npgsql tests; changing it runs them.
        var result = Select("src/Aspire.Hosting.PostgreSQL/PostgresContainerImageTags.cs");

        Assert.Contains("Aspire.Npgsql.Tests", result.TestProjects);
        Assert.Contains("Aspire.Azure.Npgsql.Tests", result.TestProjects);
        Assert.Contains("Aspire.Hosting.Tests", result.TestProjects);
    }

    [Fact]
    public void NonSharedFileInSameProjectDoesNotDragCompileConsumers()
    {
        // A different file in the same project must NOT pull the borrowed-file consumers — file
        // granularity. (The file still matches the src/Aspire.Hosting*/** curated job globs, which
        // is expected; what matters is it does not select the Npgsql tests.)
        var result = Select("src/Aspire.Hosting.PostgreSQL/PostgresBuilderExtensions.cs");

        Assert.DoesNotContain("Aspire.Npgsql.Tests", result.TestProjects);
    }

    // --- loose-file / runtime dependencies --------------------------------------------------

    [Fact]
    public void ClipackChangeSelectsCliAndInfraTests()
    {
        var result = Select("eng/clipack/npm/aspire.js");

        Assert.Contains("Aspire.Cli.Tests", result.TestProjects);
        Assert.Contains("Aspire.Cli.EndToEnd.Tests", result.TestProjects);
        Assert.Contains("Infrastructure.Tests", result.TestProjects);
    }

    [Fact]
    public void TemplatesChangeSelectsTemplatesTests()
    {
        var result = Select("src/Aspire.ProjectTemplates/templates/aspire-starter/Program.cs");

        Assert.Contains("Aspire.Templates.Tests", result.TestProjects);
    }

    [Fact]
    public void PlaygroundChangeSelectsPlaygroundTests()
    {
        var result = Select("playground/TestShop/AppHost/Program.cs");

        Assert.Contains("Aspire.Playground.Tests", result.TestProjects);
    }

    [Fact]
    public void GenericWorkflowChangeSelectsInfrastructureTestsOnly()
    {
        var result = Select(".github/workflows/some-other-workflow.yml");

        Assert.False(result.SelectsAll);
        Assert.Contains("Infrastructure.Tests", result.TestProjects);
    }

    // --- curated jobs -----------------------------------------------------------------------

    [Fact]
    public void ExtensionChangeTriggersExtensionJobs()
    {
        var result = Select("extension/src/extension.ts");

        Assert.Contains("job:extension-unit", result.Jobs);
        Assert.Contains("job:extension-e2e", result.Jobs);
    }

    [Fact]
    public void TypeSystemChangeTriggersPolyglotNotAllHosting()
    {
        var result = Select("src/Aspire.TypeSystem/TypeModel.cs");

        Assert.Contains("job:polyglot", result.Jobs);
        Assert.DoesNotContain("Aspire.Hosting.Redis.Tests", result.TestProjects);
    }

    [Fact]
    public void ComponentChangeTriggersNoJobs()
    {
        // A pure client-component .cs change matches none of the curated job globs.
        var result = Select("src/Components/Aspire.Npgsql/AspireNpgsqlExtensions.cs");

        Assert.Empty(result.Jobs);
    }

    // --- named groups (test projects + jobs) ------------------------------------------------

    [Fact]
    public void CliBundleGroupExpandsToTestsAndJobs()
    {
        // eng/Bundle.proj maps to the CLI_BUNDLE group, which mixes a test project and two jobs.
        var result = Select("eng/Bundle.proj");

        Assert.False(result.SelectsAll);
        Assert.Contains("Aspire.Cli.EndToEnd.Tests", result.TestProjects);
        Assert.Contains("job:cli-starter", result.Jobs);
        Assert.Contains("job:extension-e2e", result.Jobs);
    }

    // --- test_self --------------------------------------------------------------------------

    [Fact]
    public void TestSelfChangeRunsThatTest()
    {
        var result = Select("tests/Aspire.Cli.Tests/SomeTest.cs");

        Assert.Contains("Aspire.Cli.Tests", result.TestProjects);
    }

    // --- Layer 1 (dotnet-affected) ownership of the project closure -------------------------

    [Fact]
    public void Layer1AffectedProjectsAreIncluded()
    {
        // A leaf integration change is owned by Layer 1 now: the curated map selects nothing for
        // it, and the graph-derived projects are unioned in from the injected set.
        var result = SelectWithLayer1(
            ["src/Aspire.Hosting.Kafka/KafkaResource.cs"],
            ["Aspire.Hosting.Kafka.Tests", "Aspire.Hosting.Redis.Tests"]);

        Assert.Contains("Aspire.Hosting.Kafka.Tests", result.TestProjects);
        Assert.Contains("Aspire.Hosting.Redis.Tests", result.TestProjects);
    }

    [Fact]
    public void LeafComponentFileIsLayer2UnmatchedWithoutLayer1()
    {
        // A client-component leaf file matches no curated rule (component closure is Layer 1's
        // job), so the curated map selects nothing for it and it is reported unmatched. Hosting
        // files differ — they match the src/Aspire.Hosting*/** curated job globs — so a component
        // file is the clean "owned only by Layer 1" example.
        var result = Select("src/Components/Aspire.Npgsql/AspireNpgsqlExtensions.cs");

        Assert.False(result.SelectsAll);
        Assert.Empty(result.TestProjects);
        Assert.Empty(result.Jobs);
        Assert.Contains("src/Components/Aspire.Npgsql/AspireNpgsqlExtensions.cs", result.UnmatchedFiles);
    }

    // --- unattributed (unmatched) files -----------------------------------------------------

    [Fact]
    public void LooseFileMatchedByNoRuleIsReportedUnmatched()
    {
        var result = Select("docs/architecture/some-notes.md");

        Assert.Contains("docs/architecture/some-notes.md", result.UnmatchedFiles);
        Assert.False(result.SelectsAll);
    }

    [Fact]
    public void RunAllFileIsNotReportedUnmatched()
    {
        var result = Select("global.json");

        Assert.True(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
    }

    // --- composition: union + kill switch ---------------------------------------------------

    [Fact]
    public void MultipleChangesUnionTheirTargets()
    {
        var result = Select("eng/Bundle.proj", "extension/src/extension.ts");

        Assert.Contains("Aspire.Cli.EndToEnd.Tests", result.TestProjects);
        Assert.Contains("job:cli-starter", result.Jobs);
        Assert.Contains("job:extension-unit", result.Jobs);
    }

    [Fact]
    public void KillSwitchForcesAll()
    {
        var result = new TestSelector(s_mapPath, s_allTestProjects)
            .Select(["src/Aspire.Cli/Program.cs"], [], new SelectorOptions(ForceAll: true));

        Assert.True(result.SelectsAll);
        Assert.True(result.TestProjects.SetEquals(s_allTestProjects));
    }

    private static IReadOnlyCollection<string> EnumerateMatrixTestProjects()
    {
        // tests/<Name>/<Name>.csproj — the matrix test projects the map's test: targets refer to.
        var testsDir = Path.Combine(RepoRoot.Path, "tests");
        return Directory.EnumerateDirectories(testsDir)
            .Select(Path.GetFileName)
            .Where(name => name is not null && File.Exists(Path.Combine(testsDir, name!, $"{name}.csproj")))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
