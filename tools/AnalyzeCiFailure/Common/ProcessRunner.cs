// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace AnalyzeCiFailure.Common;

/// <summary>The outcome of a child process. Standard output is raw bytes so callers can measure or decode it.</summary>
internal sealed record ProcessResult(int ExitCode, byte[] StandardOutput, string StandardError);

internal static class ProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> to completion and captures both output streams.</summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.Start();

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        using var standardOutput = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(standardOutput, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stdoutTask;

        return new ProcessResult(process.ExitCode, standardOutput.ToArray(), await stderrTask);
    }
}
