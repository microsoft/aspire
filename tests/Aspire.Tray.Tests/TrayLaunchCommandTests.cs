// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Aspire.Tray.Tests.Helpers;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Tray.Tests;

public class TrayLaunchCommandTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DetachedLaunchPreservesOptionalBundleLeaseHandoff(bool windows, bool leased)
    {
        using var bundle = new TestTrayBundle();
        File.WriteAllText(Path.Combine(bundle.Root, "tray", "aspire-tray.exe"), "");
        var remoteOptions = new RemoteInvokeOptions();
        if (leased)
        {
            remoteOptions.StartInfo.Environment[BundleDiscovery.BundleVersionDirectoryEnvVar] = bundle.Root;
        }
        else
        {
            remoteOptions.StartInfo.Environment.Remove(BundleDiscovery.BundleVersionDirectoryEnvVar);
        }

        using (var remote = RemoteExecutor.Invoke(static (cliPath, root, windowsValue, leasedValue) =>
        {
            var windows = bool.Parse(windowsValue);
            var leased = bool.Parse(leasedValue);
            var options = TrayOptions.Parse(["--cli", cliPath, "--bundle-root", root]);
            var start = windows ? TrayLaunchCommand.CreateWindowsStartInfo(options) : TrayLaunchCommand.CreateStartInfo(options);

            start.Environment.TryGetValue(BundleDiscovery.BundleVersionDirectoryEnvVar, out var inheritedVersionDirectory);
            Assert.Equal(leased ? root : null, inheritedVersionDirectory);
            using var guiLease = BundleVersionLease.TryAcquireFromEnvironment("tray", "tray");
            using (var helperLease = BundleVersionLease.TryAcquireFromEnvironment("tray-launcher", "tray start"))
            {
                Assert.Equal(leased, helperLease is not null);
                Assert.Equal(leased, guiLease is not null);
                Assert.Equal(leased, BundleVersionLease.HasActiveLease(root));
            }
            Assert.Equal(leased, BundleVersionLease.HasActiveLease(root));
        }, bundle.CliPath, bundle.Root, windows.ToString(), leased.ToString(), remoteOptions))
        {
        }

        Assert.False(BundleVersionLease.HasActiveLease(bundle.Root));
        Assert.False(Directory.Exists(Path.Combine(bundle.Root, BundleVersionLease.LeasesDirectoryName)));
    }

    [Fact]
    public void DetachedLaunchGetsExactBundleAndCliPathsWithoutShellQuoting()
    {
        using var bundle = new TestTrayBundle();
        var options = TrayOptions.Parse(["--cli", bundle.CliPath, "--bundle-root", bundle.Root]);

        var start = TrayLaunchCommand.CreateStartInfo(options);

        Assert.Equal(Path.Combine(bundle.AppPath, "Contents", "MacOS", "aspire-tray"), start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.False(start.RedirectStandardOutput);
        Assert.False(start.RedirectStandardError);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), start.WorkingDirectory);
        Assert.Equal(new[]
        {
            "--cli", bundle.CliPath, "--bundle-root", bundle.Root
        }, start.ArgumentList);
    }

    [Fact]
    public void MissingPackagedExecutableDoesNotFallBackToPath()
    {
        using var bundle = new TestTrayBundle();
        File.Delete(Path.Combine(bundle.AppPath, "Contents", "MacOS", "aspire-tray"));

        Assert.Throws<FileNotFoundException>(() => TrayLaunchCommand.CreateStartInfo(
            new(bundle.CliPath, null, bundle.Root, null)));
    }

    [Fact]
    public void MissingAppMetadataIsRejected()
    {
        using var bundle = new TestTrayBundle();
        File.Delete(Path.Combine(bundle.AppPath, "Contents", "Info.plist"));

        Assert.Throws<FileNotFoundException>(() => TrayLaunchCommand.CreateStartInfo(
            new(bundle.CliPath, null, bundle.Root, null)));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteractiveSmokeCanPrecedeOrFollowTheDuration(bool interactiveFirst)
    {
        using var bundle = new TestTrayBundle();
        var options = TrayOptions.Parse(interactiveFirst
            ? ["--cli", bundle.CliPath, "--smoke-interactive", "--smoke-seconds", "120"]
            : ["--cli", bundle.CliPath, "--smoke-seconds", "120", "--smoke-interactive"]);

        Assert.True(options.InteractiveSmoke);
        Assert.Equal(120, options.SmokeSeconds);
        Assert.Null(options.BundleRoot);
        Assert.Null(options.StartupCliPath);
    }

    [Fact]
    public void SmokeIsNotInteractiveUnlessRequested()
    {
        using var bundle = new TestTrayBundle();
        Assert.False(TrayOptions.Parse(["--cli", bundle.CliPath]).InteractiveSmoke);
        Assert.False(TrayOptions.Parse(["--cli", bundle.CliPath, "--smoke-seconds", "1"]).InteractiveSmoke);
    }

    [Fact]
    public void InteractiveSmokeRequiresADurationAndCannotBeRepeated()
    {
        using var bundle = new TestTrayBundle();
        Assert.Equal("Interactive smoke requires --smoke-seconds.",
            Assert.Throws<ArgumentException>(() => TrayOptions.Parse(["--cli", bundle.CliPath, "--smoke-interactive"])).Message);
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse([
            "--cli", bundle.CliPath, "--smoke-seconds", "120", "--smoke-interactive", "--smoke-interactive"]));
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse([
            "--cli", bundle.CliPath, "--bundle-root", bundle.Root, "--smoke-seconds", "120", "--smoke-interactive"]));
    }
}
