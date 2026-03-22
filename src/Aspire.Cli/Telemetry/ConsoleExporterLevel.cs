// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Specifies the level of console exporter output for CLI telemetry.
/// </summary>
internal enum ConsoleExporterLevel
{
    /// <summary>
    /// Export only reported telemetry that is sent to external systems.
    /// </summary>
    Reported,

    /// <summary>
    /// Export diagnostic telemetry used for internal diagnostics.
    /// </summary>
    Diagnostic
}
