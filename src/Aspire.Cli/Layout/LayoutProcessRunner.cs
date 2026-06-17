// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.DotNet;

namespace Aspire.Cli.Layout;

/// <summary>
/// Runs processes using layout tools via an <see cref="IProcessExecutionFactory"/>.
/// </summary>
internal sealed class LayoutProcessRunner(IProcessExecutionFactory executionFactory)
{
    /// <inheritdoc />
    public async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string toolPath,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        var options = new ProcessInvocationOptions
        {
            SuppressLogging = true,
            StandardOutputCallback = line => outputBuilder.AppendLine(line),
            StandardErrorCallback = line => errorBuilder.AppendLine(line),
        };

        var args = arguments.ToArray();
        var workDir = new DirectoryInfo(workingDirectory ?? Directory.GetCurrentDirectory());

        using var execution = executionFactory.CreateExecution(toolPath, args, environmentVariables, workDir, options);

        if (!execution.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {toolPath}");
        }

        var exitCode = await execution.WaitForExitAsync(ct).ConfigureAwait(false);

        return (exitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    /// <inheritdoc />
    public IProcessExecution Start(
        string toolPath,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        ProcessInvocationOptions? options = null)
    {
        var args = arguments.ToArray();
        var workDir = new DirectoryInfo(workingDirectory ?? Directory.GetCurrentDirectory());

        var execution = executionFactory.CreateExecution(toolPath, args, environmentVariables, workDir, options ?? new ProcessInvocationOptions());

        if (!execution.Start())
        {
            execution.Dispose();
            throw new InvalidOperationException($"Failed to start process: {toolPath}");
        }

        return execution;
    }
}
