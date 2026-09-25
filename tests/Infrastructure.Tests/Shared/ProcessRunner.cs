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
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardInput = standardInput is not null;
        process.StartInfo.UseShellExecute = false;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                process.StartInfo.Environment[name] = value;
            }
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cancellation = new CancellationTokenSource(effectiveTimeout);
        output.WriteLine($"Executing: {fileName} {string.Join(' ', arguments)} in {workingDirectory}");
        process.Start();
        try
        {
            // Drain both output pipes while writing stdin; any of the three pipes can fill.
            // Apply the timeout to I/O too, including pipes inherited by child processes.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellation.Token);
            await Task.WhenAll(
                stdoutTask,
                stderrTask,
                WriteInputAsync(),
                process.WaitForExitAsync(cancellation.Token));

            var result = new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
            if (result.Output.Length > 0)
            {
                output.WriteLine(result.Output);
            }

            return result;
        }
        catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
        {
            var message = $"Process did not exit within {effectiveTimeout.TotalSeconds} seconds: {fileName}";
            output.WriteLine(message);
            throw new TimeoutException(message, exception);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        async Task WriteInputAsync()
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellation.Token);
                await process.StandardInput.FlushAsync(cancellation.Token);
                process.StandardInput.Close();
            }
        }
    }
}

internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}
