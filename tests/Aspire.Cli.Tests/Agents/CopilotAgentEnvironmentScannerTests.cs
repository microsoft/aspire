// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class CopilotAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ScanAsync_ReturnsInstalledCopilotEnvironmentsWithoutCreatingConfiguration(
        bool cliInstalled, bool appInstalled)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var runner = new FakeCopilotCliRunner(cliInstalled ? new SemVersion(1, 0, 0) : null);
        var scanner = new CopilotAgentEnvironmentScanner(runner,
            new FakeCopilotAppInstallationDetector(appInstalled ? "AI_AGENT" : null),
            new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot
        };
        var before = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories);

        var environments = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal((cliInstalled ? AgentEnvironment.CopilotCli : AgentEnvironment.None) |
            (appInstalled ? AgentEnvironment.CopilotApp : AgentEnvironment.None), environments);
        Assert.True(runner.WasCalled);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_WhenInVsCode_SkipsCliProbeWithoutClaimingEditorDetection(bool appInstalled)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var runner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var environment = new TestEnvironment(new Dictionary<string, string?> { ["TERM_PROGRAM"] = "vscode" });
        var scanner = new CopilotAgentEnvironmentScanner(runner,
            new FakeCopilotAppInstallationDetector(appInstalled ? "AI_AGENT" : null),
            environment, NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot
        };

        var environments = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(appInstalled ? AgentEnvironment.CopilotApp : AgentEnvironment.None, environments);
        Assert.False(runner.WasCalled);
    }

    [Theory]
    [InlineData("{ invalid json content")]
    [InlineData("")]
    [InlineData("""{"mcpServers":{"aspire":{"command":"custom-aspire"}}}""")]
    public async Task ScanAsync_WithMcpConfiguration_DetectsInstalledCliWithoutModifyingFile(string content)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configDirectory = workspace.CreateDirectory(".copilot");
        var configPath = Path.Combine(configDirectory.FullName, "mcp-config.json");
        await File.WriteAllTextAsync(configPath, content);
        var scanner = new CopilotAgentEnvironmentScanner(new FakeCopilotCliRunner(new SemVersion(1, 0, 0)),
            new FakeCopilotAppInstallationDetector(), new TestEnvironment(),
            NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot
        };

        var environments = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentEnvironment.CopilotCli, environments);
        Assert.Equal(content, await File.ReadAllTextAsync(configPath));
    }

    private sealed class FakeCopilotAppInstallationDetector(string? marker = null) : ICopilotAppInstallationDetector
    {
        public string? GetInstallationMarker() => marker;
    }

    private sealed class FakeCopilotCliRunner(SemVersion? version) : ICopilotCliRunner
    {
        public bool WasCalled { get; private set; }

        public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;

            return Task.FromResult(version);
        }
    }
}
