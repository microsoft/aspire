// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;

namespace Aspire.Cli.Tests.Processes;

public class DetachedProcessLauncherTests
{
    [Fact]
    public void WindowsCreationFlags_DetachProcessFromLaunchingConsole()
    {
        const uint detachedProcess = 0x00000008;
        const uint createNewProcessGroup = 0x00000200;

        Assert.NotEqual(0u, DetachedProcessLauncher.WindowsDetachedProcessCreationFlags & detachedProcess);
        Assert.NotEqual(0u, DetachedProcessLauncher.WindowsDetachedProcessCreationFlags & createNewProcessGroup);
    }
}
