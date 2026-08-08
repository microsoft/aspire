// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Xunit;

namespace Infrastructure.Tests;

public sealed class PrDocsCheckWorkflowTests(ITestOutputHelper testOutput)
{
    [Fact]
    public async Task PythonTestsPass()
    {
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        var startInfo = new ProcessStartInfo(python)
        {
            WorkingDirectory = RepoRoot.Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("unittest");
        startInfo.ArgumentList.Add("discover");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(".github/workflows/pr-docs-check");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("test_*.py");
        startInfo.ArgumentList.Add("-v");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {python}.");

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        testOutput.WriteLine(stdout);
        testOutput.WriteLine(stderr);

        Assert.True(
            process.ExitCode == 0,
            $"{python} exited with code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }
}
