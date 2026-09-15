// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class TrayLaunchCommandTests
{
    [Fact]
    public void LaunchServicesGetsExactBundleAndCliPathsWithoutShellQuoting()
    {
        using var bundle = new TestTrayBundle();
        var log = Path.Combine(bundle.Root, "tray log.txt");
        var options = TrayOptions.Parse(["--cli", bundle.CliPath, "--bundle-root", bundle.Root]);

        var start = TrayLaunchCommand.CreateStartInfo(options, log);

        Assert.Equal("/usr/bin/open", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), start.WorkingDirectory);
        Assert.Equal(new[]
        {
            "-n", "-g", "--stdout", log, "--stderr", log, bundle.AppPath,
            "--args", "--cli", bundle.CliPath, "--bundle-root", bundle.Root
        }, start.ArgumentList);
    }

    [Fact]
    public void MissingPackagedExecutableDoesNotFallBackToPath()
    {
        using var bundle = new TestTrayBundle();
        File.Delete(Path.Combine(bundle.AppPath, "Contents", "MacOS", "aspire-tray"));

        Assert.Throws<FileNotFoundException>(() => TrayLaunchCommand.CreateStartInfo(
            new(bundle.CliPath, null, bundle.Root, null), Path.Combine(bundle.Root, "tray.log")));
    }

    [Fact]
    public void MissingAppMetadataIsRejected()
    {
        using var bundle = new TestTrayBundle();
        File.Delete(Path.Combine(bundle.AppPath, "Contents", "Info.plist"));

        Assert.Throws<FileNotFoundException>(() => TrayLaunchCommand.CreateStartInfo(
            new(bundle.CliPath, null, bundle.Root, null), Path.Combine(bundle.Root, "tray.log")));
    }

    [Fact]
    public void RelativeLogPathsAreRejected()
    {
        using var bundle = new TestTrayBundle();
        Assert.Throws<ArgumentException>(() =>
            TrayLaunchCommand.CreateStartInfo(new(bundle.CliPath, null, bundle.Root, null), "tray.log"));
    }

    [Fact]
    public void ForegroundDevelopmentLaunchDoesNotRequireABundle()
    {
        using var bundle = new TestTrayBundle();
        Assert.Equal(new(bundle.CliPath, null, null, null), TrayOptions.Parse(["--cli", bundle.CliPath]));
    }

    [Theory]
    [InlineData("--bundle-root", "relative")]
    [InlineData("--bundle-root", "")]
    [InlineData("--unknown", "value")]
    public void InvalidLaunchOptionsAreRejected(string option, string value)
    {
        using var bundle = new TestTrayBundle();
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse(["--cli", bundle.CliPath, option, value]));
    }

    [Fact]
    public void SmokeCannotUseARealBundleLease()
    {
        using var bundle = new TestTrayBundle();
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse([
            "--cli", bundle.CliPath, "--bundle-root", bundle.Root, "--smoke-seconds", "20"]));
    }
}
