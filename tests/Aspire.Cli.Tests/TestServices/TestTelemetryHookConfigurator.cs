// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestTelemetryHookConfigurator : ITelemetryHookConfigurator
{
    public Exception? Exception { get; init; }

    /// <summary>
    /// Builds the results returned for the selected clients. Returns no results when unset.
    /// </summary>
    public Func<IReadOnlyList<IAgentClient>, IReadOnlyList<TelemetryHookResult>>? ResultFactory { get; init; }

    /// <summary>
    /// The ids of the clients passed to the most recent <see cref="ConfigureAsync"/> call.
    /// </summary>
    public IReadOnlyList<string> SelectedClientIds { get; private set; } = [];

    public AgentConfigurationScope? Scope { get; private set; }

    public DirectoryInfo? WorkspaceRoot { get; private set; }

    public Task<IReadOnlyList<TelemetryHookResult>> ConfigureAsync(
        IReadOnlyList<IAgentClient> selectedClients,
        AgentConfigurationScope scope,
        DirectoryInfo workspaceRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Exception is { } exception)
        {
            throw exception;
        }

        SelectedClientIds = [.. selectedClients.Select(static client => client.Id)];
        Scope = scope;
        WorkspaceRoot = workspaceRoot;
        return Task.FromResult(ResultFactory?.Invoke(selectedClients) ?? []);
    }
}
