// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Resources;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class AgentClientCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public void Clients_AreReadOnlyDefinitionsWithoutConfigurationReads()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("COPILOT_HOME", "\0invalid");
        context.SetVariable("CLAUDE_CONFIG_DIR", "\0invalid");
        context.SetVariable("OPENCODE_CONFIG", "\0invalid");
        context.SetVariable("VSCODE_APPDATA", "\0invalid");

        Assert.Equal(
            ["copilot", "vscode", "claude", "opencode"],
            context.Catalog.Clients.Select(client => client.Id));
        Assert.Equal(
            [AgentCommandStrings.Environment_Copilot, AgentCommandStrings.Environment_VsCode, "Claude Code", "OpenCode"],
            context.Catalog.Clients.Select(client => client.DisplayName));
        Assert.Equal(4, context.Catalog.Clients.Count);
        Assert.Equal(4, context.Catalog.Clients.Select(client => client.Environment).Distinct().Count());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AgentClient>)context.Catalog.Clients).Clear());
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
        Assert.Empty(context.CliRunner.Commands);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("vscode")]
    [InlineData("claude")]
    [InlineData("opencode")]
    public async Task UndetectedClient_CanConfigureWithItsRegisteredEnvironment(string clientId)
    {
        using var context = new AgentConfigurationTestContext(output);
        var client = context.Catalog.Clients.Single(client => client.Id == clientId);
        var detections = new List<AgentClientDetection>();
        foreach (var clients in context.Catalog.Clients.GroupBy(client => client.Environment))
        {
            if (await clients.Key.ScanAsync(context.Project, context.Project, CancellationToken.None).DefaultTimeout() is { } evidence)
            {
                detections.AddRange(clients.Select(entry => new AgentClientDetection(entry, evidence.Version, evidence.IsInsiders)));
            }
        }
        var probes = context.CliRunner.Commands.ToArray();

        Assert.Empty(detections);
        Assert.Equal(["copilot", "code", "code-insiders", "claude", "opencode"], probes);
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());

        var result = await context.Service.ConfigureAsync(
            context.Request([client], detections: detections), CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Targets.Count);
        Assert.All(result.Targets, target =>
        {
            Assert.Equal([client], target.Clients);
            Assert.Equal(AgentConfigurationStatus.Configured, target.Status);
            Assert.True(File.Exists(target.TargetPath));
        });
        Assert.Equal(probes, context.CliRunner.Commands);
        Assert.Equal(0, context.HookInstaller.Calls);
    }

}
