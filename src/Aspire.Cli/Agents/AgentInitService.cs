// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Agents;

/// <summary>
/// Configures selected assets and deduplicated native client targets.
/// </summary>
internal interface IAgentInitService
{
    Task<AgentInitResult> ConfigureAsync(AgentInitRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates offline native registration and the independently selected CLI-managed tool skills.
/// </summary>
internal sealed class AgentInitService(
    AgentConfigurationWriter writer,
    IAgentSkillInstaller skillInstaller,
    ITelemetryHookConfigurator hooks) : IAgentInitService
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
            ? request.Clients.Select(client => client.Environment).Distinct().SelectMany(environment => environment.GetTargets(request))
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
