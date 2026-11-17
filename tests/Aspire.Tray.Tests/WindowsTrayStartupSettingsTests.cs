// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using Aspire.Tray.Tests.Helpers;
using Microsoft.Win32;

namespace Aspire.Tray.Tests;

public class WindowsTrayStartupSettingsTests
{
    public static bool SupportsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Registry behavior requires Windows; this test never uses the real Run key.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public void RegistryStoreTouchesOnlyItsValueInAnIsolatedKey()
    {
        var path = $@"Software\Aspire.Tray.Tests\Startup-{Guid.NewGuid():N}";
        var store = new WindowsRunStartupRegistrationStore(Registry.CurrentUser, path, "AspireTray");
        try
        {
            Assert.Null(store.Read());
            using var absent = Registry.CurrentUser.OpenSubKey(path);
            Assert.Null(absent);
            store.Write(null, "owned");
            using (var key = Registry.CurrentUser.OpenSubKey(path, writable: true))
            {
                Assert.NotNull(key);
                key.SetValue("OtherApplication", "unchanged");
                key.SetValue("AspireTray", "external");
            }
            Assert.Throws<InvalidOperationException>(() => store.Write("owned", null));
            Assert.Equal("external", store.Read());
            store.Write("external", null);
            using var remaining = Registry.CurrentUser.OpenSubKey(path);
            Assert.NotNull(remaining);
            Assert.Equal("unchanged", remaining.GetValue("OtherApplication"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void ReadingOrDisablingDoesNotInstallAnything()
    {
        using var installation = new TestTrayStartupInstallation();
        var store = new TestTrayStartupRegistrationStore();
        var settings = installation.WindowsSettings(store, nativeFrontend: true);

        Assert.False(settings.Read().Enabled);
        Assert.False(settings.SetEnabled(false).Enabled);
        Assert.Equal(0, store.WriteCount);
        Assert.False(Directory.Exists(Path.GetDirectoryName(installation.Bootstrap)));
    }

    [Fact]
    public void EnableCopiesTheGuiAndRegistersOnlyTheBootstrap()
    {
        using var installation = new TestTrayStartupInstallation();
        var store = new TestTrayStartupRegistrationStore();
        var settings = installation.WindowsSettings(store, nativeFrontend: true);

        Assert.True(settings.SetEnabled(true).Enabled);
        Assert.Equal(WindowsTrayStartupSettings.BuildRunCommand(installation.Bootstrap, installation.WindowsCli), store.Value);
        Assert.Equal(File.ReadAllBytes(installation.SourceGui), File.ReadAllBytes(installation.Bootstrap));
        var timestamp = File.GetLastWriteTimeUtc(installation.Bootstrap);
        Assert.True(settings.SetEnabled(true).Enabled);
        Assert.Equal(1, store.WriteCount);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(installation.Bootstrap));

        var reopened = installation.WindowsSettings(store, nativeFrontend: true);
        Assert.True(reopened.Read().Enabled);
        Assert.False(reopened.SetEnabled(false).Enabled);
        Assert.Null(store.Value);
        Assert.True(File.Exists(installation.Bootstrap));
    }

    [Fact]
    public void DisableDoesNotRequireTheOriginalCliOrNativeDevelopmentHost()
    {
        using var installation = new TestTrayStartupInstallation();
        var store = new TestTrayStartupRegistrationStore();
        installation.WindowsSettings(store, nativeFrontend: true).SetEnabled(true);
        File.Delete(installation.WindowsCli);
        var settings = installation.WindowsSettings(store, nativeFrontend: false);

        Assert.True(settings.Read().Enabled);
        Assert.False(settings.Read().CanEnable);
        Assert.False(settings.SetEnabled(false).Enabled);
        Assert.Null(store.Value);
    }

    [Theory]
    [InlineData("foreign.exe --run")]
    [InlineData("")]
    public void UnrelatedRunValueIsNeverChanged(string registration)
    {
        using var installation = new TestTrayStartupInstallation();
        var store = new TestTrayStartupRegistrationStore { Value = registration };
        var settings = installation.WindowsSettings(store, nativeFrontend: true);

        Assert.Throws<InvalidOperationException>(settings.Read);
        Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(false));
        Assert.Equal(registration, store.Value);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public void ExternalEditBetweenReadAndToggleIsNotOverwritten()
    {
        using var installation = new TestTrayStartupInstallation();
        var store = new TestTrayStartupRegistrationStore();
        var settings = installation.WindowsSettings(store, nativeFrontend: true);
        settings.Read();
        store.Value = WindowsTrayStartupSettings.BuildRunCommand(installation.Bootstrap, Path.Combine(installation.Root, "different.exe"));

        Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(true));
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public void StoreRechecksAnExternalEditImmediatelyBeforeWrite()
    {
        using var installation = new TestTrayStartupInstallation();
        var store = new TestTrayStartupRegistrationStore();
        var settings = installation.WindowsSettings(store, nativeFrontend: true);
        settings.Read();
        store.BeforeWrite = () => store.Value = "external";

        Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(true));
        Assert.Equal("external", store.Value);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public void UnownedOrModifiedBootstrapIsPreserved()
    {
        using var installation = new TestTrayStartupInstallation();
        Directory.CreateDirectory(Path.GetDirectoryName(installation.Bootstrap)!);
        File.Copy(installation.SourceGui, installation.Bootstrap);
        var original = File.ReadAllBytes(installation.Bootstrap);
        var store = new TestTrayStartupRegistrationStore();
        var settings = installation.WindowsSettings(store, nativeFrontend: true);

        Assert.False(settings.Read().CanEnable);
        Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(true));
        Assert.Equal(original, File.ReadAllBytes(installation.Bootstrap));
        Assert.Null(store.Value);
    }

    [Fact]
    public void ManagedGuiCannotBeCopiedAsTheBootstrap()
    {
        using var installation = new TestTrayStartupInstallation();
        File.WriteAllText(Path.ChangeExtension(installation.SourceGui, ".runtimeconfig.json"), "{}");
        var store = new TestTrayStartupRegistrationStore();
        var settings = installation.WindowsSettings(store, nativeFrontend: true);

        Assert.False(settings.Read().CanEnable);
        Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(true));
        Assert.False(File.Exists(installation.Bootstrap));
        Assert.Null(store.Value);
    }

    [Fact]
    public void EnvironmentVariableDelimitersCannotChangeTheRegisteredPath()
    {
        using var installation = new TestTrayStartupInstallation();
        var cli = Path.Combine(installation.Root, "%INSTALL%.exe");
        File.Copy(installation.WindowsCli, cli);
        var options = installation.Options(windows: true) with { CliPath = cli, StartupCliPath = cli };
        var store = new TestTrayStartupRegistrationStore();
        var settings = new WindowsTrayStartupSettings(options, true, installation.SourceGui, installation.Bootstrap, store);

        Assert.False(settings.Read().CanEnable);
        Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(true));
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public void BootstrapRemainsStableAcrossBackendUpgradesAndCanBeDisabledAfterExternalChanges()
    {
        using var installation = new TestTrayStartupInstallation();
        var store = new TestTrayStartupRegistrationStore();
        var settings = installation.WindowsSettings(store, nativeFrontend: true);
        settings.SetEnabled(true);
        var original = File.ReadAllBytes(installation.Bootstrap);
        using (var source = new FileStream(installation.SourceGui, FileMode.Append, FileAccess.Write))
        {
            source.WriteByte(1);
        }
        settings.SetEnabled(false);
        settings.SetEnabled(true);
        Assert.Equal(original, File.ReadAllBytes(installation.Bootstrap));

        File.AppendAllText(installation.Bootstrap, "external edit");
        Assert.False(settings.Read().CanEnable);
        Assert.False(settings.SetEnabled(false).Enabled);
        Assert.Null(store.Value);
    }

    [Fact]
    public void OverlongRunCommandCannotBeEnabled()
    {
        using var installation = new TestTrayStartupInstallation();
        var store = new TestTrayStartupRegistrationStore();
        // The root is much shorter on Linux runners than on macOS. Make the command
        // exceed the Run key limit independently of the machine's temporary path.
        var bootstrap = Path.Combine(installation.Root, new string('a', 150), new string('b', 150), "aspire-tray-login.exe");
        var settings = new WindowsTrayStartupSettings(installation.Options(windows: true), true, installation.SourceGui, bootstrap, store);

        Assert.False(settings.Read().CanEnable);
        Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(true));
        Assert.Equal(0, store.WriteCount);
    }

