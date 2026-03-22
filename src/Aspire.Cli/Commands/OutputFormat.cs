// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Commands;

/// <summary>
/// Output format for CLI commands that support multiple output modes.
/// </summary>
internal enum OutputFormat
{
    /// <summary>
    /// Human-readable table or text output (default).
    /// </summary>
    Table,

    /// <summary>
    /// Machine-readable JSON output.
    /// </summary>
    Json
}
