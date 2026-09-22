// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Resources;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class AgentEnvironmentScannerTests(ITestOutputHelper output)
{
    [Fact]
    public void EnvironmentMetadata_DoesNotReadConfigurationOrProbeClients()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("COPILOT_HOME", "\0invalid");
        context.SetVariable("CLAUDE_CONFIG_DIR", "\0invalid");
        context.SetVariable("OPENCODE_CONFIG", "\0invalid");
        context.SetVariable("VSCODE_APPDATA", "\0invalid");

        Assert.Equal(
            ["copilot", "vscode", "claude", "opencode"],
            context.Environments.Select(client => client.Id));
        Assert.Equal(
            [AgentCommandStrings.Environment_Copilot, AgentCommandStrings.Environment_VsCode, "Claude Code", "OpenCode"],
            context.Environments.Select(client => client.DisplayName));
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
        Assert.Empty(context.CliRunner.Commands);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("vscode")]
    [InlineData("claude")]
    [InlineData("opencode")]
    public async Task UndetectedEnvironment_CanConfigureWithoutClientEvidence(string clientId)
    {
        using var context = new AgentConfigurationTestContext(output);
        var client = context.Environments.Single(client => client.Id == clientId);
        var scanContext = new AgentEnvironmentScanContext(context.Project, context.Project);
        foreach (var scanner in context.Environments)
        {
            await scanner.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();
        }
        var probes = context.CliRunner.Commands.ToArray();

        Assert.Empty(scanContext.DetectedClients);
        Assert.Equal(["copilot", "code", "code-insiders", "claude", "opencode"], probes);
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());

        var result = await context.Service.ConfigureAsync(
            context.Request([client], detections: scanContext.DetectedClients), CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.Equal(clientId == "vscode" ? 1 : 2, result.Targets.Count);
        Assert.All(result.Targets, target =>
        {
            Assert.Equal([client], target.Environments);
            Assert.Equal(AgentConfigurationStatus.Configured, target.Status);
            Assert.True(File.Exists(target.TargetPath));
        });
        Assert.Equal(probes, context.CliRunner.Commands);
        Assert.Equal(0, context.HookInstaller.Calls);
    }
}
