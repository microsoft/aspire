// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Copilot;

namespace Aspire.Cli.Agents;

/// <summary>
/// Detects agent environments by running all registered scanners.
/// </summary>
internal sealed class AgentEnvironmentDetector(IEnumerable<IAgentEnvironmentScanner> scanners) : IAgentEnvironmentDetector
{
    /// <summary>
    /// Gets the agents configured by default: the detected agents, or GitHub Copilot when none is detected.
    /// </summary>
    public static IAgentClient[] GetDefaultClients(IEnumerable<IAgentClient> clients, AgentEnvironment detectedEnvironments)
    {
        // Shared discovery does not authorize configuring another agent. Defaults express
        // detected agent use, independently of assets and their destinations. For example,
        // skills in ~/.agents/skills are read by several agents that might not be in use.
        var defaultClients = clients.Where(client => (client.Environments & detectedEnvironments) != AgentEnvironment.None).ToArray();
        return defaultClients.Length > 0
            ? defaultClients
            : [clients.Single(client => string.Equals(client.Id, CopilotAgentClient.ClientId, StringComparisons.AgentClientId))];
    }

    /// <inheritdoc />
    public async Task<AgentEnvironment> DetectAsync(
        AgentEnvironmentScanContext context,
        CancellationToken cancellationToken)
    {
        var environments = AgentEnvironment.None;
        foreach (var scanner in scanners)
        {
            environments |= await scanner.ScanAsync(context, cancellationToken);
        }

        return environments;
    }
}
