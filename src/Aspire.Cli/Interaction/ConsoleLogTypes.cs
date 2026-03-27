// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Interaction;

/// <summary>
/// Known semantic types for console log messages emitted directly by the CLI.
/// </summary>
internal static class ConsoleLogTypes
{
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string ExitCode = "exitCode";
    public const string FailedToStart = "failedToStart";
}
