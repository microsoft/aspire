// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    /// Contributes user-level hook edits independently of native client selection and configuration outcomes.
    /// </summary>
    IEnumerable<AgentConfigurationTarget> Plan(AgentInitRequest request);
}
