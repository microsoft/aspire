// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Cli.Processes;

namespace Aspire.Cli.Tests.Processes;

public class DetachedProcessLauncherTests
{
    // Regression test for the duplicate-handle bug that broke `aspire start` on Windows:
    // DetachedProcessLauncher.StartWindows points both Stdout and Stderr at the same NUL
    // handle, and PROC_THREAD_ATTRIBUTE_HANDLE_LIST rejects duplicate handle values —
    // CreateProcessW returns ERROR_INVALID_PARAMETER (87). The unified
    // WindowsProcessInterop.SpawnProcess de-duplicates the inheritable
    // handle list, so this spawn must succeed.
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Start_OnWindows_WithSharedStdoutStderrHandle_Succeeds()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        // A short-lived child is sufficient: we only need CreateProcessW to return successfully.
        // `cmd.exe /c exit 0` returns immediately and never touches stdout/stderr, so any
        // failure mode here is from the spawn primitive, not from the child itself.
        using var child = DetachedProcessLauncher.Start(
            "cmd.exe",
            ["/c", "exit", "0"],
            Environment.CurrentDirectory);

        Assert.True(child.Id > 0);
    }
}
