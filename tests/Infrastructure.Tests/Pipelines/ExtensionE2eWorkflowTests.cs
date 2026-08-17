// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests.Pipelines;

/// <summary>
/// Guards the advisory-failure contract in <c>.github/workflows/extension-e2e-tests.yml</c>.
///
/// Every shard still runs and produces diagnostics. Only issue-tracked rows may treat completed
/// VS Code test failures as advisory; setup, harness, and cleanup failures remain blocking.
/// </summary>
public sealed class ExtensionE2eWorkflowTests
{
    private const string WorkflowRelativePath = ".github/workflows/extension-e2e-tests.yml";
    private const string CallerWorkflowRelativePath = ".github/workflows/tests.yml";

    [Fact]
    public void AdvisoryShardRowsRemainIssueTrackedWithoutWeakeningRunnerStep()
    {
        var job = LoadExtensionE2eJob();

        var rows = MatrixIncludeRows(job).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.False(row.Children.ContainsKey(new YamlScalarNode("allowFailure")));
            Assert.False(row.Children.ContainsKey(new YamlScalarNode("disabledIssue")));
        });
        Assert.Contains(rows, row => row.Children.ContainsKey(new YamlScalarNode("advisoryIssue")));
        Assert.Contains(rows, row => !row.Children.ContainsKey(new YamlScalarNode("advisoryIssue")));

        var steps = ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();
        var runSuiteStep = Assert.Single(steps, step => Scalar(step, "name") == "Run extension E2E tests");
        Assert.False(runSuiteStep.Children.ContainsKey(new YamlScalarNode("if")));
        Assert.False(runSuiteStep.Children.ContainsKey(new YamlScalarNode("continue-on-error")));

        var environment = (YamlMappingNode)runSuiteStep.Children[new YamlScalarNode("env")];
        Assert.False(environment.Children.ContainsKey(new YamlScalarNode("ASPIRE_EXTENSION_E2E_ALLOW_TEST_FAILURE")));
        Assert.Equal("${{ matrix.advisoryIssue }}", Scalar(environment, "ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE"));
    }

    [Fact]
    public void SelectedExtensionE2eWorkflowMustRun()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot.Path, CallerWorkflowRelativePath)));
        yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var extensionE2eJob = (YamlMappingNode)jobs.Children[new YamlScalarNode("extension_e2e_tests")];
        Assert.Equal("${{ needs.setup_for_tests.outputs.run_extension_e2e == 'true' }}", Scalar(extensionE2eJob, "if"));

        var resultsJob = (YamlMappingNode)jobs.Children[new YamlScalarNode("results")];
        var steps = (YamlSequenceNode)resultsJob.Children[new YamlScalarNode("steps")];
        var failureStep = Assert.Single(steps.Cast<YamlMappingNode>(), step => Scalar(step, "name") == "Fail if any dependency failed");
        var condition = Scalar(failureStep, "if");
        const string unexpectedSkipCheck = "needs.extension_e2e_tests.result == 'skipped'";
        Assert.Equal(2, condition?.Split(unexpectedSkipCheck, StringSplitOptions.None).Length - 1);
    }

    private static YamlMappingNode LoadExtensionE2eJob()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot.Path, WorkflowRelativePath)));
        yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];

        return (YamlMappingNode)jobs.Children[new YamlScalarNode("extension_e2e")];
    }

    private static IEnumerable<YamlMappingNode> MatrixIncludeRows(YamlMappingNode job)
    {
        var strategy = (YamlMappingNode)job.Children[new YamlScalarNode("strategy")];
        var matrix = (YamlMappingNode)strategy.Children[new YamlScalarNode("matrix")];

        return ((YamlSequenceNode)matrix.Children[new YamlScalarNode("include")]).Cast<YamlMappingNode>();
    }

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;
}
