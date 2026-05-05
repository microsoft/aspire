// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace TestSelector;

internal static class GitHelper
{
    public static async Task<List<string>> GetGitChangedFilesAsync(string fromRef, string? toRef, string workingDir)
    {
        var args = new List<string> { "diff", "--name-only" };
        if (!string.IsNullOrEmpty(toRef))
        {
            // Use three-dot diff to compare against the merge base,
            // so only the PR's actual changes are shown.
            args.Add($"{fromRef}...{toRef}");
        }
        else
        {
            args.Add(fromRef);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start git process");
        }

        // Read both streams asynchronously to avoid deadlock when pipe buffers fill
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git diff failed: {await errorTask.ConfigureAwait(false)}");
        }

        var output = await outputTask.ConfigureAwait(false);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToList();
    }
}
