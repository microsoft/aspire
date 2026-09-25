// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Xunit;

namespace Infrastructure.Tests;

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        ITestOutputHelper output,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        output.WriteLine($"Executing: {fileName} {string.Join(' ', arguments)} in {workingDirectory}");
        process.Start();
        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var result = new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        if (result.Output.Length > 0)
        {
            output.WriteLine(result.Output);
        }

        return result;
    }
}

internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}
