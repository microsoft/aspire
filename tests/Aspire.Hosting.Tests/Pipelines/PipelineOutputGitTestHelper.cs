// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Aspire.Hosting.Tests.Pipelines;

internal static class PipelineOutputGitTestHelper
{
    internal static Matcher AllFilesMatcher()
    {
        var matcher = new Matcher(PipelineOutputRoot.PathComparison, preserveFilterOrder: true);
        matcher.AddInclude("**/*");
        return matcher;
    }

    internal static async Task<string> RunAsync(string directory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        // Git setup must write only this test's index and configuration, even with ambient Git overrides.
        process.StartInfo.Environment["GIT_DIR"] = Path.Combine(directory, ".git");
        process.StartInfo.Environment["GIT_WORK_TREE"] = directory;
        process.StartInfo.Environment["GIT_INDEX_FILE"] = Path.Combine(directory, ".git", "index");
        process.StartInfo.Environment["GIT_CONFIG_COUNT"] = "0";
        foreach (var name in new[] { "GIT_COMMON_DIR", "GIT_CONFIG", "GIT_CONFIG_PARAMETERS" })
        {
            process.StartInfo.Environment.Remove(name);
        }
        return await RunProcessAsync(process);
    }

    internal static async Task CreateDirectoryLinkAsync(string path, string target, bool junction)
    {
        if (!junction)
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", path, target])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        await RunProcessAsync(process);
    }

    private static async Task<string> RunProcessAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var stderr = process.StandardError.ReadToEndAsync(cancellation.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellation.Token), stdout, stderr);
        }
        catch (Exception failure)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception race) when ((race is InvalidOperationException or Win32Exception) && process.HasExited)
            {
                failure.Data["ProcessTermination"] = race.Message;
            }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            throw;
        }
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }
}
