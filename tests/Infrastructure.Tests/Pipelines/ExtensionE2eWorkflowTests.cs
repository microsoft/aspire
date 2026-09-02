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
    private const string CallerWorkflowRelativePath = ".github/workflows/tests.yml";
    private const string ExtensionUnitWorkflowRelativePath = ".github/workflows/extension-unit-tests.yml";

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

        var prepareCliStep = Assert.Single(steps, step => Scalar(step, "name") == "Prepare Aspire CLI and package hive");
        var prepareCliScript = Scalar(prepareCliStep, "run") ?? string.Empty;
        Assert.Contains(
            "\"ASPIRE_DCP_PATH=$($dcp.Directory.FullName)\" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append",
            prepareCliScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"ASPIRE_CLI_PACKAGES=$hiveDir\" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append",
            prepareCliScript,
            StringComparison.Ordinal);
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

        // The condition is a folded block, so compare on collapsed whitespace rather than the layout.
        var e2eCondition = string.Join(' ', (Scalar(extensionE2eJob, "if") ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // Selection still decides whether the shards run at all.
        Assert.Contains("needs.setup_for_tests.outputs.run_extension_e2e == 'true'", e2eCondition, StringComparison.Ordinal);

        // !cancelled() rather than success(): extension_tests_win is in `needs` only because it
        // uploads the VSIX the shards install, and its packaging steps carry the same condition, so a
        // unit-test failure must not take the E2E signal with it. Assert the job's result is not
        // consulted at all -- reintroducing it is the regression this pins.
        Assert.StartsWith("${{ !cancelled() &&", e2eCondition, StringComparison.Ordinal);
        Assert.DoesNotContain("needs.extension_tests_win.result", e2eCondition, StringComparison.Ordinal);

        // The artifact producers stay required by result: without them every shard fails on a
        // download, which reports nothing useful about the extension.
        foreach (var producer in new[] { "build_packages", "build_cli_archive_linux", "build_cli_archive_windows", "extension_bootstrap_linux" })
        {
            Assert.Contains($"needs.{producer}.result == 'success'", e2eCondition, StringComparison.Ordinal);
        }

        // The VSIX the shards install is published even when the unit tests fail; without this the
        // decoupling above buys nothing because the artifact would never exist.
        var extensionUnitJobs = LoadWorkflowJobs(ExtensionUnitWorkflowRelativePath);
        var extensionUnitJob = (YamlMappingNode)extensionUnitJobs.Children[new YamlScalarNode("extension_tests_win")];
        var unitSteps = ((YamlSequenceNode)extensionUnitJob.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();
        var uploadStep = Assert.Single(unitSteps, step => Scalar(step, "name") == "Upload VSIX");
        Assert.Contains("!cancelled()", Scalar(uploadStep, "if") ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("inputs.packageVsix", Scalar(uploadStep, "if") ?? string.Empty, StringComparison.Ordinal);
        var packageStep = Assert.Single(unitSteps, step => Scalar(step, "name") == "Package VSIX");
        Assert.Contains("!cancelled()", Scalar(packageStep, "if") ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("inputs.packageVsix", Scalar(packageStep, "if") ?? string.Empty, StringComparison.Ordinal);

        var resultsJob = (YamlMappingNode)jobs.Children[new YamlScalarNode("results")];
        var steps = (YamlSequenceNode)resultsJob.Children[new YamlScalarNode("steps")];
        var failureStep = Assert.Single(steps.Cast<YamlMappingNode>(), step => Scalar(step, "name") == "Fail if any dependency failed");
        var condition = Scalar(failureStep, "if");
        const string unexpectedSkipCheck = "needs.extension_e2e_tests.result == 'skipped'";
        Assert.Equal(2, condition?.Split(unexpectedSkipCheck, StringSplitOptions.None).Length - 1);
    }

    private static YamlMappingNode LoadExtensionE2eJob() => ExtensionE2eWorkflow.Job();

    private static YamlMappingNode LoadWorkflowJobs(string relativePath)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot.Path, relativePath)));
        yaml.Load(reader);

        var root = Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
        return Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("jobs")]);
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
