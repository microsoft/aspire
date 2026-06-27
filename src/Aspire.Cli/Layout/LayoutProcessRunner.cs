// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Cli.DotNet;
using Aspire.Hosting;

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

    private static IDictionary<string, string> WithOrphanDetectionEnvironment(IDictionary<string, string>? environmentVariables)
    {
        // Copy so the caller's dictionary is never mutated; tolerate a null input.
        var environment = environmentVariables is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(environmentVariables, StringComparer.Ordinal);

        if (!environment.ContainsKey(KnownConfigNames.CliProcessId))
        {
            environment[KnownConfigNames.CliProcessId] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        }

        if (!environment.ContainsKey(KnownConfigNames.CliProcessStarted))
        {
            environment[KnownConfigNames.CliProcessStarted] = ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixSeconds().ToString(CultureInfo.InvariantCulture);
        }

        return environment;
    }
}
