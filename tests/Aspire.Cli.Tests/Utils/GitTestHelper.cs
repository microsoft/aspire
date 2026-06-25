// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;

namespace Aspire.Cli.Tests.Utils;

internal static class GitTestHelper
{
    public static async Task EnsureGitAvailableAsync()
    {
        var startInfo = CreateGitStartInfo(Directory.GetCurrentDirectory(), ["--version"]);

        using var process = new Process { StartInfo = startInfo };
        StartProcessOrSkip(process);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            Assert.Skip($"git is required for this test but 'git --version' failed with exit code {process.ExitCode}: {stderr}");
        }
    }

    public static async Task ConfigureGitIdentityAsync(string workingDirectory)
    {
        // Fresh temporary repos have no inherited identity, but `git commit` requires
        // user.name and user.email. Set them locally to keep tests self-contained.
        await RunGitAsync(workingDirectory, "config", "user.email", "test@example.com");
        await RunGitAsync(workingDirectory, "config", "user.name", "Test User");
        await RunGitAsync(workingDirectory, "config", "commit.gpgsign", "false");
    }

    public static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = CreateGitStartInfo(workingDirectory, arguments);

        using var process = new Process { StartInfo = startInfo };
        StartProcessOrSkip(process);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}. stdout: {stdout}, stderr: {stderr}");
        }
    }

    private static void StartProcessOrSkip(Process process)
    {
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Assert.Skip("git is required for this test but was not found on PATH.");
        }
    }

    private static ProcessStartInfo CreateGitStartInfo(string workingDirectory, string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }
}
