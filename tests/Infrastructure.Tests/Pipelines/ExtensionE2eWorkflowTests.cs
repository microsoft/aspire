// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests.Pipelines;

/// <summary>
/// Guards the advisory-failure contract in <c>.github/workflows/extension-e2e-tests.yml</c>.
///
/// Every shard still runs and produces diagnostics, but test failures are temporarily non-blocking.
/// Setup, compilation, and other harness failures remain blocking because only the test step uses
/// <c>continue-on-error</c>.
/// </summary>
public sealed class ExtensionE2eWorkflowTests
{
    private const string WorkflowRelativePath = ".github/workflows/extension-e2e-tests.yml";

    [Fact]
    public void AllShardRowsRunWithAllowedTestFailures()
    {
        var job = LoadExtensionE2eJob();

        var rows = MatrixIncludeRows(job).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal("true", Scalar(row, "allowFailure")));
        Assert.All(rows, row => Assert.False(row.Children.ContainsKey(new YamlScalarNode("disabledIssue"))));

        var steps = ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();
        var runSuiteStep = Assert.Single(steps, step => Scalar(step, "name") == "Run extension E2E tests");
        Assert.Null(Scalar(runSuiteStep, "if"));
        Assert.Equal("${{ matrix.allowFailure }}", Scalar(runSuiteStep, "continue-on-error"));
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
