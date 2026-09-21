// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Agents;

/// <summary>
/// Orchestrates offline native registration and the independently selected CLI-managed tool skills.
/// </summary>
internal sealed class AgentInitService(
    AgentClientCatalog catalog,
    AgentConfigurationWriter writer,
    IAgentSkillInstaller skillInstaller,
    ITelemetryHookConfigurator hooks,
    CliExecutionContext executionContext,
    IEnvironment environment) : IAgentInitService
{
    public async Task<AgentInitResult> ConfigureAsync(AgentInitRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Assets.HasAssets || request.Clients.Count == 0)
        {
            return new AgentInitResult([]);
        }

        var results = new List<AgentTargetResult>();
        IEnumerable<AgentConfigurationTarget> nativeTargets = request.Assets.Mcp || request.Assets.AspireSkills
            ? catalog.GetTargets(request, executionContext, environment)
            : [];
        results.AddRange(await writer.ApplyAsync(nativeTargets.Concat(hooks.Plan(request)), cancellationToken));

        cancellationToken.ThrowIfCancellationRequested();
        if (request.Assets.Playwright || request.Assets.DotnetInspect)
        {
            results.AddRange(await skillInstaller.InstallAsync(request, cancellationToken));
        }

        return new AgentInitResult(results);
    }
}
