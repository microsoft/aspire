// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace Aspire.Hosting.Dcp.Process;

internal static partial class ProcessUtil
{
    // Captured text must preserve NUL delimiters and newlines, while legacy line execution
    // retains its callback and WriteLine behavior. These paths are separate temporarily;
    // https://github.com/microsoft/aspire/issues/21540 tracks consolidating lifetime/output handling.
    public static async Task<ProcessCaptureResult> RunAndCaptureTextAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        ReadOnlyMemory<byte> standardInput = default,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new ProcessStartInfo(executablePath)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                process.StartInfo.Environment[key] = value;
            }
        }
        process.Start();
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = process.StandardOutput.ReadToEndAsync(batch.Token);
        var error = process.StandardError.ReadToEndAsync(batch.Token);
        // Concurrent WaitForExitAsync calls can lose Process.Exited handlers; share one wait.
        var exit = process.WaitForExitAsync(CancellationToken.None);
        IOException? inputError = null;
        try
        {
            // Start all three streams before waiting: a child can fill either output pipe
            // before reading input, and input itself can exceed pipe capacity.
            await Task.WhenAll(GuardAsync(output), GuardAsync(error), GuardAsync(WriteAsync()),
                GuardAsync(exit.WaitAsync(batch.Token))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (inputError is not null && process.ExitCode == 0)
            {
                throw inputError;
            }
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch
        {
            await StopCapturedProcessAsync(process, exit).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        async Task WriteAsync()
        {
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(standardInput, batch.Token).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                // A child can reject input and close its pipe before emitting its final diagnostic.
                // Drain output and wait for its exit; a nonzero result carries that diagnostic.
                inputError = exception;
            }
            finally
            {
                // Only the underlying stream is used; closing the writer can flush a broken pipe.
                process.StandardInput.BaseStream.Close();
            }
        }

        async Task GuardAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                batch.Cancel();
                await StopCapturedProcessAsync(process, exit).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task StopCapturedProcessAsync(System.Diagnostics.Process process, Task exit)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception) && process.HasExited)
        {
            // Exit can race the kill. Still wait before relinquishing process ownership.
            await exit.ConfigureAwait(false);
            return;
        }
        await exit.ConfigureAwait(false);
    }
}
