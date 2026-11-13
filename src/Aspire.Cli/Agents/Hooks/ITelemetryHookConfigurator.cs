// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Registers the Aspire agent telemetry <c>PostToolUse</c> hook once into the user-level configuration of
/// each given agent client that supports telemetry hooks, mirroring the behavior of the azure-skills
/// plugin hooks. Whether telemetry is actually transmitted remains gated by the telemetry opt-out
/// environment variables; this only wires the hooks up.
/// </summary>
internal interface ITelemetryHookConfigurator
{
    /// <summary>
    /// Materializes the hook scripts and registers one hook for each agent client that supports
    /// telemetry hooks.
    /// </summary>
    /// <param name="clients">The agent clients to register hooks for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>One result per client that supports telemetry hooks, in input order.</returns>
    Task<IReadOnlyList<TelemetryHookResult>> ConfigureAsync(
        IReadOnlyList<IAgentClient> clients,
        CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of registering a telemetry hook for an agent client.
/// </summary>
internal enum TelemetryHookStatus
{
    /// <summary>The hook was registered or refreshed.</summary>
    Configured,

    /// <summary>The client's existing configuration file contained malformed JSON, so it was left unchanged.</summary>
    MalformedConfig,

    /// <summary>The client's existing configuration had a hooks shape Aspire does not recognize, so it was left unchanged.</summary>
    UnexpectedConfigShape,

    /// <summary>Reading or writing the client's configuration failed.</summary>
    WriteFailed,
}

/// <summary>
/// The telemetry hook registration outcome for one agent client.
/// </summary>
/// <param name="Client">The agent client.</param>
/// <param name="Status">The registration outcome.</param>
internal sealed record TelemetryHookResult(IAgentClient Client, TelemetryHookStatus Status);
