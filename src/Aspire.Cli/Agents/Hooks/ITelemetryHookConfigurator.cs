// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Configuration;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Plans Aspire agent telemetry <c>PostToolUse</c> hooks in the user-level configuration of
/// each selected, supported agent client. Whether
/// telemetry is actually transmitted remains gated by the telemetry opt-out environment variables;
/// this only wires the hooks up.
/// </summary>
internal interface ITelemetryHookConfigurator
{
    /// <summary>
    /// Contributes user-level hook edits to the shared writer. The edits run only after
    /// relevant native Aspire configuration succeeds, including pending edits in the same file.
    /// </summary>
    IEnumerable<AgentConfigurationTarget> Plan(AgentInitRequest request);
}
