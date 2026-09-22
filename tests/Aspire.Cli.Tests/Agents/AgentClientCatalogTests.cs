// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;
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
            ["copilot-cli", "copilot-app", "vscode", "claude-code", "opencode"],
            context.Catalog.Clients.Select(client => client.Id));
        Assert.Equal(
            ["GitHub Copilot CLI", "GitHub Copilot App", "VS Code", "Claude Code", "OpenCode"],
            context.Catalog.Clients.Select(client => client.DisplayName));
        Assert.Same(context.CopilotCli.Environment, context.CopilotApp.Environment);
        Assert.Equal(4, context.Catalog.Clients.Select(client => client.Environment).Distinct().Count());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AgentClient>)context.Catalog.Clients).Clear());
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
        Assert.Empty(context.CliRunner.Commands);
    }

    [Fact]
    public void GetTargets_OnlyCreatesTargetsForSelectedClients()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("COPILOT_HOME", "\0invalid");
        context.SetVariable("CLAUDE_CONFIG_DIR", "\0invalid");
        context.SetVariable("VSCODE_APPDATA", "\0invalid");
        var request = context.Request([context.OpenCode], mcp: true);

        var targets = request.Clients.Select(client => client.Environment).Distinct().SelectMany(environment => environment.GetTargets(request)).ToArray();

        Assert.Equal(4, targets.Length);
        Assert.All(targets, target => Assert.Equal([context.OpenCode], target.Clients));
        Assert.Equal([AgentConfigurationScope.Project, AgentConfigurationScope.User], targets.Select(target => target.Scope).Distinct());
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
        Assert.Empty(context.CliRunner.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetTargets_SharedCopilotSourceIsContributedOnce(bool includeCopilotFrontends)
    {
        using var context = new AgentConfigurationTestContext(output);
        AgentClient[] clients = includeCopilotFrontends
            ? [context.CopilotCli, context.CopilotApp, context.VsCode]
            : [context.VsCode];
        var request = context.Request(clients, mcp: true);

        var targets = request.Clients.Select(client => client.Environment).Distinct().SelectMany(environment => environment.GetTargets(request)).ToArray();

        Assert.Equal(includeCopilotFrontends ? 6 : 4, targets.Length);
        var plugins = targets.Where(target => target.Asset is AgentAssetKind.AspireSkills).ToArray();
        Assert.Equal(2, plugins.Length);
        Assert.All(plugins, target => Assert.Equal(clients, target.Clients));
        Assert.Equal(targets.Length, targets.Select(target => (target.Path, target.Entry)).Distinct().Count());
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("copilot-cli")]
    [InlineData("copilot-app")]
    [InlineData("vscode")]
    [InlineData("claude-code")]
    [InlineData("opencode")]
    public async Task UndetectedClient_CanConfigureWithItsRegisteredEnvironment(string clientId)
    {
        using var context = new AgentConfigurationTestContext(output);
        var client = context.Catalog.Clients.Single(client => client.Id == clientId);
        var detections = new List<AgentClientDetection>();
        foreach (var clients in context.Catalog.Clients.GroupBy(client => client.Environment))
        {
            detections.AddRange(await clients.Key.ScanAsync(clients.ToArray(), context.Project, context.Project, CancellationToken.None).DefaultTimeout());
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

    [Fact]
    public void SharedEnvironment_IsRegisteredAndSelectedOnce()
    {
        using var context = new AgentConfigurationTestContext(output);
        var requests = new List<AgentInitRequest>();
        var agent = new TestAgentClientEnvironment
        {
            GetTargetsCallback = request =>
            {
                requests.Add(request);
                return [];
            }
        };
        var clients = new TestAgentClients(agent);
        var catalog = new AgentClientCatalog([clients.CopilotCli, clients.CopilotApp]);
        var request = context.Request(catalog.Clients);

        Assert.Same(agent, Assert.Single(catalog.Clients.Select(client => client.Environment).Distinct()));
        Assert.Empty(Assert.Single(request.Clients.Select(client => client.Environment).Distinct()).GetTargets(request));
        Assert.Same(request, Assert.Single(requests));
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public void Catalog_StoresClientEnvironmentAssociationsWithoutInvokingThem()
    {
        var selected = new TestAgentClientEnvironment
        {
            ScanAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("Catalog lookup must not scan."),
            GetTargetsCallback = _ => throw new InvalidOperationException("Catalog lookup must not configure.")
        };
        var unselected = new TestAgentClientEnvironment
        {
            ScanAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("Catalog lookup must not scan."),
            GetTargetsCallback = _ => throw new InvalidOperationException("An unselected environment must not be invoked.")
        };
        var catalog = new AgentClientCatalog(
        [
            new("claude-code", "Claude Code", unselected),
            new("opencode", "OpenCode", selected)
        ]);

        Assert.Equal<IAgentClientEnvironment>([unselected, selected], catalog.Clients.Select(client => client.Environment));
        Assert.Same(selected, catalog.Clients.Single(client => client.Id == "opencode").Environment);
        Assert.Empty(selected.Calls);
        Assert.Empty(unselected.Calls);
    }
}
