// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Registers the Aspire agent telemetry <c>PostToolUse</c> hooks into the user-level configuration of
/// each detected agent environment, mirroring the behavior of the azure-skills plugin hooks. Whether
/// telemetry is actually transmitted remains gated by the telemetry opt-out environment variables;
/// this only wires the hooks up.
/// </summary>
internal interface ITelemetryHookConfigurator
{
    /// <summary>
    /// Materializes the hook scripts and registers them for each supported, detected agent environment.
    /// </summary>
    /// <param name="detectedEnvironments">The detected agent environments.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A summary of which environments were configured and which were skipped (and why).</returns>
    Task<TelemetryHookConfigurationResult> ConfigureAsync(
        AgentEnvironment detectedEnvironments,
        CancellationToken cancellationToken);
}

/// <summary>
/// The reason a telemetry hook could not be registered for an environment.
/// </summary>
internal enum TelemetryHookSkipReason
{
    /// <summary>The environment's existing configuration file contained malformed JSON.</summary>
    MalformedConfig,

    /// <summary>The environment's existing configuration had a hooks shape Aspire does not recognize.</summary>
    UnexpectedConfigShape,

    /// <summary>Writing the environment's configuration failed.</summary>
    WriteFailed,
}

/// <summary>
/// Describes an environment whose telemetry hook registration was skipped.
/// </summary>
/// <param name="Environment">The environment that was skipped.</param>
/// <param name="Reason">Why registration was skipped.</param>
internal sealed record TelemetryHookSkip(AgentEnvironment Environment, TelemetryHookSkipReason Reason);

/// <summary>
/// The outcome of <see cref="ITelemetryHookConfigurator.ConfigureAsync"/>.
/// </summary>
/// <param name="ConfiguredEnvironments">Environments whose telemetry hook was registered or refreshed.</param>
/// <param name="Skipped">Environments whose registration was skipped, with the reason.</param>
internal sealed record TelemetryHookConfigurationResult(
    AgentEnvironment ConfiguredEnvironments,
    IReadOnlyList<TelemetryHookSkip> Skipped);
