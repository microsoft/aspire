// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.Extensions.FileSystemGlobbing;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

[Trait("Category", "AgenticWorkflow")]
public sealed class ValidateAgenticWorkflowsTests(ITestOutputHelper output)
{
    private const string WorkflowRelativePath = ".github/workflows/validate-agentic-workflows.yml";
    private const string LockPath = ".github/workflows/test-agent.lock.yml";
    private const string ActionsLockPath = ".github/aw/actions-lock.json";

    [Theory]
    [InlineData(true, ".github/workflows/analyze-ci-failure.md")]
    [InlineData(true, ".github/workflows/analyze-ci-failure.lock.yml")]
    [InlineData(true, ".github/workflows/agentics-maintenance-microsoft-aspire.dev.yml")]
    [InlineData(true, ".github/workflows/copilot-setup-steps.yml")]
    [InlineData(true, ActionsLockPath)]
    [InlineData(true, ".github/actionlint.yaml")]
    [InlineData(true, WorkflowRelativePath)]
    [InlineData(true, ".github/workflows/report-agentic-validation.yml")]
    [InlineData(true, ".github/workflows/agentic-validation-report.js")]
    [InlineData(true, ".github/workflows/new-agent.md")]
    [InlineData(true, ".github/workflows/nested/new-agent.md")]
    [InlineData(false, ".github/workflows/README.md")]
    [InlineData(false, ".github/workflows/nested/README.md")]
    [InlineData(true, ".github/workflows/README.md", ".github/workflows/new-agent.md")]
    public void WorkflowRunsForAgenticInputs(bool expected, params string[] changedPaths)
    {
        var root = LoadWorkflow();
        var pullRequest = Mapping(Mapping(root, "on"), "pull_request");
        var paths = Sequence(pullRequest, "paths").Children.Select(node => node.ToString()).ToArray();
        // GitHub applies paths in order: a later "!.../README.md" excludes an earlier Markdown match.
        // https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#onpushpull_requestpull_request_targetpathspaths-ignore
        var matcher = new Matcher(StringComparison.Ordinal, preserveFilterOrder: true);
        foreach (var path in paths)
        {
            if (path.StartsWith('!'))
            {
                matcher.AddExclude(path[1..]);
            }
            else
            {
                matcher.AddInclude(path);
            }
        }

        Assert.Equal(expected, matcher.Match(changedPaths).HasMatches);
    }

    [Theory]
    [InlineData(LockPath, "?? .github/workflows/test-agent.lock.yml")]
    [InlineData(ActionsLockPath, " M .github/aw/actions-lock.json")]
    [RequiresTools(["git", "bash"])]
    public async Task GeneratedFileVerificationRejectsDrift(string changedPath, string expectedStatus)
    {
        using var workspace = CreateRepository();
        if (changedPath == LockPath)
        {
            File.Delete(GetFullPath(workspace, LockPath));
            CommitAll(workspace, "Remove generated lock");
            WriteFile(workspace, LockPath, "untracked generated lock\n");
        }
        else
        {
            File.AppendAllText(GetFullPath(workspace, changedPath), "changed\n");
        }

        var result = await ProcessRunner.RunAsync(
            output,
            "bash",
            ["-c", Scalar(Step(Steps(LoadWorkflow()), "Verify generated files are up to date"), "run")],
            workspace.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Generated agentic workflow files are not up to date:", result.Output, StringComparison.Ordinal);
        Assert.Contains(expectedStatus, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node", "git"])]
    public async Task FailureReportingBehaviors()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var script = Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "WorkflowScripts", "agentic-validation-report.test.js");
        var result = await ProcessRunner.RunAsync(output, "node", ["--test", script], workspace.Path);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["git", "bash"])]
    public async Task GeneratedFileVerificationPreservesCleanAndDeletedFileResults(bool deleteGeneratedFile)
    {
        using var workspace = CreateRepository();
        if (deleteGeneratedFile)
        {
            File.Delete(GetFullPath(workspace, LockPath));
        }
        var result = await ProcessRunner.RunAsync(
            output, "bash",
            ["-c", Scalar(Step(Steps(LoadWorkflow()), "Verify generated files are up to date"), "run")],
            workspace.Path);

        Assert.Equal(deleteGeneratedFile ? 1 : 0, result.ExitCode);
    }

