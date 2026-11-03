// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Hooks;

internal sealed class TelemetryHookConfigurator(
    ITelemetryHookInstaller installer,
    IEnumerable<IAgentClient> clients) : ITelemetryHookConfigurator
{
    public async Task<TelemetryHookConfigurationResult> ConfigureAsync(
        AgentEnvironment detectedEnvironments, CancellationToken cancellationToken)
    {
        // VS Code and OpenCode do not yet have verified telemetry hook schemas.
        var supportedEnvironments = detectedEnvironments &
            (AgentEnvironment.CopilotApp | AgentEnvironment.CopilotCli | AgentEnvironment.ClaudeCode);
        if (supportedEnvironments == AgentEnvironment.None)
        {
            return new TelemetryHookConfigurationResult(AgentEnvironment.None, []);
        }

        // Keep compatibility scripts current for existing registrations and older clients.
        await installer.EnsureInstalledAsync(cancellationToken);

        var configured = AgentEnvironment.None;
        var skipped = new List<TelemetryHookSkip>();
        foreach (var client in clients.Where(client => (client.Environments & supportedEnvironments) != AgentEnvironment.None))
        {
            var result = await client.ConfigureTelemetryHooksAsync(supportedEnvironments, cancellationToken);
            configured |= result.ConfiguredEnvironments;
            skipped.AddRange(result.Skipped);
        }

        return new TelemetryHookConfigurationResult(configured, skipped);
    }
}
