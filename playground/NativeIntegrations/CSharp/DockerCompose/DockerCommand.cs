// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NativeIntegrations.DockerCompose;

public sealed record DockerCommandOptions(
    string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout, CancellationToken CancellationToken);
public sealed record DockerCommandResult(int ExitCode, string Stdout, string Stderr);
public delegate Task<DockerCommandResult> DockerCommandRunner(string executable, IReadOnlyList<string> arguments, DockerCommandOptions options);

public static class DockerCommand
{
    public static async Task<DockerCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, DockerCommandOptions options)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                WorkingDirectory = options.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.Environment.Clear();
        foreach (var (key, value) in options.Environment)
        {
            process.StartInfo.Environment.Add(key, value);
        }
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Could not start Docker. Install Docker CLI and ensure it is on PATH.");
        }
        process.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
        deadline.CancelAfter(options.Timeout);
        var length = 0;
        // Drain both pipes concurrently: waiting for either alone can deadlock a child
        // that fills the other pipe. The combined byte budget also bounds memory usage.
        var stdout = ReadAsync(process.StandardOutput.BaseStream);
        var stderr = ReadAsync(process.StandardError.BaseStream);
        Task output = Task.WhenAll(stdout, stderr);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            // Reads observe the deadline too: an exited child can leave inherited
            // pipes open in a descendant, so process exit alone cannot bound draining.
            await output.ConfigureAwait(false);
            if (length > 1024 * 1024)
            {
                throw new InvalidOperationException("Docker command exceeded the bounded output limit.");
            }

            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await output.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            options.CancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Docker command was cancelled, timed out, or exceeded the bounded output limit.");
        }

        async Task<string> ReadAsync(Stream stream)
        {
            using var captured = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (Interlocked.Add(ref length, count) > 1024 * 1024)
                {
                    await deadline.CancelAsync().ConfigureAwait(false);
                }
                else
                {
                    captured.Write(buffer, 0, count);
                }
            }

            return Encoding.UTF8.GetString(captured.GetBuffer(), 0, checked((int)captured.Length));
        }
    }
}
