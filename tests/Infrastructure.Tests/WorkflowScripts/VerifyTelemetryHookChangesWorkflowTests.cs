// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class VerifyTelemetryHookChangesWorkflowTests(ITestOutputHelper output)
{
    private const string WorkflowRelativePath = ".github/workflows/verify-telemetry-hook-changes.yml";
    private const string ProtectedHookPath = "src/Aspire.Cli/Agents/Hooks/track-telemetry.sh";
    private const string AspireSkillsInstallerPath = "src/Aspire.Cli/Agents/AspireSkills/AspireSkillsInstaller.cs";
    private const string AspireSkillsMetadataPath = "src/Aspire.Cli/Agents/AspireSkills/Embedded/aspire-skills.metadata.json";
    private const string AspireCliProjectPath = "src/Aspire.Cli/Aspire.Cli.csproj";
    private const string OldArchivePath = "src/Aspire.Cli/Agents/AspireSkills/Embedded/aspire-skills-v0.0.2.tgz";
    private const string NewArchivePath = "src/Aspire.Cli/Agents/AspireSkills/Embedded/aspire-skills-v0.0.3.tgz";
    private const string ArchiveChange = "archive";
    private const string SynchronizationBranch = "update-aspire-skills-bundle";

    private static readonly string[] s_requiredBundlePaths =
    [
        AspireSkillsInstallerPath,
        AspireSkillsMetadataPath,
        AspireCliProjectPath,
    ];

    [Fact]
    public void WorkflowProvidesMergeResultHistoryAndPullRequestHead()
    {
        var root = LoadWorkflow();
        var job = Mapping(Mapping(root, "jobs"), "verify");
        var steps = Sequence(job, "steps").Children.Cast<YamlMappingNode>().ToList();

        var checkout = Assert.Single(steps, step => ScalarOrNull(step, "uses")?.StartsWith("actions/checkout@", StringComparison.Ordinal) == true);
        var checkoutOptions = Mapping(checkout, "with");
        Assert.Equal("${{ github.sha }}", Scalar(checkoutOptions, "ref"));
        Assert.Equal("2", Scalar(checkoutOptions, "fetch-depth"));
        Assert.Equal("false", Scalar(checkoutOptions, "persist-credentials"));

        var verify = GetVerifyStep(steps);
        Assert.Equal("pwsh", Scalar(verify, "shell"));
        Assert.Equal("${{ github.head_ref }}", Scalar(Mapping(verify, "env"), "PR_HEAD_REF"));
        Assert.False(string.IsNullOrWhiteSpace(Scalar(verify, "run")));
    }

    [Fact]
    [RequiresTools(["git", "pwsh"])]
    public async Task FeatureBranchRejectsHookOnlyChange()
    {
        using var workspace = CreateRepository();
        ChangeFile(workspace, ProtectedHookPath);
        CommitAll(workspace, "Change telemetry hook only");

        var result = await RunPolicyAsync(workspace, "feature/hook-only");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Telemetry hook scripts can only change", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["git", "pwsh"])]
    public async Task FeatureBranchAcceptsCompleteBundleRefresh()
    {
        using var workspace = CreateRepository();
        ChangeFile(workspace, ProtectedHookPath);
        foreach (var path in s_requiredBundlePaths)
        {
            ChangeFile(workspace, path);
        }
        ReplaceArchive(workspace);
        CommitAll(workspace, "Refresh complete Aspire skills bundle");

        var result = await RunPolicyAsync(workspace, "feature/bundle-refresh");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Telemetry hook changes are part of a complete generated bundle update", result.Output, StringComparison.Ordinal);
        Assert.Contains("Telemetry hook branch policy satisfied", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AspireSkillsInstallerPath)]
    [InlineData(AspireSkillsMetadataPath)]
    [InlineData(AspireCliProjectPath)]
    [InlineData(ArchiveChange)]
    [RequiresTools(["git", "pwsh"])]
    public async Task FeatureBranchRejectsIncompleteBundleRefresh(string omittedChange)
    {
        using var workspace = CreateRepository();
        ChangeFile(workspace, ProtectedHookPath);
        foreach (var path in s_requiredBundlePaths)
        {
            if (!string.Equals(path, omittedChange, StringComparison.Ordinal))
            {
                ChangeFile(workspace, path);
            }
        }
        if (!string.Equals(omittedChange, ArchiveChange, StringComparison.Ordinal))
        {
            ReplaceArchive(workspace);
        }
        CommitAll(workspace, $"Refresh Aspire skills bundle without {omittedChange}");

        var result = await RunPolicyAsync(workspace, "feature/incomplete-bundle-refresh");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("as part of a complete generated bundle update", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["git", "pwsh"])]
    public async Task SynchronizationBranchAcceptsHookChange()
    {
        using var workspace = CreateRepository();
        ChangeFile(workspace, ProtectedHookPath);
        CommitAll(workspace, "Synchronize telemetry hook");

        var result = await RunPolicyAsync(workspace, SynchronizationBranch);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Telemetry hook branch policy satisfied", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("complete generated bundle update", result.Output, StringComparison.Ordinal);
    }

    private TemporaryWorkspace CreateRepository()
    {
        var workspace = TemporaryWorkspace.Create(output);
        GitCli.Run(workspace.Path, "init", "-q", "-b", "main");
        GitCli.Run(workspace.Path, "config", "user.email", "test@example.com");
        GitCli.Run(workspace.Path, "config", "user.name", "Test");
        GitCli.Run(workspace.Path, "config", "commit.gpgsign", "false");

        WriteFile(workspace, ProtectedHookPath, "#!/usr/bin/env bash\necho baseline\n");
        WriteFile(workspace, "src/Aspire.Cli/Agents/Hooks/track-telemetry.ps1", "Write-Output baseline\n");
        foreach (var path in s_requiredBundlePaths)
        {
            WriteFile(workspace, path, $"baseline {path}\n");
        }
        WriteFile(workspace, OldArchivePath, "baseline archive\n");
        CommitAll(workspace, "Create baseline");

        return workspace;
    }

    private async Task<CommandResult> RunPolicyAsync(TemporaryWorkspace workspace, string headRef)
    {
        var scriptPath = Path.Combine(workspace.Path, "verify-telemetry-hook-changes.ps1");
        await File.WriteAllTextAsync(scriptPath, GetPolicyScript());

        using var command = new PowerShellCommand(scriptPath, output)
            .WithWorkingDirectory(workspace.Path)
            .WithEnvironmentVariable("PR_HEAD_REF", headRef)
            .WithTimeout(TimeSpan.FromMinutes(1));

        return await command.ExecuteAsync();
    }

    private static void ReplaceArchive(TemporaryWorkspace workspace)
    {
        File.Delete(GetFullPath(workspace, OldArchivePath));
        WriteFile(workspace, NewArchivePath, "refreshed archive\n");
    }

    private static void ChangeFile(TemporaryWorkspace workspace, string relativePath)
    {
        File.AppendAllText(GetFullPath(workspace, relativePath), "changed\n");
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

    private static string GetPolicyScript()
    {
        var steps = Sequence(Mapping(Mapping(LoadWorkflow(), "jobs"), "verify"), "steps")
            .Children
            .Cast<YamlMappingNode>()
            .ToList();
        return Scalar(GetVerifyStep(steps), "run");
    }

    private static YamlMappingNode GetVerifyStep(IReadOnlyList<YamlMappingNode> steps)
        => Assert.Single(
            steps,
            step => string.Equals(
                ScalarOrNull(step, "name"),
                "Verify telemetry scripts use the synchronization branch",
                StringComparison.Ordinal));

    private static YamlMappingNode LoadWorkflow()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, WorkflowRelativePath));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static YamlSequenceNode Sequence(YamlMappingNode node, string key)
        => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key)
        => Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;

    private static string? ScalarOrNull(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? Assert.IsType<YamlScalarNode>(value).Value
            : null;
}
