// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Microsoft.Extensions.FileSystemGlobbing;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class ValidateAgenticWorkflowsTests(ITestOutputHelper output)
{
    private const string WorkflowRelativePath = ".github/workflows/validate-agentic-workflows.yml";
    private const string LockPath = ".github/workflows/test-agent.lock.yml";
    private const string ActionsLockPath = ".github/aw/actions-lock.json";

    [Theory]
    [InlineData(".github/workflows/analyze-ci-failure.md")]
    [InlineData(".github/workflows/analyze-ci-failure.lock.yml")]
    [InlineData(".github/workflows/agentics-maintenance-microsoft-aspire.dev.yml")]
    [InlineData(".github/workflows/copilot-setup-steps.yml")]
    [InlineData(ActionsLockPath)]
    [InlineData(".github/actionlint.yaml")]
    [InlineData(WorkflowRelativePath)]
    public void WorkflowRunsForAgenticInputs(string changedPath)
    {
        var root = LoadWorkflow();
        var pullRequest = Mapping(Mapping(root, "on"), "pull_request");
        var paths = Sequence(pullRequest, "paths").Children.Select(node => node.ToString()).ToArray();
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(paths);

        Assert.True(matcher.Match([changedPath]).HasMatches, $"{changedPath} does not trigger {WorkflowRelativePath}");
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

        var result = await RunScriptAsync(
            workspace,
            Scalar(Step(Steps(LoadWorkflow()), "Verify generated files are up to date"), "run"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Generated agentic workflow files are not up to date:", result.Output, StringComparison.Ordinal);
        Assert.Contains(expectedStatus, result.Output, StringComparison.Ordinal);
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

    private static async Task<(int ExitCode, string Output)> RunScriptAsync(
        TemporaryWorkspace workspace,
        string script)
    {
        using var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.WorkingDirectory = workspace.Path;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        process.Start();
        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdoutTask + await stderrTask;
        return (process.ExitCode, output);
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

    private static YamlMappingNode LoadWorkflow()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, WorkflowRelativePath));
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
