// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.SelectTests;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Acceptance/behavior spec for the <see cref="TestSelector"/> tool, derived from the dependency
/// kinds the curated <c>docs/ci/test-trigger-map.yml</c> describes (and the selector design doc).
/// Each test states "given these changed files, the selector must pick these targets" for one
/// dependency kind or edge case. They are RED until the selector is implemented — that is the
/// point: implementing <c>TestSelector.Select</c> is "done" when these pass.
/// </summary>
public sealed class SelectTestsAcceptanceTests
{
    private static readonly string s_mapPath = Path.Combine(RepoRoot.Path, "docs", "ci", "test-trigger-map.yml");
    private static readonly IReadOnlyCollection<string> s_allTestProjects = EnumerateMatrixTestProjects();

    private static SelectionResult Select(params string[] changedFiles)
        => new TestSelector(s_mapPath, s_allTestProjects).Select(changedFiles, [], new SelectorOptions());

    // --- ProjectReference closure (leaf + core) ---------------------------------------------

    [Fact]
    public void SelectsLeafIntegrationTests()
    {
        // A leaf hosting integration maps to its own test project, not the whole hosting cluster.
        var result = Select("src/Aspire.Hosting.Kafka/KafkaResource.cs");

        Assert.False(result.SelectsAll);
        Assert.Contains("Aspire.Hosting.Kafka.Tests", result.TestProjects);
    }

    [Fact]
    public void CoreHostingChangeSelectsHostingNotComponents()
    {
        // src/Aspire.Hosting reaches the hosting side but not the pure client-component tests.
        var result = Select("src/Aspire.Hosting/ApplicationModel/Resource.cs");

        Assert.Contains("Aspire.Hosting.Tests", result.TestProjects);
        Assert.DoesNotContain("Aspire.Npgsql.Tests", result.TestProjects);
    }

    [Fact]
    public void ComponentChangeDoesNotPullOtherComponents()
    {
        // Component <-> component isolation: an Npgsql change must not drag Redis/Mongo/RabbitMQ.
        var result = Select("src/Components/Aspire.Npgsql/AspireNpgsqlExtensions.cs");

        Assert.Contains("Aspire.Npgsql.Tests", result.TestProjects);
        Assert.DoesNotContain("Aspire.StackExchange.Redis.Tests", result.TestProjects);
        Assert.DoesNotContain("Aspire.MongoDB.Driver.Tests", result.TestProjects);
    }

    // --- Foreign <Compile Include> is FILE-granular -----------------------------------------

    [Fact]
    public void LinkCompiledFileSelectsItsConsumers()
    {
        // The shared constants file is link-compiled into 5 Npgsql tests; changing it runs them all.
        var result = Select("src/Aspire.Hosting.PostgreSQL/PostgresContainerImageTags.cs");

        Assert.Contains("Aspire.Npgsql.Tests", result.TestProjects);
        Assert.Contains("Aspire.Azure.Npgsql.Tests", result.TestProjects);
        Assert.Contains("Aspire.Npgsql.EntityFrameworkCore.PostgreSQL.Tests", result.TestProjects);
        Assert.Contains("Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL.Tests", result.TestProjects);
        Assert.Contains("Aspire.Hosting.Tests", result.TestProjects);
    }

    [Fact]
    public void NonSharedFileDoesNotDragCompileConsumers()
    {
        // A different file in the same project must NOT pull the borrowed-file consumers — the
        // whole point of tracking compile-include at file granularity rather than project closure.
        var result = Select("src/Aspire.Hosting.PostgreSQL/PostgresBuilderExtensions.cs");

        Assert.Contains("Aspire.Hosting.PostgreSQL.Tests", result.TestProjects);
        Assert.DoesNotContain("Aspire.Npgsql.Tests", result.TestProjects);
    }

    // --- Test hubs and shared source dirs ---------------------------------------------------

    [Fact]
    public void TestHubChangeSelectsAllHosting()
    {
        // tests/Aspire.Hosting.Tests is referenced by the whole hosting cluster.
        var result = Select("tests/Aspire.Hosting.Tests/SomeHelper.cs");

        Assert.Contains("Aspire.Hosting.Yarp.Tests", result.TestProjects);
        Assert.Contains("Aspire.Hosting.Redis.Tests", result.TestProjects);
    }

    [Fact]
    public void ComponentTestUtilHubSelectsAllComponents()
    {
        var result = Select("tests/Aspire.Components.Common.TestUtilities/ConformanceTests.cs");

        Assert.Contains("Aspire.Npgsql.Tests", result.TestProjects);
        Assert.Contains("Aspire.StackExchange.Redis.Tests", result.TestProjects);
    }

    [Fact]
    public void ComponentsCommonSelectsAllComponents()
    {
        // src/Components/Common is compiled into many client components (no owning csproj).
        var result = Select("src/Components/Common/HealthChecksExtensions.cs");

        Assert.Contains("Aspire.Npgsql.Tests", result.TestProjects);
    }

    [Fact]
    public void VendoredOtelSelectsRedisAndKafka()
    {
        var result = Select("src/Vendoring/OpenTelemetry.Shared/SomeShared.cs");

        Assert.Contains("Aspire.StackExchange.Redis.Tests", result.TestProjects);
        Assert.Contains("Aspire.Confluent.Kafka.Tests", result.TestProjects);
    }

