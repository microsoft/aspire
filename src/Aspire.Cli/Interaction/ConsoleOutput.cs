// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Interaction;

/// <summary>
/// Specifies which console output stream to use.
/// </summary>
internal enum ConsoleOutput
{
    /// <summary>
    /// Standard output (stdout).
    /// </summary>
    Standard,

    /// <summary>
    /// Standard error (stderr).
    /// </summary>
    Error
}
