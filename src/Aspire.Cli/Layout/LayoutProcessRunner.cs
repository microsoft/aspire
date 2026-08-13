// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Cli.DotNet;
using Aspire.Cli.Processes;

namespace Aspire.Cli.Layout;

/// <summary>
/// Runs processes using layout tools via an <see cref="IProcessExecutionFactory"/>.
/// </summary>
internal sealed class LayoutProcessRunner(IProcessExecutionFactory executionFactory)
{
    // Layout helpers include network-bound NuGet operations. If one wedges, the helper needs its own
    // command-level bound instead of relying on an outer test hang dump or a hard-killed parent CLI.
    internal static TimeSpan DefaultRunTimeout { get; } = TimeSpan.FromMinutes(3);

    /// <inheritdoc />
    public async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string toolPath,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        bool killOnParentExit = false,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var effectiveTimeout = timeout ?? DefaultRunTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The process timeout must be greater than zero.");
        }

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

        if (!await execution.StartAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to start process: {toolPath}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);
        int exitCode;
        try
        {
            exitCode = await execution.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(BuildTimeoutMessage(toolPath, args, workDir, effectiveTimeout));
        }

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

    private static string BuildTimeoutMessage(string toolPath, IReadOnlyList<string> arguments, DirectoryInfo workingDirectory, TimeSpan timeout)
    {
        var timeoutText = FormatTimeout(timeout);
        if (arguments is ["nuget", "search", ..])
        {
            var query = TryGetArgumentValue(arguments, "--query");
            var packageDescription = string.IsNullOrEmpty(query)
                ? "NuGet package search"
                : $"NuGet package search for '{query}'";
            return $"{packageDescription} timed out after {timeoutText} contacting configured NuGet sources from '{workingDirectory.FullName}'.";
        }

        if (arguments is ["nuget", "restore", ..])
        {
            return $"NuGet package restore timed out after {timeoutText} contacting configured NuGet sources from '{workingDirectory.FullName}'.";
        }

        return $"Process '{Path.GetFileName(toolPath)}' timed out after {timeoutText} in '{workingDirectory.FullName}'.";
    }

    private static string? TryGetArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (string.Equals(arguments[i], name, StringComparison.Ordinal))
            {
                return arguments[i + 1];
            }
        }

        return null;
    }

    private static string FormatTimeout(TimeSpan timeout)
    {
        if (timeout.TotalHours >= 1 && timeout.TotalMinutes % 60 == 0)
        {
            return FormatTimeoutUnit(timeout.TotalHours, "0", "hour", "hours");
        }

        if (timeout.TotalMinutes >= 1 && timeout.TotalSeconds % 60 == 0)
        {
            return FormatTimeoutUnit(timeout.TotalMinutes, "0", "minute", "minutes");
        }

        if (timeout.TotalSeconds >= 1)
        {
            return FormatTimeoutUnit(timeout.TotalSeconds, "0.###", "second", "seconds");
        }

        return FormatTimeoutUnit(timeout.TotalMilliseconds, "0.###", "millisecond", "milliseconds");
    }

    private static string FormatTimeoutUnit(double value, string format, string singularUnit, string pluralUnit)
    {
        var formattedValue = value.ToString(format, CultureInfo.InvariantCulture);
        var unit = string.Equals(formattedValue, "1", StringComparison.Ordinal) ? singularUnit : pluralUnit;

        return $"{formattedValue} {unit}";
    }
}
