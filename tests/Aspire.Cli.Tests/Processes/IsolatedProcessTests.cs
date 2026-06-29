// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Cli.Processes;

namespace Aspire.Cli.Tests.Processes;

public class IsolatedProcessTests
{
    [Fact]
    public async Task Start_EchoesLine_InvokesOutputCallbackAndCompletesStandardOutputClosed()
    {
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();

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

        await using var child = IsolatedProcess.Start(
            startInfo,
            standardOutputHandler: (_, line) => stdout.Enqueue(line),
            standardErrorHandler: (_, line) => stderr.Enqueue(line));

        // Both pumps complete on pipe EOF — child exits within tens of milliseconds, but
        // the OS pipe close + StreamReader drain can take a bit longer under load.
        await Task.WhenAll(child.StandardOutputClosed, child.StandardErrorClosed).WaitAsync(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("hello-from-launcher", stdout);
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

        await using var child = IsolatedProcess.Start(
            startInfo,
            standardOutputHandler: static (_, _) => { },
            standardErrorHandler: static (_, _) => { });

        // Carried explicitly because Process.GetProcessById returns a Process whose
        // StartInfo is empty — telemetry callers depend on these fields.
        Assert.Equal(fileName, child.FileName);
        Assert.Equal(arguments, child.Arguments);

        await Task.WhenAll(child.StandardOutputClosed, child.StandardErrorClosed).WaitAsync(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Start_CallbackThrows_PumpDrainsToEndAndFaultsStandardOutputClosed()
    {
        // Emit two lines; callback throws on the FIRST and records every line it sees so we
        // can verify the pump kept draining (i.e. did not abandon the pipe after the throw).
        var seenLines = new ConcurrentQueue<string>();
        var (fileName, arguments) = GetTwoLineCommand("line-one", "line-two");

        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        await using var child = IsolatedProcess.Start(
            startInfo,
            standardOutputHandler: (_, line) =>
            {
                seenLines.Enqueue(line);
                if (line.Contains("line-one"))
                {
                    throw new InvalidOperationException("intentional callback failure");
                }
            },
            standardErrorHandler: static (_, _) => { });

        // StandardOutputClosed should fault with the recorded exception, but only AFTER
        // draining every line. The first OperationCanceledException-style early-exit was the bug.
        var fault = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await child.StandardOutputClosed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("intentional callback failure", fault.Message);

        await child.StandardErrorClosed.WaitAsync(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(seenLines, line => line.Contains("line-one"));
        Assert.Contains(seenLines, line => line.Contains("line-two"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Start_NonIsolatedWithJobHandle_OnWindows_AssignsChildToJob()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only behavior: kill-on-close job is a Win32 primitive.");

        // Regression coverage for https://github.com/microsoft/aspire/issues/18490 — non-isolated
        // helper children (aspire-managed.exe nuget search, dotnet package search) used to be
        // spawned outside the CLI's kill-on-close job and survived an abnormal CLI exit (e.g.
        // TerminateProcess from the VS Code extension), accumulating as orphans. After the fix,
        // ProcessExecutionFactory wires JobHandle through for BindChildToCliJob=true and
        // IsolatedProcess.StartRedirected calls AssignProcessToJobObject after Process.Start so
        // disposing the job kills the still-running child even though IsolateConsole stayed false
        // (no per-call conhost.exe overhead).
        using var job = new WindowsConsoleProcessJob();

        // Use the long-running ping shape from WindowsConsoleProcessJobTests so the same
        // observable signal (process exits because the job's last handle was closed) drives this
        // test too; the difference is that JobHandle goes through the non-isolated StartRedirected
        // path here instead of the isolated SpawnConsoleIsolatedProcess path.
        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = Environment.CurrentDirectory,
            IsolateConsole = false,
            JobHandle = job.Handle,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping");
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("127.0.0.1");

        await using var child = IsolatedProcess.Start(
            startInfo,
            standardOutputHandler: static (_, _) => { },
            standardErrorHandler: static (_, _) => { });

        // Confirm the child actually started before disposing the job — otherwise a fast spawn
        // failure would look identical to a successful kill-on-close.
        Assert.False(child.HasExited);

        job.Dispose();

        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(child.HasExited);
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

    private static (string FileName, IReadOnlyList<string> Arguments) GetTwoLineCommand(string line1, string line2)
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd.exe", new[] { "/c", $"echo {line1}&echo {line2}" });
        }

        return ("/bin/sh", new[] { "-c", $"echo {line1}; echo {line2}" });
    }
}
