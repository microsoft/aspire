// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Detects whether the CLI is running under a known coding agent.
/// </summary>
internal interface ICodingAgentDetector
{
    /// <summary>
    /// Gets the detected coding agent name, or names, for the current environment.
    /// </summary>
    /// <returns>The detected coding agent names, or <see langword="null"/> when none are detected.</returns>
    string? GetCodingAgent();
}
