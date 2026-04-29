// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;

namespace Aspire.Cli.Tests.Processes;

public class DetachedProcessLauncherTests
{
    [Fact]
    public void WindowsCreationFlags_CreateIndependentHiddenConsole()
    {
        const uint detachedProcess = 0x00000008;
        const uint createNewConsole = 0x00000010;
        const uint createNewProcessGroup = 0x00000200;
        const uint createNoWindow = 0x08000000;

        Assert.Equal(0u, DetachedProcessLauncher.WindowsDetachedProcessCreationFlags & detachedProcess);
        Assert.NotEqual(0u, DetachedProcessLauncher.WindowsDetachedProcessCreationFlags & createNewConsole);
        Assert.Equal(0u, DetachedProcessLauncher.WindowsDetachedProcessCreationFlags & createNewProcessGroup);
        Assert.NotEqual(0u, DetachedProcessLauncher.WindowsDetachedProcessCreationFlags & createNoWindow);
    }
}
