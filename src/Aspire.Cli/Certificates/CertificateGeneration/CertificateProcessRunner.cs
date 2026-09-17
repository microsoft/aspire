// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

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

        // Drain both redirected pipes concurrently. Waiting for the process before reading, or
        // reading one pipe to completion before the other, can deadlock when either pipe fills.
        var standardOutputTask = startInfo.RedirectStandardOutput
            ? ReadOutputAsync(process.StandardOutput, captureOutput, cancellationToken)
            : Task.FromResult(string.Empty);
        var standardErrorTask = startInfo.RedirectStandardError
            ? ReadOutputAsync(process.StandardError, captureOutput, cancellationToken)
            : Task.FromResult(string.Empty);

        try
        {
            Task.WhenAll(
                standardOutputTask,
                standardErrorTask,
                process.WaitForExitAsync(cancellationToken)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

        return new CertificateProcessResult(
            process.ExitCode,
            standardOutputTask.GetAwaiter().GetResult(),
            standardErrorTask.GetAwaiter().GetResult());
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, bool captureOutput, CancellationToken cancellationToken)
    {
        if (captureOutput)
        {
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        await reader.BaseStream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        return string.Empty;
    }
}

internal readonly record struct CertificateProcessResult(int ExitCode, string StandardOutput, string StandardError);
