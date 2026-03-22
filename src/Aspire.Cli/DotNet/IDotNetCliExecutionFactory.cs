// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.DotNet;

/// <summary>
/// Creates configured dotnet CLI executions.
/// </summary>
internal interface IDotNetCliExecutionFactory
{
    /// <summary>
    /// Creates a configured dotnet CLI execution ready to be started.
    /// </summary>
    /// <param name="args">The command-line arguments to pass to dotnet.</param>
    /// <param name="env">Optional environment variables to set for the process. If backchannel communication
    /// is needed, the socket path should be set via <c>ASPIRE__BACKCHANNEL__UNIXSOCKETPATH</c>.</param>
    /// <param name="workingDirectory">The working directory for the process.</param>
    /// <param name="options">Invocation options for the command.</param>
    /// <returns>A configured <see cref="IDotNetCliExecution"/> ready to be started.</returns>
    IDotNetCliExecution CreateExecution(
        string[] args,
        IDictionary<string, string>? env,
        DirectoryInfo workingDirectory,
        DotNetCliRunnerInvocationOptions options);
}
