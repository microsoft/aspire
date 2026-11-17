// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Hooks;

internal sealed class TelemetryHookConfigurator(ITelemetryHookInstaller installer) : ITelemetryHookConfigurator
{
    public async Task<IReadOnlyList<TelemetryHookResult>> ConfigureAsync(
        IReadOnlyList<IAgentClient> clients,
        CancellationToken cancellationToken)
    {
        var eligibleClients = clients.Where(client => client.SupportsTelemetryHooks).ToList();
        if (eligibleClients.Count == 0)
        {
            return [];
        }

        // Keep compatibility scripts current for existing registrations and older clients.
        await installer.EnsureInstalledAsync(cancellationToken);

        var results = new List<TelemetryHookResult>(eligibleClients.Count);
        foreach (var client in eligibleClients)
        {
            results.Add(new TelemetryHookResult(client, await client.ConfigureTelemetryHookAsync(cancellationToken)));
        }

        return results;
    }
}
