// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RemoteHost.Language;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class IntegrationHostLauncherTests
{
    [Fact]
    public void CreateProcessStartInfo_PreservesArgumentBoundariesAndExpandsEntryPoint()
    {
        var entryPoint = Path.GetFullPath(Path.Combine("integration packages", "host entry.mts"));
        var startInfo = IntegrationHostLauncher.CreateProcessStartInfo(
            "integration-runtime",
            [
                "--no-install",
                "tsx",
                "{entryPoint}",
                "--entry={entryPoint}",
                "{entryPoint};{entryPoint}",
                "--label=a \"quoted\" value",
                @"C:\tools with spaces\trailing\",
                "\\\"",
                "",
                "{otherPlaceholder}"
            ],
            entryPoint);

        Assert.Equal(
            [
                "--no-install",
                "tsx",
                entryPoint,
                $"--entry={entryPoint}",
                $"{entryPoint};{entryPoint}",
                "--label=a \"quoted\" value",
                @"C:\tools with spaces\trailing\",
                "\\\"",
                "",
                "{otherPlaceholder}"
            ],
            startInfo.ArgumentList.ToArray());
        Assert.Empty(startInfo.Arguments);
        Assert.Equal("integration-runtime", startInfo.FileName);
        Assert.Equal(Path.GetDirectoryName(entryPoint), startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void CreateProcessStartInfo_AllowsEmptyArgumentList()
    {
        var entryPoint = Path.GetFullPath("host.mts");

        var startInfo = IntegrationHostLauncher.CreateProcessStartInfo("integration-runtime", [], entryPoint);

        Assert.Empty(startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }
}
