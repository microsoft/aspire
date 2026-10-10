// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.DotNet;
using Aspire.Shared;

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
        bool killOnParentExit = false,
        CancellationToken ct = default)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        var options = new ProcessInvocationOptions
        {
            SuppressLogging = true,
            StandardOutputCallback = line => outputBuilder.AppendLine(line),
            StandardErrorCallback = line => errorBuilder.AppendLine(line),
            KillOnParentExit = killOnParentExit,
            Lifetime = ChildProcessLifetime.OwnedTree,
        };

        var args = arguments.ToArray();
        var workDir = new DirectoryInfo(workingDirectory ?? Directory.GetCurrentDirectory());

        // Parent-death protection belongs to the shared guardian, not the helper's
        // event loop. In particular, a stopped or blocked helper cannot run a watchdog.
        var effectiveEnvironment = CopyEnvironment(environmentVariables);

        await using var execution = executionFactory.CreateExecution(toolPath, args, effectiveEnvironment, workDir, options);

        if (!await execution.StartAsync(ct).ConfigureAwait(false))
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
        ProcessInvocationOptions? options = null,
        bool killOnParentExit = false)
    {
        var args = arguments.ToArray();
        var workDir = new DirectoryInfo(workingDirectory ?? Directory.GetCurrentDirectory());

        // Clone so the KillOnParentExit flip below never mutates the caller's options instance — the
        // caller may reuse it across invocations. Falls back to a fresh instance when none was passed.
        var effectiveOptions = options?.Clone() ?? new ProcessInvocationOptions();

        if (killOnParentExit)
        {
            effectiveOptions.KillOnParentExit = true;
        }

        if (!effectiveOptions.Detached && effectiveOptions.Lifetime != ChildProcessLifetime.AppHost)
        {
            effectiveOptions.Lifetime = ChildProcessLifetime.OwnedTree;
        }
        var effectiveEnvironment = CopyEnvironment(environmentVariables);

        var execution = executionFactory.CreateExecution(toolPath, args, effectiveEnvironment, workDir, effectiveOptions);

        // StartAsync returns a background execution handle. Its caller owns the lifetime and must
        // explicitly wait, kill, or dispose it; cancellation here would only abort launch setup,
        // not define how the background process should be stopped after it starts.
        if (!await execution.StartAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await execution.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to start process: {toolPath}");
        }

        return execution;
    }

    private static IDictionary<string, string> CopyEnvironment(IDictionary<string, string>? environmentVariables)
        => environmentVariables is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(environmentVariables, StringComparer.Ordinal);
}
