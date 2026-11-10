// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Hooks;

internal sealed class TelemetryHookConfigurator(ITelemetryHookInstaller installer) : ITelemetryHookConfigurator
{
    public async Task<TelemetryHookConfigurationResult> ConfigureAsync(
        IReadOnlyList<IAgentClient> selectedClients,
        AgentEnvironment detectedEnvironments,
        CancellationToken cancellationToken)
    {
        var eligibleClients = selectedClients
            .Where(client => (client.TelemetryHookEnvironments & detectedEnvironments) != AgentEnvironment.None)
            .ToList();
        if (eligibleClients.Count == 0)
        {
            return TelemetryHookConfigurationResult.None;
        }

        // Keep compatibility scripts current for existing registrations and older clients.
        await installer.EnsureInstalledAsync(cancellationToken);

        var configured = new List<IAgentClient>();
        var skipped = new List<TelemetryHookSkip>();
        foreach (var client in eligibleClients)
        {
            var result = await client.ConfigureTelemetryHookAsync(cancellationToken);
            configured.AddRange(result.ConfiguredClients);
            skipped.AddRange(result.Skipped);
        }

        return new TelemetryHookConfigurationResult(configured, skipped);
    }
}
