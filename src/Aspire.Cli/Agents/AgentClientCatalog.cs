// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Agents.VsCode;

namespace Aspire.Cli.Agents;

/// <summary>
/// Collects client-owned definitions without probing installations or reading configuration.
/// </summary>
internal sealed class AgentClientCatalog
{
    public IReadOnlyList<AgentClientDescriptor> Clients { get; } = Array.AsReadOnly<AgentClientDescriptor>(
    [
        .. CopilotAgentConfiguration.GetClients(),
        VsCodeAgentConfiguration.Client,
        ClaudeCodeAgentConfiguration.Client,
        OpenCodeAgentConfiguration.Client
    ]);

    public AgentClientDescriptor Get(AgentClientKind client) => Clients.Single(descriptor => descriptor.Kind == client);

    public IEnumerable<AgentConfigurationTarget> GetTargets(
        AgentInitRequest request,
        CliExecutionContext executionContext,
        IEnvironment environment)
        => Clients.Where(client => request.Clients.Contains(client.Kind))
            // Copilot App/CLI share the same callback as well as physical configuration.
            .Select(client => client.GetTargets).Distinct()
            .SelectMany(getTargets => getTargets(request, executionContext, environment));
}

/// <summary>
/// A client's selection metadata and native configuration callback.
/// </summary>
internal sealed record AgentClientDescriptor(
    AgentClientKind Kind,
    string Id,
    string DisplayName,
    Func<AgentInitRequest, CliExecutionContext, IEnvironment, IEnumerable<AgentConfigurationTarget>> GetTargets)
{
    public override string ToString() => Id;
}
