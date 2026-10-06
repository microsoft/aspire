// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class TrayStartupOptionsTests
{
    public static bool SupportsMac => OperatingSystem.IsMacOS();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupEntryIsForwardedWithoutReplacingTheBackend(bool windows)
    {
        using var bundle = new TestTrayBundle();
        File.WriteAllText(Path.Combine(bundle.Root, "tray", "aspire-tray.exe"), "");
        var startup = Path.Combine(bundle.Root, "installed & stable CLI");
        var options = TrayOptions.Parse(["--cli", bundle.CliPath, "--bundle-root", bundle.Root, "--startup-cli", startup]);
        Assert.Equal(bundle.CliPath, options.CliPath);
        Assert.Equal(startup, options.StartupCliPath);

        var start = windows ? TrayLaunchCommand.CreateWindowsStartInfo(options)
            : TrayLaunchCommand.CreateStartInfo(options);
        var expected = new[] { "--cli", bundle.CliPath, "--bundle-root", bundle.Root, "--startup-cli", startup };
        Assert.Equal(expected, start.ArgumentList);
    }

    [Fact]
    public void StartupEntryIsOptionalButCannotBeRelativeDuplicatedOrUsedBySmoke()
    {
        using var bundle = new TestTrayBundle();
        Assert.Null(TrayOptions.Parse(["--cli", bundle.CliPath]).StartupCliPath);
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse(["--cli", bundle.CliPath, "--startup-cli", "relative"]));
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse([
            "--cli", bundle.CliPath, "--startup-cli", bundle.CliPath, "--startup-cli", bundle.CliPath]));
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse([
            "--cli", bundle.CliPath, "--startup-cli", bundle.CliPath, "--smoke-seconds", "1"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingManagedOrUntrustedCliCannotBeEnabled(bool windows)
    {
        using var installation = new TestTrayStartupInstallation();
        var options = installation.Options(windows);
        Assert.Null(TrayStartupEntry.GetUnavailableReason(options, true, windows));
        Assert.NotNull(TrayStartupEntry.GetUnavailableReason(options, false, windows));
        Assert.NotNull(TrayStartupEntry.GetUnavailableReason(options with { StartupCliPath = null }, true, windows));
        Assert.NotNull(TrayStartupEntry.GetUnavailableReason(options with { BundleRoot = null }, true, windows));
        Assert.NotNull(TrayStartupEntry.GetUnavailableReason(options with { SmokeSeconds = 1 }, true, windows));

        File.WriteAllText(Path.ChangeExtension(options.CliPath, ".runtimeconfig.json"), "{}");
        Assert.NotNull(TrayStartupEntry.GetUnavailableReason(options, true, windows));
        File.Delete(options.CliPath);
        Assert.NotNull(TrayStartupEntry.GetUnavailableReason(options, true, windows));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupCliInsideTheVersionedPayloadIsRejected(bool windows)
    {
        using var installation = new TestTrayStartupInstallation();
        var options = installation.Options(windows);
        var cli = Path.Combine(installation.BundleRoot, "aspire");
        File.Copy(options.CliPath, cli);

        Assert.Equal("Launch at sign-in cannot use a CLI inside an extracted version bundle.",
            TrayStartupEntry.GetUnavailableReason(options with { CliPath = cli, StartupCliPath = cli }, true, windows));
    }

    [Theory]
    [InlineData("node_modules")]
    [InlineData(".store")]
    public void ExplicitStartupEntriesCannotPointIntoPackageVersionStores(string store)
    {
        using var installation = new TestTrayStartupInstallation();
        var cli = Path.Combine(installation.Root, store, "aspire", "1.0.0", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.Copy(installation.MacCli, cli);
        var options = installation.Options(windows: false) with { CliPath = cli, StartupCliPath = cli };

        Assert.Equal(TrayStartupEntry.Unavailable, TrayStartupEntry.GetUnavailableReason(options, true, false));
    }

    [Fact(Skip = "Symlinks are checked on macOS.", SkipUnless = nameof(SupportsMac))]
    public void StableSymlinksArePreservedButCannotHideAVersionedPayload()
    {
        using var installation = new TestTrayStartupInstallation();
        var options = installation.Options(windows: false);
        var link = Path.Combine(installation.Root, "stable-installation");
        File.CreateSymbolicLink(link, installation.MacCli);
        Assert.Null(TrayStartupEntry.GetUnavailableReason(options with { StartupCliPath = link }, true, false));

        File.Delete(link);
        var versioned = Path.Combine(installation.BundleRoot, "aspire");
        File.Copy(installation.MacCli, versioned);
        File.CreateSymbolicLink(link, versioned);
        Assert.Equal("Launch at sign-in cannot use a CLI inside an extracted version bundle.",
            TrayStartupEntry.GetUnavailableReason(options with { CliPath = versioned, StartupCliPath = link }, true, false));
    }

    [Fact]
    public void WindowsRunCommandQuotesArgumentsRatherThanInterpolatingShellCode()
    {
        Assert.Equal("\"C:\\Profile & A\\aspire-tray-login.exe\" \"login-start\" \"--cli\" \"C:\\CLI & tools\\aspire.exe\"",
            WindowsTrayStartupSettings.BuildRunCommand(@"C:\Profile & A\aspire-tray-login.exe", @"C:\CLI & tools\aspire.exe"));
        Assert.Equal("\"C:\\tray.exe\" \"login-start\" \"--cli\" \"C:\\quoted\\\"name\\aspire.exe\"",
            WindowsTrayStartupSettings.BuildRunCommand(@"C:\tray.exe", "C:\\quoted\"name\\aspire.exe"));
    }

    [Fact]
    public void LoginBootstrapRejectsMissingAndManagedCliWithoutLaunchingAnything()
    {
        using var installation = new TestTrayStartupInstallation();
        File.WriteAllText(Path.ChangeExtension(installation.WindowsCli, ".runtimeconfig.json"), "{}");
        Assert.Throws<InvalidOperationException>(() => WindowsLoginBootstrap.CreateStartInfo(installation.WindowsCli));
        File.Delete(installation.WindowsCli);
        Assert.Throws<InvalidOperationException>(() => WindowsLoginBootstrap.CreateStartInfo(installation.WindowsCli));
    }
}
