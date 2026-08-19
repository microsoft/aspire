// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;

namespace Aspire.Hosting;

/// <summary>
/// Runs .NET CLI commands for Blazor projects.
/// </summary>
internal static class BlazorDotNetCliRunner
{
    public static async Task<BlazorDotNetCliResult> RunAsync(
        string projectPath,
        string command,
        IReadOnlyList<string> arguments,
        bool machineReadableOutput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnetHostPath
                ? dotnetHostPath
                : "dotnet",
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(projectPath);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (machineReadableOutput)
        {
            // MSBuild queries emit JSON on stdout. Disable unrelated CLI messages that could
            // corrupt the machine-readable output before callers have a chance to parse it.
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new(startInfo.FileName, false, -1, "", "", ex);
        }

        if (process is null)
        {
            return new(startInfo.FileName, false, -1, "", "", null);
        }

        using (process)
        {
            // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                return new(
                    startInfo.FileName,
                    true,
                    process.ExitCode,
                    await stdoutTask.ConfigureAwait(false),
                    await stderrTask.ConfigureAwait(false),
                    null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Canceling WaitForExitAsync only stops waiting. Terminate the complete process tree
                // so dotnet/MSBuild child processes cannot outlive AppHost shutdown and retain files.
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process can exit between HasExited and Kill.
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }
}

internal readonly record struct BlazorDotNetCliResult(
    string Command,
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    Exception? StartException);
