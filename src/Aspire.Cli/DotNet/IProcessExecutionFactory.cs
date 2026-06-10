// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.DotNet;

/// <summary>
/// Creates configured process executions.
/// </summary>
internal interface IProcessExecutionFactory
{
    /// <summary>
    /// Creates a configured process execution ready to be started.
    /// </summary>
    /// <param name="fileName">The executable path to run.</param>
    /// <param name="args">The command-line arguments to pass to the process.</param>
    /// <param name="env">Optional environment variables to set for the process.</param>
    /// <param name="workingDirectory">The working directory for the process.</param>
    /// <param name="options">Invocation options for the command.</param>
    /// <returns>A configured <see cref="IProcessExecution"/> ready to be started.</returns>
    IProcessExecution CreateExecution(
        string fileName,
        string[] args,
        IDictionary<string, string>? env,
        DirectoryInfo workingDirectory,
        ProcessInvocationOptions options);
}
