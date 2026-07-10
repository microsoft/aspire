// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;

namespace Aspire.Cli.Tests.Processes;

public class IsolatedProcessTests
{
    [Fact]
    public async Task Start_EchoesLine_ExposesStandardOutputReader()
    {
        var (fileName, arguments) = GetEchoCommand("hello-from-launcher");

        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        await using var child = await IsolatedProcess.StartAsync(startInfo, CancellationToken.None);

        var stdoutTask = child.StandardOutput.ReadToEndAsync();
        var stderrTask = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(10));
        var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("hello-from-launcher", stdout);
        Assert.Empty(stderr);
        Assert.Equal(0, child.ExitCode);
    }

    [Fact]
    public async Task Start_ExposesFileNameAndArgumentsOnReturnedChild()
    {
        var (fileName, arguments) = GetEchoCommand("metadata-check");

        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        await using var child = await IsolatedProcess.StartAsync(startInfo, CancellationToken.None);

        // Carried explicitly because Process.GetProcessById returns a Process whose
        // StartInfo is empty — telemetry callers depend on these fields.
        Assert.Equal(fileName, child.FileName);
        Assert.Equal(arguments, child.Arguments);

        await Task.WhenAll(child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static (string FileName, IReadOnlyList<string> Arguments) GetEchoCommand(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            // cmd /c echo <text> — cmd ships with every Windows install.
            return ("cmd.exe", new[] { "/c", "echo", text });
        }

        return ("/bin/sh", new[] { "-c", $"echo {text}" });
    }

}
