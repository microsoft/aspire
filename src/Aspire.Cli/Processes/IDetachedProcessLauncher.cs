// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Processes;

/// <summary>
/// Starts detached child processes for commands that need to outlive the current CLI process.
/// </summary>
internal interface IDetachedProcessLauncher
{
    /// <summary>
    /// Starts a detached child process.
    /// </summary>
    IDetachedProcess Start(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? shouldRemoveEnvironmentVariable = null,
        IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null);
}

/// <summary>
/// Represents a detached child process.
/// </summary>
internal interface IDetachedProcess
{
    /// <summary>
    /// Gets the process ID.
    /// </summary>
    int Id { get; }

    /// <summary>
    /// Gets a value indicating whether the process has exited.
    /// </summary>
    bool HasExited { get; }

    /// <summary>
    /// Gets the process exit code.
    /// </summary>
    int ExitCode { get; }

    /// <summary>
    /// Asynchronously waits for the process to exit.
    /// </summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Kills the process.
    /// </summary>
    /// <param name="entireProcessTree">When <c>true</c>, kills the entire process tree; otherwise kills only the root process.</param>
    void Kill(bool entireProcessTree);
}

/// <summary>
/// Default implementation of <see cref="IDetachedProcessLauncher"/>.
/// </summary>
internal sealed class DefaultDetachedProcessLauncher : IDetachedProcessLauncher
{
    public IDetachedProcess Start(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? shouldRemoveEnvironmentVariable = null,
        IReadOnlyDictionary<string, string>? additionalEnvironmentVariables = null)
    {
        var process = DetachedProcessLauncher.Start(fileName, arguments, workingDirectory, shouldRemoveEnvironmentVariable, additionalEnvironmentVariables);

        return new DetachedProcess(process);
    }

    private sealed class DetachedProcess(Process process) : IDetachedProcess
    {
        public int Id => process.Id;

        public bool HasExited => process.HasExited;

        public int ExitCode => process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
    }
}
