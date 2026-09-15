// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Tray;

/// <summary>
/// Runs only the stable CLI's tray-start command, without a console or a native UI.
/// </summary>
internal static class WindowsLoginBootstrap
{
    internal static ProcessStartInfo CreateStartInfo(string cliPath)
    {
        if (!Path.IsPathFullyQualified(cliPath) || !File.Exists(cliPath))
        {
            throw new InvalidOperationException("The installed CLI for sign-in startup is missing. Update the startup setting from the installed Aspire CLI.");
        }
        if (!TrayStartupEntry.IsNativeExecutable(cliPath, windows: true, requireGui: false))
        {
            throw new InvalidOperationException("Sign-in startup requires a native Windows Aspire CLI executable.");
        }
        return CliProcess.CreateStartInfo(cliPath, "tray", "start", "--non-interactive", "--nologo");
    }

    internal static async Task RunAsync(string cliPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var process = Process.Start(CreateStartInfo(cliPath))
            ?? throw new InvalidOperationException("Windows could not start the installed Aspire CLI at sign-in.");
        process.StandardInput.Close();
        var stdout = CliProcess.DrainAsync(process.StandardOutput, timeout.Token);
        var stderr = CliProcess.DrainAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The sign-in tray start failed (CLI exit {process.ExitCode}). Start Aspire manually to inspect the installation.");
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Terminate only our CLI invocation, never its detached GUI or any AppHost.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
            using var termination = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(termination.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (termination.IsCancellationRequested)
            {
                throw new TimeoutException("The sign-in CLI invocation could not be stopped after its startup timeout.");
            }
            throw new TimeoutException("The sign-in tray start did not finish within 60 seconds.");
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
        }
    }
}
