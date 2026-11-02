// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class VolumePathLauncherTests
{
    [Theory]
    [InlineData(false, "/select,\"C:\\src\\init.cmd\"")]
    [InlineData(true, "\"C:\\src\\data folder\"")]
    public void CreateStartInfo_Windows_SelectsFileOrOpensFolder(bool isDirectory, string expectedArguments)
    {
        var path = isDirectory ? "C:\\src\\data folder" : "C:\\src\\init.cmd";

        var startInfo = VolumePathLauncher.CreateStartInfo(path, isDirectory, VolumePathPlatform.Windows);

        Assert.NotNull(startInfo);
        Assert.Equal("explorer.exe", startInfo.FileName);
        Assert.Equal(expectedArguments, startInfo.Arguments);
        Assert.Empty(startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void CreateStartInfo_WindowsPathWithQuote_ReturnsNull()
    {
        Assert.Null(VolumePathLauncher.CreateStartInfo("C:\\src\\a\" b", isDirectory: false, VolumePathPlatform.Windows));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateStartInfo_MacOS_RevealsInFinder(bool isDirectory)
    {
        var startInfo = VolumePathLauncher.CreateStartInfo("/src/my \"scripts\"/init.command", isDirectory, VolumePathPlatform.MacOS);

        Assert.NotNull(startInfo);
        Assert.Equal("open", startInfo.FileName);
        Assert.Equal(["-R", "/src/my \"scripts\"/init.command"], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void CreateStartInfo_LinuxFile_OpensContainingFolder()
    {
        // xdg-open would run a .desktop file, so the file's folder is opened instead.
        var path = "/src/scripts/app.desktop";

        var startInfo = VolumePathLauncher.CreateStartInfo(path, isDirectory: false, VolumePathPlatform.Linux);

        Assert.NotNull(startInfo);
        Assert.Equal("xdg-open", startInfo.FileName);
        Assert.Equal([Path.GetDirectoryName(path)!], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void CreateStartInfo_LinuxDirectory_OpensFolder()
    {
        var startInfo = VolumePathLauncher.CreateStartInfo("/src/data", isDirectory: true, VolumePathPlatform.Linux);

        Assert.NotNull(startInfo);
        Assert.Equal("xdg-open", startInfo.FileName);
        Assert.Equal(["/src/data"], startInfo.ArgumentList);
    }
}
