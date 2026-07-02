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
            // Windows OS-level backstop layered on top of the cross-platform watchdog stamped below:
            // binds the child to the CLI's kill-on-close job so a hard-killed CLI cannot leak this
            // helper even if it is wedged in a native call. No-op on non-Windows hosts.
            KillOnParentExit = killOnParentExit,
        };

        var args = arguments.ToArray();
        var workDir = new DirectoryInfo(workingDirectory ?? Directory.GetCurrentDirectory());

        // Stamp the launching CLI's identity onto the child so layout tools (e.g. aspire-managed nuget)
        // can run a parent-liveness watchdog and self-terminate if the CLI dies,
        // preventing leaked aspire-managed processes. Does not override values the caller already set.
        var effectiveEnvironment = WithOrphanDetectionEnvironment(environmentVariables);

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

        // Stamp the launching CLI's identity onto the child (same as RunAsync) so long-lived layout
        // processes started here — notably aspire-managed dashboard for `aspire dashboard run` and the
        // profiling collector — can run a parent-liveness watchdog and self-terminate if the CLI is
        // hard-killed, preventing leaked aspire-managed processes. Does not override caller-set values.
        var effectiveEnvironment = WithOrphanDetectionEnvironment(environmentVariables);

        var effectiveOptions = options ?? new ProcessInvocationOptions();

        // Windows OS-level backstop that complements the cross-platform watchdog above: bind the child
        // to the CLI's kill-on-close job so the OS terminates it if the CLI dies, even when the child is
        // wedged and cannot react to the watchdog's cancellation. Only ever turned on here (never off),
        // so a caller that already opted in via its own options is preserved. No-op on non-Windows hosts.
        if (killOnParentExit)
        {
            effectiveOptions.KillOnParentExit = true;
        }

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
        // Copy so the caller's dictionary is never mutated; tolerate a null input.
        var environment = environmentVariables is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(environmentVariables, StringComparer.Ordinal);

        // Stamp the launching CLI's identity, but never override values the caller already supplied
        // so an explicit caller override always wins.
        OrphanDetectionEnvironment.ApplyCurrentProcess(environment, overwrite: false);

        return environment;
    }
}
