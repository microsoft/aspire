// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestTelemetryHookConfigurator : ITelemetryHookConfigurator
{
    public Exception? Exception { get; init; }

    /// <summary>
    /// Builds the result returned for the selected clients. Returns no configured or skipped clients when unset.
    /// </summary>
    public Func<IReadOnlyList<IAgentClient>, TelemetryHookConfigurationResult>? ResultFactory { get; init; }

    public IReadOnlyList<IAgentClient> SelectedClients { get; private set; } = [];

    /// <summary>
    /// The detected environments that overlap the selected clients' environments.
    /// </summary>
    public AgentEnvironment ConfiguredEnvironments { get; private set; }

    public Task<TelemetryHookConfigurationResult> ConfigureAsync(
        IReadOnlyList<IAgentClient> selectedClients,
        AgentEnvironment detectedEnvironments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Exception is { } exception)
        {
            throw exception;
        }

        SelectedClients = selectedClients;
        ConfiguredEnvironments = detectedEnvironments & selectedClients.Aggregate(
            AgentEnvironment.None, static (environments, client) => environments | client.Environments);
        return Task.FromResult(ResultFactory?.Invoke(selectedClients) ?? TelemetryHookConfigurationResult.None);
    }
}