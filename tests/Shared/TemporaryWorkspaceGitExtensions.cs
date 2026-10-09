// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace Aspire.Tests.Utils;

public static class TemporaryWorkspaceGitExtensions
{
    public static async Task InitializeGitAsync(this TemporaryWorkspace workspace, CancellationToken cancellationToken = default)
    {
        workspace.TestOutputHelper.WriteLine($"Initializing git repository at: {workspace.Path}");

        await RunGitAsync(workspace.Path, workspace.TestOutputHelper, ["init"], cancellationToken);
    }

    internal static async Task RunGitAsync(string workingDirectory, ITestOutputHelper outputHelper, string[] arguments, CancellationToken cancellationToken)
    {
        var command = $"git {string.Join(' ', arguments)}";
        var stopwatch = Stopwatch.StartNew();
        outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] Starting '{command}' in '{workingDirectory}'");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, TestContext.Current.CancellationToken, timeout.Token);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2 && Directory.Exists(workingDirectory))
        {
            outputHelper.WriteLine($"Failed to start git: {ex}");
            Assert.Skip("git is required for this test but was not found on PATH.");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' started with PID {process.Id} after {stopwatch.Elapsed}");

            // Drain both pipes while Git runs: waiting for exit first can deadlock if either
            // pipe fills. Log each line immediately so output survives a timeout.
            var stdoutTask = ReadOutputAsync(process.StandardOutput, stdout, "stdout");
            var stderrTask = ReadOutputAsync(process.StandardError, stderr, "stderr");

            await Task.WhenAll(process.WaitForExitAsync(cancellation.Token), stdoutTask, stderrTask);
        }
        catch (Exception ex)
        {
            cancellation.Cancel();

            // Disposing Process does not stop it. Request tree termination and reap the root
            // on any post-start failure, before logging or workspace disposal. HasExited can
            // change between the check and Kill.
            Exception? terminationRace = null;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception cleanupException) when ((cleanupException is InvalidOperationException or Win32Exception) && process.HasExited)
            {
                terminationRace = cleanupException;
            }

            // WaitForExitAsync observes only the root, not every descendant. Tree cleanup is
            // best-effort; strict containment would require process groups or Windows jobs.
            // https://learn.microsoft.com/dotnet/api/system.diagnostics.process.kill#remarks
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            if (terminationRace is not null)
            {
                outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) exited during termination: {terminationRace.Message}");
            }

            if (ex is OperationCanceledException)
            {
                outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) {(timeout.IsCancellationRequested ? "timed out" : "was canceled")} after {stopwatch.Elapsed}");
                if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"'{command}' in '{workingDirectory}' (PID {process.Id}) timed out after 30 seconds.");
                }
            }
            else
            {
                outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) failed after {stopwatch.Elapsed}: {ex}");
            }

            throw;
        }
        finally
        {
            outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) finished after {stopwatch.Elapsed}; exit code: {(process.HasExited ? process.ExitCode.ToString() : "still running")}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{command}' in '{workingDirectory}' failed with exit code {process.ExitCode}. stdout: {stdout}, stderr: {stderr}");
        }

        async Task ReadOutputAsync(StreamReader reader, StringBuilder capturedOutput, string streamName)
        {
            try
            {
                while (await reader.ReadLineAsync(cancellation.Token) is { } line)
                {
                    capturedOutput.AppendLine(line);
                    outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) {streamName}: {line}");
                }

                outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) {streamName} closed");
            }
            catch
            {
                // WhenAll waits for every task. Cancel the other reader and exit wait so a
                // reader fault reaches process cleanup immediately, not at the timeout.
                cancellation.Cancel();
                throw;
            }
        }
    }
}
