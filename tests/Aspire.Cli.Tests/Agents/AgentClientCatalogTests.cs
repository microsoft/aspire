// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

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
            ["copilot-cli", "copilot-app", "vscode", "claude-code", "opencode"],
            context.Catalog.Clients.Select(client => client.Id));
        Assert.Equal(
            ["GitHub Copilot CLI", "GitHub Copilot App", "VS Code", "Claude Code", "OpenCode"],
            context.Catalog.Clients.Select(client => client.DisplayName));
        Assert.All(context.Catalog.Clients, client => Assert.Same(client, context.Catalog.Get(client.Kind)));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AgentClientDescriptor>)context.Catalog.Clients).Clear());
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Fact]
    public void GetTargets_OnlyInvokesSelectedClientDefinitions()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("COPILOT_HOME", "\0invalid");
        context.SetVariable("CLAUDE_CONFIG_DIR", "\0invalid");
        context.SetVariable("VSCODE_APPDATA", "\0invalid");
        var request = context.Request([AgentClientKind.OpenCode], mcp: true);

        var targets = context.Catalog.GetTargets(request, context.ExecutionContext, context.Environment).ToArray();

        Assert.Equal(4, targets.Length);
        Assert.All(targets, target => Assert.Equal([AgentClientKind.OpenCode], target.Clients));
        Assert.Equal([AgentConfigurationScope.Project, AgentConfigurationScope.User], targets.Select(target => target.Scope).Distinct());
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetTargets_SharedCopilotSourceIsContributedOnce(bool includeCopilotFrontends)
    {
        using var context = new AgentConfigurationTestContext(output);
        AgentClientKind[] clients = includeCopilotFrontends
            ? [AgentClientKind.CopilotCli, AgentClientKind.CopilotApp, AgentClientKind.VsCode]
            : [AgentClientKind.VsCode];
        var request = context.Request(clients, mcp: true);

        var targets = context.Catalog.GetTargets(request, context.ExecutionContext, context.Environment).ToArray();

        Assert.Equal(includeCopilotFrontends ? 6 : 4, targets.Length);
        var plugins = targets.Where(target => target.Asset is AgentAssetKind.AspireSkills).ToArray();
        Assert.Equal(2, plugins.Length);
        Assert.All(plugins, target => Assert.Equal(clients, target.Clients));
        Assert.Equal(targets.Length, targets.Select(target => (target.Path, target.Entry)).Distinct().Count());
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }
}
