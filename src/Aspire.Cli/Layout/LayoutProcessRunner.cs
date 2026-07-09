// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.DotNet;
using Aspire.Cli.Processes;

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
        };

        var args = arguments.ToArray();
        var workDir = new DirectoryInfo(workingDirectory ?? Directory.GetCurrentDirectory());

        // The Windows kill-on-close job (KillOnParentExit, above) and the cross-platform cooperative
        // parent-liveness watchdog (activated by the ASPIRE_CLI_PID identity that
        // WithOrphanDetectionEnvironment stamps) are two implementations of the SAME "don't outlive the
        // CLI" policy. Arming BOTH on one child races the job's kernel TerminateProcess against the
        // watchdog's Environment.Exit(124) when the CLI exits, which can get the child stuck mid-teardown.
        // So we use exactly one mechanism per child: on Windows the kill-on-close
        // job is authoritative (kernel-enforced), and we do not use the watchdog.
        // Everywhere else KillOnParentExit is a no-op, and the cooperative watchdog remains the sole mechanism 
        // and MUST have relevant environment variables set.
        var effectiveEnvironment = options.KillOnParentExit && OperatingSystem.IsWindows()
            ? CopyEnvironment(environmentVariables)
            : WithOrphanDetectionEnvironment(environmentVariables);

        await using var execution = executionFactory.CreateExecution(toolPath, args, effectiveEnvironment, workDir, options);

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

        // Compare with RunAsync: the same logic applies here.
        var effectiveEnvironment = effectiveOptions.KillOnParentExit && OperatingSystem.IsWindows()
            ? CopyEnvironment(environmentVariables)
            : WithOrphanDetectionEnvironment(environmentVariables);

        var execution = executionFactory.CreateExecution(toolPath, args, effectiveEnvironment, workDir, effectiveOptions);

        if (!execution.Start())
        {
            await execution.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to start process: {toolPath}");
        }

        return execution;
    }

    private static IDictionary<string, string> WithOrphanDetectionEnvironment(IDictionary<string, string>? environmentVariables)
    {
        var environment = CopyEnvironment(environmentVariables);

        // Stamp the launching CLI's identity, but never override values the caller already supplied
        // so an explicit caller override always wins.
        OrphanDetectionEnvironment.ApplyCurrentProcess(environment, overwrite: false);

        return environment;
    }

    private static IDictionary<string, string> CopyEnvironment(IDictionary<string, string>? environmentVariables)
        => environmentVariables is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(environmentVariables, StringComparer.Ordinal);
}
