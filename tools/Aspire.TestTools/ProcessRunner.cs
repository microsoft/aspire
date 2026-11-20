// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.TestTools;

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        string? outputFile = null)
    {
        var fileName = executable;
        if (!Path.IsPathRooted(executable) && environment is not null && environment.TryGetValue("PATH", out var searchPath))
        {
            // ProcessStartInfo.Environment does not control executable lookup on Unix.
            // Honor the caller's PATH without changing process-global state.
            // Windows native lookup appends .exe to extensionless executable names.
            var searchName = OperatingSystem.IsWindows() && !Path.HasExtension(executable) ? executable + ".exe" : executable;
            fileName = searchPath.Split(Path.PathSeparator).Select(path => Path.Combine(path, searchName))
                .FirstOrDefault(File.Exists) ?? throw new System.ComponentModel.Win32Exception($"Executable not found: {executable}");
        }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            process.StartInfo.Environment[name] = value;
        }

        // Open the destination before launching so an invalid output path cannot
        // leave a child waiting on an unread stdout pipe.
        using var destination = outputFile is null ? null : File.Create(outputFile);
        process.Start();
        // Both pipes must drain concurrently. ZIP bytes must never pass through
        // a text decoder; text callers receive stdout and stderr separately.
        var stderr = process.StandardError.ReadToEndAsync();
        if (destination is not null)
        {
            await process.StandardOutput.BaseStream.CopyToAsync(destination).ConfigureAwait(continueOnCapturedContext: false);
            await process.WaitForExitAsync().ConfigureAwait(continueOnCapturedContext: false);

            return new ProcessResult(process.ExitCode, "", await stderr.ConfigureAwait(continueOnCapturedContext: false));
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(continueOnCapturedContext: false);

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(continueOnCapturedContext: false), await stderr.ConfigureAwait(continueOnCapturedContext: false));
    }
}

public sealed record ProcessResult(int ExitCode, string Output, string Error);