    [Theory]
    [InlineData(260, true)]
    [InlineData(261, false)]
    public void RunCommandLengthLimitIsExact(int length, bool canEnable)
    {
        using var installation = new TestTrayStartupInstallation();
        var originalLength = WindowsTrayStartupSettings.BuildRunCommand(installation.Bootstrap, installation.WindowsCli).Length;
        Assert.True(originalLength < 260);
        var cli = Path.Combine(installation.Root, new string('a', length - originalLength) + Path.GetFileName(installation.WindowsCli));
        File.Copy(installation.WindowsCli, cli);
        var options = installation.Options(windows: true) with { CliPath = cli, StartupCliPath = cli };
        var store = new TestTrayStartupRegistrationStore();
        var settings = new WindowsTrayStartupSettings(options, true, installation.SourceGui, installation.Bootstrap, store);

        Assert.Equal(length, WindowsTrayStartupSettings.BuildRunCommand(installation.Bootstrap, cli).Length);
        Assert.Equal(canEnable, settings.Read().CanEnable);
        if (canEnable)
        {
            Assert.True(settings.SetEnabled(true).Enabled);
            Assert.Equal(length, store.Value!.Length);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(true));
            Assert.Equal(0, store.WriteCount);
        }
    }

    [Fact]
    public void BootstrapUsesOnlyExplicitTrayStartArgumentsWithoutAConsole()
    {
        using var installation = new TestTrayStartupInstallation();
        var start = WindowsLoginBootstrap.CreateStartInfo(installation.WindowsCli);

        Assert.Equal(installation.WindowsCli, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(["tray", "start", "--non-interactive", "--nologo"], start.ArgumentList);
    }
}
