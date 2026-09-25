// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Utils;

namespace Microsoft.AspNetCore.Certificates.Generation;

internal static class CertificateProcessRunner
{
    public static CertificateProcessResult Run(ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
        => RunCore(startInfo, captureOutput: false, cancellationToken);

    public static CertificateProcessResult RunAndCaptureText(ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
        => RunCore(startInfo, captureOutput: true, cancellationToken);

    private static CertificateProcessResult RunCore(ProcessStartInfo startInfo, bool captureOutput, CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start process '{startInfo.FileName}'.");

        var outputTask = captureOutput
            ? ReadOutputAsync(process, startInfo, cancellationToken)
            : DiscardOutputAsync(process, startInfo, cancellationToken);

        try
        {
            Task.WhenAll(
                outputTask,
                process.WaitForExitAsync(cancellationToken)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // RunAndCaptureTextAsync only kills the root process. Certificate checks must also
            // terminate descendants when canceled, so retain ownership of the process.
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // The process either exited concurrently or could not be killed by this platform.
            }

            throw;
        }

        var output = outputTask.GetAwaiter().GetResult();
        return new CertificateProcessResult(process.ExitCode, output.StandardOutput, output.StandardError);
    }

    private static async Task<(string StandardOutput, string StandardError)> ReadOutputAsync(Process process, ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        if (startInfo.RedirectStandardOutput && startInfo.RedirectStandardError)
        {
            return await ProcessOutputReader.ReadAllTextAsync(process, cancellationToken).ConfigureAwait(false);
        }

        // ReadAllTextAsync requires both streams to be redirected. Some security commands
        // capture only stdout and leave stderr inherited, so read just the redirected stream.
        return (
            startInfo.RedirectStandardOutput ? await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false) : string.Empty,
            startInfo.RedirectStandardError ? await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false) : string.Empty);
    }

    private static async Task<(string StandardOutput, string StandardError)> DiscardOutputAsync(Process process, ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        // Do not buffer output for callers that only need the exit code.
        await Task.WhenAll(
            startInfo.RedirectStandardOutput ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken) : Task.CompletedTask,
            startInfo.RedirectStandardError ? process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken) : Task.CompletedTask).ConfigureAwait(false);

        return (string.Empty, string.Empty);
    }
}

internal readonly record struct CertificateProcessResult(int ExitCode, string StandardOutput, string StandardError);
