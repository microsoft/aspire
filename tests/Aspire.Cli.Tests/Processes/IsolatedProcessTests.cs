// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
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

        await using var child = new IsolatedProcess(startInfo);
        child.OutputDataReceived += (_, line) => stdout.Enqueue(line);
        child.ErrorDataReceived += (_, line) => stderr.Enqueue(line);
        await child.StartAsync(CancellationToken.None);
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

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

        await using var child = new IsolatedProcess(startInfo);
        await child.StartAsync(CancellationToken.None);
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        // Carried explicitly because Process.GetProcessById returns a Process whose
        // StartInfo is empty — telemetry callers depend on these fields.
        Assert.Equal(fileName, child.FileName);
        Assert.Equal(arguments, child.Arguments);

        await Task.WhenAll(child.StandardOutputClosed, child.StandardErrorClosed).WaitAsync(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false, false, false, true, false)]
    [InlineData(true, false, false, true, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, false, true, true, true)]
    [SupportedOSPlatform("windows")]
    public void CreateProcessStartInfo_OnWindows_MapsLaunchOptions(
        bool isolateConsole,
        bool killOnParentExit,
        bool detached,
        bool expectedCreateNoWindow,
        bool expectedOnlyStandardHandlesInherited)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only test.");

        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = "child.exe",
            WorkingDirectory = Environment.CurrentDirectory,
            IsolateConsole = isolateConsole,
            KillOnParentExit = killOnParentExit,
            Detached = detached,
        };
        using var nullHandle = File.OpenNullHandle();

        var psi = IsolatedProcess.CreateProcessStartInfo(startInfo, nullHandle);

        Assert.Equal(expectedCreateNoWindow, psi.CreateNoWindow);
        Assert.Equal(killOnParentExit, psi.KillOnParentExit);
        if (expectedOnlyStandardHandlesInherited)
        {
            Assert.NotNull(psi.InheritedHandles);
            Assert.Empty(psi.InheritedHandles);
        }
        else
        {
            Assert.Null(psi.InheritedHandles);
        }

        Assert.Same(nullHandle, psi.StandardInputHandle);
        Assert.Equal(!detached, psi.RedirectStandardOutput);
        Assert.Equal(!detached, psi.RedirectStandardError);
        Assert.Same(detached ? nullHandle : null, psi.StandardOutputHandle);
        Assert.Same(detached ? nullHandle : null, psi.StandardErrorHandle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public async Task StartAsync_OnWindows_IsolateConsole_ChildReceivesCtrlCThroughItsOwnConsole(bool killOnParentExit)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only test.");

        // Mirror Program.Main: clear any inherited "ignore CTRL+C" attribute so the child, which
        // inherits it across CreateProcess, can observe CTRL_C_EVENT regardless of how the test host
        // was launched.
        WindowsProcessInterop.SetConsoleCtrlHandler(nint.Zero, add: false);

        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = "ping.exe",
            WorkingDirectory = Environment.CurrentDirectory,
            IsolateConsole = true,
            KillOnParentExit = killOnParentExit,
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("120");
        startInfo.ArgumentList.Add("127.0.0.1");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var child = new IsolatedProcess(startInfo);
        child.OutputDataReceived += (_, _) => started.TrySetResult();
        await child.StartAsync(CancellationToken.None);
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var signal = await SendCtrlCThroughAttachedConsoleAsync(child.Id);
            Assert.True(signal.ExitStatus.ExitCode == 0, $"Signaler exited with {signal.ExitStatus.ExitCode}: {signal.StandardError}");

            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            // STATUS_CONTROL_C_EXIT: ping handled CTRL+C and exited, rather than being killed.
            Assert.Equal(unchecked((int)0xC000013A), child.ExitCode);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
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

        await using var child = new IsolatedProcess(startInfo);
        child.OutputDataReceived += (_, line) =>
        {
            seenLines.Enqueue(line);
            if (line.Contains("line-one"))
            {
                throw new InvalidOperationException("intentional callback failure");
            }
        };
        await child.StartAsync(CancellationToken.None);
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

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
    public async Task DisposeAsync_DoesNotWaitForOutputCallbackToComplete()
    {
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (fileName, arguments) = GetEchoCommand("blocked-callback");

        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var child = new IsolatedProcess(startInfo);
        child.OutputDataReceived += (_, _) =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        };

        try
        {
            await child.StartAsync(CancellationToken.None);
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(child.StandardOutputClosed.IsCompleted);
            await child.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseCallback.TrySetResult();
            await child.StandardOutputClosed.WaitAsync(TimeSpan.FromSeconds(10));
            await child.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (fileName, arguments) = GetEchoCommand("should-not-start");

        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        await using var child = new IsolatedProcess(startInfo);
        await child.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => child.StartAsync(CancellationToken.None));
    }

    /// <summary>
    /// Performs the same console-signal sequence as DCP's <c>stop-process-tree</c> on Windows,
    /// from a separate process so the test host's console attachment is never changed:
    /// attach to the target's console, ignore CTRL+C in the signaler, and send CTRL_C_EVENT to
    /// every process attached to that console. AttachConsole fails if the target has no console.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task<ProcessTextOutput> SendCtrlCThroughAttachedConsoleAsync(int processId)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Add-Type -Namespace AspireTests -Name ConsoleSignal -MemberDefinition @'
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
            '@
            [void][AspireTests.ConsoleSignal]::FreeConsole()
            if (-not [AspireTests.ConsoleSignal]::AttachConsole({{processId}})) { [Console]::Error.WriteLine("AttachConsole failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 2 }
            if (-not [AspireTests.ConsoleSignal]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)) { [Console]::Error.WriteLine("SetConsoleCtrlHandler failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 3 }
            if (-not [AspireTests.ConsoleSignal]::GenerateConsoleCtrlEvent(0, 0)) { [Console]::Error.WriteLine("GenerateConsoleCtrlEvent failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 4 }
            exit 0
            """;

        // -EncodedCommand avoids Windows PowerShell's command-line quote handling for the script.
        var startInfo = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))])
        {
            // DCP is launched the same way: its own hidden console, so it can FreeConsole/AttachConsole
            // without touching the console the test host is attached to.
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await Process.RunAndCaptureTextAsync(startInfo, timeout.Token);
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
