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
            // Bind aspire-managed.exe / DCP children to the CLI's kill-on-close job so that an
            // abnormal CLI exit (e.g. TerminateProcess from the VS Code extension via Node's
            // proc.kill(), a crash, or power loss) reliably terminates the helper. Without this
            // the helper's only exit triggers are graceful CLI shutdown (cancellation token) and
            // its own next stdout/stderr write hitting a broken pipe — neither of which fires
            // while the helper is blocked on a slow NuGet HTTP request, leading to the orphan
            // accumulation reported in https://github.com/microsoft/aspire/issues/18490.
            BindChildToCliJob = true,
            StandardOutputCallback = line => outputBuilder.AppendLine(line),
            StandardErrorCallback = line => errorBuilder.AppendLine(line),
        };

        var args = arguments.ToArray();
        var workDir = new DirectoryInfo(workingDirectory ?? Directory.GetCurrentDirectory());

        await using var execution = executionFactory.CreateExecution(toolPath, args, environmentVariables, workDir, options);

        if (!execution.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {toolPath}");
        }

        var exitCode = await execution.WaitForExitAsync(ct).ConfigureAwait(false);

        return (exitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    /// <inheritdoc />
    public async Task<IProcessExecution> StartAsync(
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
            await execution.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to start process: {toolPath}");
        }

        return execution;
    }
}
