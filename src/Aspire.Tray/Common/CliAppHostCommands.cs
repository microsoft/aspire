// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
