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
            entryPoint,
            isWindows: false);

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

        var startInfo = IntegrationHostLauncher.CreateProcessStartInfo("integration-runtime", [], entryPoint, isWindows: false);

        Assert.Empty(startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Theory]
    [InlineData("npx.cmd")]
    [InlineData("npx.CMD")]
    [InlineData("npx.bat")]
    public void CreateProcessStartInfo_WindowsBatchShim_UsesOuterQuotedCommand(string shim)
    {
        var command = $@"C:\Program Files\nodejs\{shim}";
        var entryPoint = Path.GetFullPath(Path.Combine("integration packages", "host entry.mts"));

        var startInfo = IntegrationHostLauncher.CreateProcessStartInfo(
            command, ["--no-install", "tsx", "{entryPoint}"], entryPoint, isWindows: true);

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Equal($"/c \"\"{command}\" \"--no-install\" \"tsx\" \"{entryPoint}\"\"", startInfo.Arguments);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }
}
