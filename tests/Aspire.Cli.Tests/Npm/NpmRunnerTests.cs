// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Cli.Npm;

namespace Aspire.Cli.Tests.Npm;

public class NpmRunnerTests
{
    [Fact]
    public void CreateNpmProcessStartInfo_SetsCommonProperties()
    {
        var startInfo = NpmRunner.CreateNpmProcessStartInfo("/usr/bin/npm", ["view", "express", "version"], "/tmp/workdir");

        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal("/tmp/workdir", startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnWindows_WithCmdExtension_UsesCmdExe()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            @"C:\Program Files\nodejs\npm.cmd",
            ["view", "@playwright/cli@0.1.1", "version", "--registry", "https://registry.npmjs.org/"],
            @"C:\temp\workdir");

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Contains("npm.cmd", startInfo.Arguments);
        Assert.Contains("view", startInfo.Arguments);
        Assert.Contains("@playwright/cli@0.1.1", startInfo.Arguments);
        Assert.Contains("version", startInfo.Arguments);
        Assert.Contains("--registry", startInfo.Arguments);
        Assert.StartsWith("/c ", startInfo.Arguments);
        Assert.Equal(@"C:\temp\workdir", startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnWindows_WithCmdExtension_WrapsInOuterQuotes()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            @"C:\Program Files\nodejs\npm.cmd",
            ["view", "express", "version"],
            @"C:\temp");

        // cmd.exe /c requires outer quotes wrapping the entire command:
        // /c ""C:\Program Files\nodejs\npm.cmd" "view" "express" "version""
        var args = startInfo.Arguments;
        Assert.StartsWith(@"/c """, args);
        Assert.EndsWith(@"""", args);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnWindows_WithExeExtension_DoesNotUseCmdExe()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            @"C:\Program Files\nodejs\npm.exe",
            ["view", "express", "version"],
            @"C:\temp");

        Assert.Equal(@"C:\Program Files\nodejs\npm.exe", startInfo.FileName);
        Assert.Equal(["view", "express", "version"], startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnNonWindows_UsesDirectInvocation()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Non-Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            "/usr/local/bin/npm",
            ["view", "@playwright/cli@0.1.1", "version"],
            "/tmp/workdir");

        Assert.Equal("/usr/local/bin/npm", startInfo.FileName);
        Assert.Equal(["view", "@playwright/cli@0.1.1", "version"], startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnNonWindows_CmdExtensionIsIgnored()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Non-Windows-only test.");

        // On non-Windows, even a .cmd path is invoked directly (not via cmd.exe).
        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            "/usr/local/bin/npm.cmd",
            ["view", "express", "version"],
            "/tmp");

        Assert.Equal("/usr/local/bin/npm.cmd", startInfo.FileName);
        Assert.Equal(["view", "express", "version"], startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_WithEmptyArgs_OnNonWindows_ProducesValidStartInfo()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Non-Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo("/usr/bin/npm", [], "/tmp");

        Assert.Equal("/usr/bin/npm", startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_WithEmptyArgs_OnWindows_ProducesValidStartInfo()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo(@"C:\Program Files\nodejs\npm.cmd", [], @"C:\temp");

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Contains("npm.cmd", startInfo.Arguments);
        Assert.Equal(@"C:\temp", startInfo.WorkingDirectory);
    }
}
