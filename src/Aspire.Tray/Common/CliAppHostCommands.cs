// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Shared;

namespace Aspire.Tray;

internal sealed class CliAppHostCommands(string cliPath, TimeSpan timeout)
{
    private readonly TimeSpan _timeout = timeout > TimeSpan.Zero ? timeout
        : throw new ArgumentOutOfRangeException(nameof(timeout), "The command timeout must be positive.");

    internal static ProcessStartInfo CreateStopStartInfo(string executable, AppHostId target)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target.AppHostPid);
        if (!Path.IsPathFullyQualified(target.AppHostPath) || target.AppHostPath.Contains('\0'))
        {
            throw new ArgumentException("An absolute AppHost path is required.", nameof(target));
        }
        if (target.ProcessStartTimeUnixMilliseconds is not > 0)
        {
            throw new ArgumentException("A verified process lifetime is required.", nameof(target));
        }

        // Never retry without --pid: project-only stop intentionally stops every matching instance.
        // The CLI revalidates both identifiers against its live connection before sending shutdown.
        return CliProcess.CreateStartInfo(executable, "stop", "--apphost", target.AppHostPath,
            "--pid", target.AppHostPid.ToString(CultureInfo.InvariantCulture),
            "--started-at", target.ProcessStartTimeUnixMilliseconds.Value.ToString(CultureInfo.InvariantCulture),
            "--format", "json", "--protocol-version", "1", "--non-interactive", "--nologo");
    }

    internal static ProcessStartInfo CreateStartStartInfo(string executable, string appHostPath)
    {
        var path = TrayAppHostPath.RequireExistingFile(appHostPath);
        var startInfo = CliProcess.CreateStartInfo(executable, "start", "--apphost", path, "--non-interactive", "--nologo");
        startInfo.WorkingDirectory = Path.GetDirectoryName(path)!;
        return startInfo;
    }

    public async Task<StartResult> StartAsync(string appHostPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo;
        try
        {
            startInfo = CreateStartStartInfo(cliPath, appHostPath);
        }
        catch (FileNotFoundException)
        {
            return new(StartOutcome.NotFound, null);
        }

        using var deadline = new CancellationTokenSource(_timeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Aspire CLI.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"The Aspire start command could not launch ({ex.GetType().Name}).");
            return new(StartOutcome.Failed, null);
        }
        using (process)
        {
            var output = Task.CompletedTask;
            try
            {
                process.StandardInput.Close();
                // Both streams are bounded-memory drains, not diagnostic text to display:
                // the ordinary start command can emit authenticated dashboard URLs.
                output = Task.WhenAll(CliProcess.DrainAsync(process.StandardOutput, lifetime.Token),
                    CliProcess.DrainAsync(process.StandardError, lifetime.Token));
                await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
                await output.ConfigureAwait(false);
                return new(process.ExitCode == 0 ? StartOutcome.Started : StartOutcome.Failed, process.ExitCode);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new(StartOutcome.TimedOut, null);
            }
            finally
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                // Never kill a process tree: start launches a detached AppHost that belongs
                // to the user even if the tray closes or stops waiting for CLI completion.
                await CliProcess.TerminateOwnedChildAsync(process).ConfigureAwait(false);
                try
                {
                    await output.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
            }
        }
    }

    public async Task<StopResult> StopAsync(AppHostId target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(_timeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var process = Process.Start(CreateStopStartInfo(cliPath, target))
            ?? throw new InvalidOperationException("Could not start the Aspire CLI.");
        var stderr = Task.CompletedTask;
        try
        {
            process.StandardInput.Close();
            stderr = CliProcess.DrainAsync(process.StandardError, lifetime.Token);
            TrayStopMessage? result = null;
            await foreach (var line in CliProtocol.ReadLinesAsync(process.StandardOutput, lifetime.Token).ConfigureAwait(false))
            {
                if (result is not null)
                {
                    throw new CliProtocolException();
                }
                result = CliProtocol.ReadStopMessage(line);
            }
            await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (result is null || result.ExitCode != process.ExitCode)
            {
                throw new CliProtocolException();
            }
            return new(CliProtocol.GetStopOutcome(result.Outcome), process.ExitCode);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new(StopOutcome.TimedOut, null);
        }
        catch (Exception ex) when (ex is CliProtocolException or JsonException)
        {
            return new(StopOutcome.Incompatible, process.HasExited ? process.ExitCode : null);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await CliProcess.TerminateOwnedChildAsync(process).ConfigureAwait(false);
            try
            {
                await stderr.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }
}
