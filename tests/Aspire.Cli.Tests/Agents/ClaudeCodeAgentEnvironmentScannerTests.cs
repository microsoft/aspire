// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class ClaudeCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScanAsync_WithProjectConfiguration_DetectsWithoutExecutable(bool inParent, bool mcpFileOnly)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var workingDirectory = workspace.CreateDirectory("project");
        var configDirectory = inParent ? workspace.WorkspaceRoot : workingDirectory;
        if (mcpFileOnly)
        {
            await File.WriteAllTextAsync(Path.Combine(configDirectory.FullName, ".mcp.json"), "{}");
        }
        else
        {
            configDirectory.CreateSubdirectory(".claude");
        }
        var runner = new TestAgentCliRunner();
        var agent = CreateAgent(runner, workspace.CreateExecutionContext());
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(workingDirectory, workspace.WorkspaceRoot);

        var detections = await agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(clients.ClaudeCode, null, false)], detections);
        Assert.Equal(["claude"], runner.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_WhenCliIsInstalled_RecordsVersionWithoutCreatingConfiguration(bool hasProjectConfiguration)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        if (hasProjectConfiguration)
        {
            workspace.CreateDirectory(".claude");
        }
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var runner = new TestAgentCliRunner { ClaudeCodeVersion = new SemVersion(2, 1, 0) };
        var agent = CreateAgent(runner, workspace.CreateExecutionContext());
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(clients.ClaudeCode, "2.1.0", false)], detections);
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
        var list = Assert.IsAssignableFrom<IList<AgentClientDetection>>(detections);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = new(clients.CopilotCli, null, false));
        Assert.Throws<NotSupportedException>(list.Clear);
    }

    [Fact]
    public async Task ScanAsync_WithNoEvidence_ReturnsReadOnlyEmptyList()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner();
        var agent = CreateAgent(runner, workspace.CreateExecutionContext());
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Empty(detections);
        Assert.Equal(["claude"], runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));
        var collection = Assert.IsAssignableFrom<ICollection<AgentClientDetection>>(detections);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Add(new(clients.ClaudeCode, null, false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_DoesNotSearchAboveRepositoryRoot(bool trailingSeparator)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        workspace.CreateDirectory(".claude");
        var repositoryRoot = workspace.CreateDirectory("repo");
        var workingDirectory = repositoryRoot.CreateSubdirectory("src");
        var agent = CreateAgent(new TestAgentCliRunner(), workspace.CreateExecutionContext());
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(
            workingDirectory,
            trailingSeparator ? new DirectoryInfo(repositoryRoot.FullName + Path.DirectorySeparatorChar) : repositoryRoot);

        var detections = await agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Empty(detections);
    }

    [Fact]
    public async Task ScanAsync_DoesNotTreatHomeConfigurationAsProjectConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var homeDirectory = workspace.CreateDirectory("home");
        homeDirectory.CreateSubdirectory(".claude");
        var workingDirectory = homeDirectory.CreateSubdirectory("project");
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workingDirectory, homeDirectory: homeDirectory);
        var agent = CreateAgent(new TestAgentCliRunner(), executionContext);
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(workingDirectory, homeDirectory);

        var detections = await agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Empty(detections);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScanAsync_WithWorkingDirectoryOutsideRepository_OnlyUsesSelectedRoot(bool workingDirectoryIsParent, bool hasTargetConfiguration)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var repositoryRoot = workspace.CreateDirectory("repo");
        var unrelatedParent = workingDirectoryIsParent ? workspace.WorkspaceRoot : workspace.CreateDirectory("unrelated");
        unrelatedParent.CreateSubdirectory(".claude");
        var workingDirectory = workingDirectoryIsParent ? unrelatedParent : unrelatedParent.CreateSubdirectory("nested");
        if (hasTargetConfiguration)
        {
            repositoryRoot.CreateSubdirectory(".claude");
        }
        var agent = CreateAgent(new TestAgentCliRunner(), workspace.CreateExecutionContext());
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(workingDirectory, repositoryRoot);

        var detections = await agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>(
            hasTargetConfiguration ? [new(clients.ClaudeCode, null, false)] : [],
            detections);
    }

    [Fact]
    public async Task ScanAsync_WithWorkspaceRootOverride_UsesSelectedRoot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var repositoryRoot = workspace.CreateDirectory("repo");
        repositoryRoot.CreateSubdirectory(".claude");
        var workingDirectory = workspace.CreateDirectory("outside");
        var agent = CreateAgent(new TestAgentCliRunner(), workspace.CreateExecutionContext());
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(workingDirectory, repositoryRoot);

        var detections = await agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(clients.ClaudeCode, null, false)], detections);
    }

    [Theory]
    [InlineData("""{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""")]
    [InlineData("{ invalid json content")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task ScanAsync_LeavesExistingConfigurationUntouched(string content)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        await File.WriteAllTextAsync(configPath, content);
        File.SetLastWriteTimeUtc(configPath, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var lastWriteTime = File.GetLastWriteTimeUtc(configPath);
        var agent = CreateAgent(new TestAgentCliRunner(), workspace.CreateExecutionContext());
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(clients.ClaudeCode, null, false)], detections);
        Assert.Equal(content, await File.ReadAllTextAsync(configPath));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(configPath));
        Assert.Equal([configPath], Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ScanAsync_WhenCancelled_DoesNotProbeOrWrite()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner();
        var agent = CreateAgent(runner, workspace.CreateExecutionContext());
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, new CancellationToken(canceled: true))).DefaultTimeout();

        Assert.Empty(runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));
    }

    private static ClaudeCodeAgentEnvironmentScanner CreateAgent(TestAgentCliRunner runner, CliExecutionContext executionContext)
        => new(runner, executionContext, new TestEnvironment(), NullLogger<ClaudeCodeAgentEnvironmentScanner>.Instance);

    private static (DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot) CreateScanDirectories(DirectoryInfo workingDirectory, DirectoryInfo repositoryRoot)
        => (workingDirectory, repositoryRoot);
}
