// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Registers the Aspire agent telemetry <c>PostToolUse</c> hook once into the user-level configuration of
/// each eligible agent client, mirroring the behavior of the azure-skills plugin hooks. Whether
/// telemetry is actually transmitted remains gated by the telemetry opt-out environment variables;
/// this only wires the hooks up.
/// </summary>
internal interface ITelemetryHookConfigurator
{
    /// <summary>
    /// Materializes the hook scripts and registers one hook for each agent client made eligible by a
    /// detected environment.
    /// </summary>
    /// <param name="detectedEnvironments">The detected agent environments used to decide client eligibility.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A summary of which agent clients were configured and which were skipped (and why).</returns>
    Task<TelemetryHookConfigurationResult> ConfigureAsync(
        AgentEnvironment detectedEnvironments,
        CancellationToken cancellationToken);
}

/// <summary>
/// The reason a telemetry hook could not be registered for an agent client.
/// </summary>
internal enum TelemetryHookSkipReason
{
    /// <summary>The client's existing configuration file contained malformed JSON.</summary>
    MalformedConfig,

    /// <summary>The client's existing configuration had a hooks shape Aspire does not recognize.</summary>
    UnexpectedConfigShape,

    /// <summary>Writing the client's configuration failed.</summary>
    WriteFailed,
}

/// <summary>
/// Describes an agent client whose telemetry hook registration was skipped.
/// </summary>
/// <param name="Client">The agent client that was skipped.</param>
/// <param name="Reason">Why registration was skipped.</param>
internal sealed record TelemetryHookSkip(IAgentClient Client, TelemetryHookSkipReason Reason);

/// <summary>
/// The outcome of <see cref="ITelemetryHookConfigurator.ConfigureAsync"/>.
/// </summary>
/// <param name="ConfiguredClients">Agent clients whose telemetry hook was registered or refreshed.</param>
/// <param name="Skipped">Agent clients whose registration was skipped, with the reason.</param>
internal sealed record TelemetryHookConfigurationResult(
    IReadOnlyList<IAgentClient> ConfiguredClients,
    IReadOnlyList<TelemetryHookSkip> Skipped)
{
    public static TelemetryHookConfigurationResult None { get; } = new([], []);
}
