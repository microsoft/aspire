// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Registers the Aspire agent telemetry <c>PostToolUse</c> hooks into the user-level configuration of
/// each detected agent client, mirroring the behavior of the azure-skills plugin hooks. Whether
/// telemetry is actually transmitted remains gated by the telemetry opt-out environment variables;
/// this only wires the hooks up.
/// </summary>
internal interface ITelemetryHookConfigurator
{
    /// <summary>
    /// Materializes the hook scripts and registers them for each supported, detected agent client.
    /// </summary>
    /// <param name="detectedClients">The agent clients detected during the environment scan.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A summary of which clients were configured and which were skipped (and why).</returns>
    Task<TelemetryHookConfigurationResult> ConfigureAsync(
        IReadOnlyCollection<AgentClientKind> detectedClients,
        CancellationToken cancellationToken);
}

/// <summary>
/// The reason a telemetry hook could not be registered for a client.
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
/// Describes a client whose telemetry hook registration was skipped.
/// </summary>
/// <param name="Client">The client that was skipped.</param>
/// <param name="Reason">Why registration was skipped.</param>
internal sealed record TelemetryHookSkip(AgentClientKind Client, TelemetryHookSkipReason Reason);

/// <summary>
/// The outcome of <see cref="ITelemetryHookConfigurator.ConfigureAsync"/>.
/// </summary>
/// <param name="ConfiguredClients">Clients whose telemetry hook was registered or refreshed.</param>
/// <param name="Skipped">Clients whose registration was skipped, with the reason.</param>
internal sealed record TelemetryHookConfigurationResult(
    IReadOnlyList<AgentClientKind> ConfiguredClients,
    IReadOnlyList<TelemetryHookSkip> Skipped);
