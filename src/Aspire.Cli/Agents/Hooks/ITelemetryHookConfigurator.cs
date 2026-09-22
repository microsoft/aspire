// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Plans Aspire agent telemetry <c>PostToolUse</c> hooks in the user-level configuration of
/// each detected, supported agent client. Whether
/// telemetry is actually transmitted remains gated by the telemetry opt-out environment variables;
/// this only wires the hooks up.
/// </summary>
internal interface ITelemetryHookConfigurator
{
    /// <summary>
    /// Contributes deferred user-level hook edits so the shared writer can combine hooks
    /// and plugin settings in the same file. Targeting depends on detection, not selection.
    /// </summary>
    /// <param name="request">The asset selections and detected environments for this setup operation.</param>
    /// <returns>Deferred edits whose outcomes are reported by the writer as <see cref="AgentTargetResult"/> values.</returns>
    IEnumerable<AgentConfigurationTarget> Plan(AgentInitRequest request);
}

/// <summary>
/// Environment-owned hook paths and schema edits, applied by the shared hook writer.
/// </summary>
internal sealed record AgentHookConfiguration(
    string Path,
    IEnumerable<string> PolicyPaths,
    IEnumerable<string> ExistingHookPaths,
    Action<JsonObject> Validate,
    Action<JsonObject, TelemetryHookScripts, Func<JsonNode?, bool>> Apply);
