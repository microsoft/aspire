// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class OpenCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3", true)]
    [InlineData("2.0.0-beta.1", false)]
    [InlineData("2.0.0-beta.1", true)]
    public async Task ScanAsync_WhenCliIsInstalled_RecordsVersionWithoutChangingConfiguration(string version, bool hasProjectConfiguration)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        if (hasProjectConfiguration)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc"), "{}");
        }
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var runner = new TestAgentCliRunner { OpenCodeVersion = SemVersion.Parse(version, SemVersionStyles.Strict) };
        var agent = CreateAgent(runner, workspace.CreateExecutionContext());
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(clients.OpenCode, version, false)], detections);
        Assert.Equal(["opencode"], runner.Commands);
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
        var list = Assert.IsAssignableFrom<IList<AgentClientDetection>>(detections);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = new(clients.CopilotCli, null, false));
        Assert.Throws<NotSupportedException>(list.Clear);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_WithOpenCodeDirectoryButNoConfigFiles_DoesNotDetectClient(bool hasUnrelatedFile)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configDirectory = workspace.CreateDirectory(".opencode");
        if (hasUnrelatedFile)
        {
            await File.WriteAllTextAsync(Path.Combine(configDirectory.FullName, "other.json"), "{}");
        }
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var runner = new TestAgentCliRunner();
        var agent = CreateAgent(runner, workspace.CreateExecutionContext());
        var clients = new TestAgentClients(agent);
        var directories = CreateScanDirectories(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await agent.ScanAsync(clients.All, directories.WorkingDirectory, directories.WorkspaceRoot, CancellationToken.None).DefaultTimeout();

        Assert.Empty(detections);
        Assert.Equal(["opencode"], runner.Commands);
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
    }

    private static OpenCodeAgentEnvironmentScanner CreateAgent(TestAgentCliRunner runner, CliExecutionContext executionContext)
        => new(runner, executionContext, new TestEnvironment(), NullLogger<OpenCodeAgentEnvironmentScanner>.Instance);

    private static (DirectoryInfo WorkingDirectory, DirectoryInfo WorkspaceRoot) CreateScanDirectories(DirectoryInfo workingDirectory, DirectoryInfo repositoryRoot)
        => (workingDirectory, repositoryRoot);
}