    [Fact]
    public void ReportingPreservesTrustBoundariesAndCompilerIdentity()
    {
        var validator = LoadWorkflow();
        Assert.Equal(["contents"], Mapping(validator, "permissions").Children.Keys.Select(key => key.ToString()));
        Assert.Equal("read", Scalar(Mapping(validator, "permissions"), "contents"));
        var steps = Steps(validator);
        Assert.Equal(["checkout", "compiler", "compile", "drift", "lint", "sdk", "restore", "contracts"],
            steps.Select(step => Scalar(step, "id")).Where(id => id.Length > 0));
        Assert.All(steps, step => Assert.Equal("", Scalar(step, "continue-on-error")));
        var compiler = Assert.Single(steps, step => Scalar(step, "id") == "compiler");
        Assert.Equal("${{ env.GH_AW_VERSION }}", Scalar(Mapping(compiler, "with"), "version"));
        Assert.Equal("Install gh-aw extension (${{ env.GH_AW_VERSION }})", Scalar(compiler, "name"));

        var bootstrap = LoadWorkflow(".github/workflows/copilot-setup-steps.yml");
        var bootstrapSteps = Sequence(Mapping(Mapping(bootstrap, "jobs"), "copilot-setup-steps"), "steps");
        var bootstrapCompiler = Assert.Single(bootstrapSteps.Children.Cast<YamlMappingNode>(),
            step => Scalar(step, "uses").StartsWith("github/gh-aw-actions/setup-cli@", StringComparison.Ordinal));
        Assert.Equal(Scalar(Mapping(bootstrapCompiler, "with"), "version"), Scalar(Mapping(validator, "env"), "GH_AW_VERSION"));

        var summary = Step(steps, "Summarize validation failure");
        Assert.Equal("${{ failure() && steps.checkout.outcome == 'success' }}", Scalar(summary, "if"));
        Assert.Equal("${{ toJSON(steps) }}", Scalar(Mapping(summary, "env"), "VALIDATION_STEPS"));

        var reporter = LoadWorkflow(".github/workflows/report-agentic-validation.yml");
        Assert.Equal(["workflow_run"], Mapping(reporter, "on").Children.Keys.Select(key => key.ToString()));
        var trigger = Mapping(Mapping(reporter, "on"), "workflow_run");
        Assert.Equal(["Validate Agentic Workflows"], Sequence(trigger, "workflows").Children.Select(node => node.ToString()));
        Assert.Equal(["completed"], Sequence(trigger, "types").Children.Select(node => node.ToString()));
        var permissions = Mapping(reporter, "permissions").Children.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.ToString());
        Assert.Equal(new Dictionary<string, string> { ["contents"] = "read", ["actions"] = "read", ["pull-requests"] = "write" }, permissions);

        var reportSteps = Sequence(Mapping(Mapping(reporter, "jobs"), "report"), "steps").Children.Cast<YamlMappingNode>().ToArray();
        Assert.Equal(["actions/checkout", "actions/github-script"], reportSteps.Select(step => Scalar(step, "uses").Split('@')[0]));
        Assert.Equal("${{ github.sha }}", Scalar(Mapping(reportSteps[0], "with"), "ref"));
        Assert.Equal("false", Scalar(Mapping(reportSteps[0], "with"), "persist-credentials"));
    }

    private TemporaryWorkspace CreateRepository()
    {
        var workspace = TemporaryWorkspace.Create(output);
        GitCli.Run(workspace.Path, "init", "-q", "-b", "main");
        GitCli.Run(workspace.Path, "config", "user.email", "test@example.com");
        GitCli.Run(workspace.Path, "config", "user.name", "Test");
        GitCli.Run(workspace.Path, "config", "commit.gpgsign", "false");

        WriteFile(workspace, LockPath, "generated lock\n");
        WriteFile(workspace, ActionsLockPath, "{}\n");
        CommitAll(workspace, "Create baseline");

        return workspace;
    }

    private static void WriteFile(TemporaryWorkspace workspace, string relativePath, string contents)
    {
        var path = GetFullPath(workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string GetFullPath(TemporaryWorkspace workspace, string relativePath)
        => Path.Combine(workspace.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void CommitAll(TemporaryWorkspace workspace, string message)
    {
        GitCli.Run(workspace.Path, "add", "-A");
        GitCli.Run(workspace.Path, "commit", "-q", "-m", message);
    }

    private static YamlMappingNode LoadWorkflow(string relativePath = WorkflowRelativePath)
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, relativePath));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static IReadOnlyList<YamlMappingNode> Steps(YamlMappingNode root)
        => Sequence(Mapping(Mapping(root, "jobs"), "validate"), "steps").Children.Cast<YamlMappingNode>().ToArray();

    private static YamlMappingNode Step(IReadOnlyList<YamlMappingNode> steps, string name)
        => Assert.Single(steps, step => Scalar(step, "name") == name);

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static YamlSequenceNode Sequence(YamlMappingNode node, string key)
        => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value.ToString() : "";
}
