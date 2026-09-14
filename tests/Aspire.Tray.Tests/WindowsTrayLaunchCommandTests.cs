// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class WindowsTrayLaunchCommandTests
{
    [Fact]
    public void LaunchUsesTheExactPackagedExecutableAndExplicitArguments()
    {
        using var bundle = new TestTrayBundle();
        var executable = Path.Combine(bundle.Root, "tray", "aspire-tray.exe");
        File.WriteAllText(executable, "");

        var start = TrayLaunchCommand.CreateWindowsStartInfo(new(bundle.CliPath, null, bundle.Root));

        Assert.Equal(executable, start.FileName);
        Assert.Equal(Path.Combine(bundle.Root, "tray"), start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.RedirectStandardInput);
        Assert.False(start.RedirectStandardOutput);
        Assert.False(start.RedirectStandardError);
        Assert.Equal(new[] { "--cli", bundle.CliPath, "--bundle-root", bundle.Root }, start.ArgumentList);
    }

    [Fact]
    public void MissingWindowsPayloadProducesAnExplicitFailure()
    {
        using var bundle = new TestTrayBundle();
        var error = Assert.Throws<FileNotFoundException>(() =>
            TrayLaunchCommand.CreateWindowsStartInfo(new(bundle.CliPath, null, bundle.Root)));
        Assert.Equal(Path.Combine(bundle.Root, "tray", "aspire-tray.exe"), error.FileName);
    }

    [Fact]
    public void MissingCliProducesAnExplicitFailure()
    {
        using var bundle = new TestTrayBundle();
        File.WriteAllText(Path.Combine(bundle.Root, "tray", "aspire-tray.exe"), "");
        File.Delete(bundle.CliPath);

        var error = Assert.Throws<FileNotFoundException>(() =>
            TrayLaunchCommand.CreateWindowsStartInfo(new(bundle.CliPath, null, bundle.Root)));
        Assert.Equal(bundle.CliPath, error.FileName);
    }

    [Fact]
    public void DetachedLaunchRequiresABundleAndRejectsSmoke()
    {
        using var bundle = new TestTrayBundle();
        Assert.Throws<ArgumentException>(() =>
            TrayLaunchCommand.CreateWindowsStartInfo(new(bundle.CliPath, null, null)));
        Assert.Throws<ArgumentException>(() =>
            TrayLaunchCommand.CreateWindowsStartInfo(new(bundle.CliPath, 1, bundle.Root)));
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "\"plain\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\tb", "\"a\tb\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("C:\\a b\\", "\"C:\\a b\\\\\"")]
    [InlineData("C:\\a b\\\\", "\"C:\\a b\\\\\\\\\"")]
    [InlineData("a&b|c%PATH%", "\"a&b|c%PATH%\"")]
    public void NativeCommandLinePreservesArguments(string argument, string expected)
    {
        var start = new ProcessStartInfo(@"C:\Aspire Tray\aspire-tray.exe");
        start.ArgumentList.Add(argument);

        Assert.Equal("\"C:\\Aspire Tray\\aspire-tray.exe\" " + expected,
            TrayLaunchCommand.BuildWindowsCommandLine(start));
    }
}
