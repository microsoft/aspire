// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.DotNet;

/// <summary>
/// Represents a configured dotnet CLI execution that can be started and awaited.
/// </summary>
internal interface IDotNetCliExecution
{
    /// <summary>
    /// Gets the file name of the executable to run.
    /// </summary>
    string FileName { get; }

    /// <summary>
    /// Gets the command-line arguments.
    /// </summary>
    IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Gets the environment variables configured for the process.
    /// </summary>
    IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }

    /// <summary>
    /// Starts the execution.
    /// </summary>
    /// <returns><c>true</c> if the process was started successfully; otherwise, <c>false</c>.</returns>
    bool Start();

    /// <summary>
    /// Waits for the process to exit asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code of the process.</returns>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a value indicating whether the process has exited. Only valid after <see cref="Start"/> returns <c>true</c>.
    /// </summary>
    bool HasExited { get; }

    /// <summary>
    /// Gets the exit code of the process. Only valid after <see cref="HasExited"/> returns <c>true</c>.
    /// </summary>
    int ExitCode { get; }
}
