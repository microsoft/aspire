// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Projects;

/// <summary>
/// Strategy for launching a guest language process.
/// </summary>
internal interface IGuestProcessLauncher
{
    /// <summary>
    /// Launches the guest process with the given command, arguments, and environment.
    /// </summary>
    Task<(int ExitCode, OutputCollector? Output)> LaunchAsync(
        string command,
        string[] args,
        DirectoryInfo workingDirectory,
        IDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken);
}