    // --- run_all catch-all and fail-open ----------------------------------------------------

    [Fact]
    public void BuildInfraChangeSelectsAll()
    {
        var result = Select("global.json");

        Assert.True(result.SelectsAll);
        Assert.NotNull(result.EscalationReason);
    }

    [Fact]
    public void UnknownRootBuildPropSelectsAll()
    {
        // Broadened run_all globs (eng/*.props, eng/*.targets) err toward caution: an eng build
        // prop that is not individually enumerated must still run everything.
        var result = Select("eng/Publishing.props");

        Assert.True(result.SelectsAll);
    }

    [Fact]
    public void UnmappedSourceFileFailsOpenToAll()
    {
        // A src/** file that matches no rule must fail open to ALL — a missed test is a silent
        // regression, an extra run is just slower.
        var result = Select("src/Aspire.BrandNewThing/Thing.cs");

        Assert.True(result.SelectsAll);
        Assert.NotNull(result.EscalationReason);
    }

    [Fact]
    public void SelectsAllExpandsToFullMatrix()
    {
        var result = Select("global.json");

        Assert.True(result.SelectsAll);
        Assert.True(result.TestProjects.SetEquals(s_allTestProjects),
            "SelectsAll must expand TestProjects to the full matrix.");
    }

    // --- test_self --------------------------------------------------------------------------

    [Fact]
    public void TestSelfChangeRunsThatTest()
    {
        var result = Select("tests/Aspire.Cli.Tests/SomeTest.cs");

        Assert.Contains("Aspire.Cli.Tests", result.TestProjects);
    }

    // --- Curated jobs -----------------------------------------------------------------------

    [Fact]
    public void TypeSystemChangeTriggersPolyglotNotAllHosting()
    {
        // Aspire.TypeSystem drives the polyglot/codegen + CLI surface, not the whole hosting set.
        var result = Select("src/Aspire.TypeSystem/TypeModel.cs");

        Assert.Contains("job:polyglot", result.Jobs);
        Assert.Contains("Aspire.Cli.Tests", result.TestProjects);
        Assert.DoesNotContain("Aspire.Hosting.Redis.Tests", result.TestProjects);
    }

    [Fact]
    public void ExtensionChangeTriggersExtensionJobs()
    {
        var result = Select("extension/src/extension.ts");

        Assert.Contains("job:extension-unit", result.Jobs);
        Assert.Contains("job:extension-e2e", result.Jobs);
    }

    [Fact]
    public void ComponentChangeTriggersNoJobs()
    {
        // A pure client-component .cs change matches none of the curated job globs.
        var result = Select("src/Components/Aspire.Npgsql/AspireNpgsqlExtensions.cs");

        Assert.Empty(result.Jobs);
    }

    // --- Runtime / loose-file dependencies (invisible to the project graph) -----------------

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
        // A non-run_all workflow file maps to Infrastructure.Tests and must NOT escalate to ALL
        // (tests.yml / run-tests.yml are separately in run_all_globs; an arbitrary one is not).
        var result = Select(".github/workflows/some-other-workflow.yml");

        Assert.False(result.SelectsAll);
        Assert.Contains("Infrastructure.Tests", result.TestProjects);
    }

    // --- Composition: union, aliases, layer 1, kill switch ----------------------------------

    [Fact]
    public void MultipleChangesUnionTheirTargets()
    {
        // Rules are additive across all changed files.
        var result = Select("src/Aspire.Hosting.Kafka/KafkaResource.cs", "extension/src/extension.ts");

        Assert.Contains("Aspire.Hosting.Kafka.Tests", result.TestProjects);
        Assert.Contains("job:extension-unit", result.Jobs);
    }

    [Fact]
    public void AliasesExpandToConcreteProjects()
    {
        var result = Select("tests/Aspire.Hosting.Tests/SomeHelper.cs");

        Assert.DoesNotContain("ALL_HOSTING_TESTS", result.TestProjects);
        Assert.Contains("Aspire.Hosting.Yarp.Tests", result.TestProjects);
    }

    [Fact]
    public void Layer1AffectedProjectsAreIncluded()
    {
        // The graph tool (Layer 1) reports a project the curated map would not select for this
        // file (e.g. via a ProjectReference / CPM / Directory.Build edge). It must be unioned in.
        var result = new TestSelector(s_mapPath, s_allTestProjects)
            .Select(["src/Aspire.Hosting.Kafka/KafkaResource.cs"], ["Aspire.Hosting.Redis.Tests"], new SelectorOptions());

        Assert.Contains("Aspire.Hosting.Kafka.Tests", result.TestProjects);
        Assert.Contains("Aspire.Hosting.Redis.Tests", result.TestProjects);
    }

    [Fact]
    public void KillSwitchForcesAll()
    {
        var result = new TestSelector(s_mapPath, s_allTestProjects)
            .Select(["src/Aspire.Cli/Program.cs"], [], new SelectorOptions(ForceAll: true));

        Assert.True(result.SelectsAll);
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
