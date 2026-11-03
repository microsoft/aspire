// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.DotNet;

public class ProcessExecutionDetachedTests(ITestOutputHelper outputHelper)
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task StartAsync_OnWindows_ProvidesValidNullStdin()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only test.");

        await using var child = CreateDetachedExecution(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", """
                Add-Type 'using System; using System.Runtime.InteropServices; public static class Stdio { [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int id); }';
                $inputHandle = [Stdio]::GetStdHandle(-10);
                if ($inputHandle -eq [IntPtr]::Zero -or $inputHandle.ToInt64() -eq -1) { exit 42 }
                if ([Console]::In.Read() -ne -1) { exit 43 }
                [Console]::Out.WriteLine('discarded stdout');
                [Console]::Error.WriteLine('discarded stderr');
                exit 0
                """],
            Environment.CurrentDirectory);

        Assert.True(await child.StartAsync(TestContext.Current.CancellationToken));
        try
        {
            Assert.Equal(0, await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    // Regression test for the duplicate-handle bug that broke `aspire start` on Windows:
    // the Windows detached path points both Stdout and Stderr at the same NUL handle, and
    // PROC_THREAD_ATTRIBUTE_HANDLE_LIST rejects duplicate handle values. Process.Start
    // duplicates the standard handles before building the list, so this spawn must succeed.
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task StartAsync_OnWindows_WithSharedStdoutStderrHandle_Succeeds()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        // A short-lived child is sufficient: we only need CreateProcessW to return successfully.
        // `cmd.exe /c exit 0` returns immediately and never touches stdout/stderr, so any
        // failure mode here is from the spawn primitive, not from the child itself.
        await using var child = CreateDetachedExecution(
            "cmd.exe",
            ["/c", "exit", "0"],
            Environment.CurrentDirectory);

        Assert.True(await child.StartAsync(CancellationToken.None));
        Assert.True(child.ProcessId > 0);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task StartAsync_OnUnix_StartsDirectChildInNewSessionWithExpectedContext()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var capturePath = Path.Combine(workspace.WorkspaceRoot.FullName, "capture.txt");

        await using var detachedProcess = CreateDetachedExecution(
            "/bin/sh",
            ["-c", """
                {
                  printf 'cwd=%s\n' "$PWD"
                  printf 'pgid=%s\n' "$(ps -o pgid= -p $$ | tr -d ' ')"
                  printf 'ppid=%s\n' "$PPID"
                  printf 'home=%s\n' "${HOME:-missing}"
                  printf 'added=%s\n' "$ASPIRE_TEST_ADDED"
                  if read -r line; then printf 'stdin=data\n'; else printf 'stdin=eof\n'; fi
                } > "$ASPIRE_TEST_CAPTURE"
                echo discarded
                exit 7
                """],
            workspace.WorkspaceRoot.FullName,
            environmentVariableFilter: name => string.Equals(name, "HOME", StringComparison.Ordinal),
            environment: new Dictionary<string, string>
            {
                ["ASPIRE_TEST_ADDED"] = "value",
                ["ASPIRE_TEST_CAPTURE"] = capturePath
            });

        Assert.True(await detachedProcess.StartAsync(CancellationToken.None));

        // The exit code is observable because StartDetached keeps the child a direct child of the
        // CLI; setsid() only moves it into its own session and process group.
        Assert.Equal(7, await detachedProcess.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal([
            $"cwd={PathNormalizer.ResolveSymlinks(workspace.WorkspaceRoot.FullName)}",
            $"pgid={detachedProcess.ProcessId.ToString(CultureInfo.InvariantCulture)}",
            $"ppid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}",
            "home=missing",
            "added=value",
            "stdin=eof"
        ], await File.ReadAllLinesAsync(capturePath));
    }

    private static IProcessExecution CreateDetachedExecution(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? environmentVariableFilter = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var factory = new ProcessExecutionFactory(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
        return factory.CreateExecution(
            fileName,
            arguments.ToArray(),
            environment?.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value),
            new DirectoryInfo(workingDirectory),
            new ProcessInvocationOptions
            {
                Detached = true,
                IsolateConsole = true,
                EnvironmentVariableFilter = environmentVariableFilter
            });
    }
}
