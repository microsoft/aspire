// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.Versioning;
using Aspire.Shared;
using Aspire.Tests.Utils;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class LinuxTrayTests
{
    public static bool SupportsLinux => OperatingSystem.IsLinux();

    [Fact(Skip = "Uses Unix absolute path syntax.", SkipUnless = nameof(SupportsLinux))]
    public Task AutostartEscapesDesktopArgumentsAndFieldCodes()
        => Verifier.Verify(LinuxTrayStartupSettings.Serialize("/opt/Aspire \"A & B\"/$100%`back\\slash/aspire"), "txt")
            .UseDirectory("Snapshots");

    [Fact]
    public void MenuActionsRetainExactIdentity()
    {
        var id = new AppHostId(Path.GetFullPath("Example.AppHost.csproj"), 42, 1000);
        var host = new AppHostMenuItem(id, "Example", "PID 42", "Example", true, true, false, null)
        {
            Health = AppHostHealth.Healthy
        };
        var state = new TrayViewState(DiscoveryState.Live, [host], "One AppHost");
        var menu = LinuxTrayMenu.Build(state, confirmStop: true, codeAvailable: true);
        var row = menu[0];
        Assert.Equal("Example - Healthy", row.Label);
        Assert.Equal(LinuxTrayAction.None, row.Action);
        Assert.Same(host, row.Host);
        var actions = row.Children!.Where(item => item.Action is not (LinuxTrayAction.None or LinuxTrayAction.Separator)).ToArray();
        Assert.Equal([LinuxTrayAction.Dashboard, LinuxTrayAction.Stop, LinuxTrayAction.TogglePin,
            LinuxTrayAction.Folder, LinuxTrayAction.CopyPath, LinuxTrayAction.Code], actions.Select(item => item.Action));
        Assert.All(actions, action => Assert.Equal(id, action.Host!.Id));
        Assert.Equal("Stop AppHost...", actions[1].Label);
        Assert.Equal(LinuxTrayIcon.Healthy, row.Icon);
        Assert.Equal(LinuxTrayIcon.Documentation, menu.Single(item => item.Action == LinuxTrayAction.Documentation).Icon);
        Assert.Equal(LinuxTrayIcon.Settings, menu.Single(item => item.Action == LinuxTrayAction.Settings).Icon);
    }

    [Theory]
    [InlineData("Healthy", "Healthy")]
    [InlineData("Warning", "Waiting / degraded")]
    [InlineData("Unhealthy", "Unhealthy")]
    [InlineData("Unknown", "Health unknown")]
    public void HealthIsExposedAsTextWithoutDependingOnHostIconSupport(string health, string expected)
    {
        var host = new AppHostMenuItem(new(Path.GetFullPath("apphost.cs"), 1, 2),
            "Example", "PID 1", "Example", true, true, false, null) { Health = Enum.Parse<AppHostHealth>(health) };
        var menu = LinuxTrayMenu.Build(new(DiscoveryState.Live, [host], ""), true, false);
        Assert.Equal($"Example - {expected}", menu[0].Label);
        Assert.Equal(Enum.Parse<LinuxTrayIcon>(health), menu[0].Icon);
    }

    [Fact]
    public void OfflinePinsHaveExplicitStartAndDisconnectedActionsStayDisabled()
    {
        var host = new AppHostMenuItem(new(Path.GetFullPath("apphost.cs"), 0, null),
            "Example", "Stopped", "Example", false, false, false, null)
        {
            IsRunning = false, IsPinned = true, CanStart = true
        };
        var state = new TrayViewState(DiscoveryState.Live, [host], "");
        var row = LinuxTrayMenu.Build(state, true, false)[0];
        Assert.Equal(LinuxTrayAction.None, row.Action);
        Assert.Equal(LinuxTrayAction.Start, row.Children![0].Action);
        Assert.True(row.Children[0].Enabled);
        Assert.Equal(LinuxTrayIcon.Stopped, row.Icon);
        Assert.Equal("Unpin AppHost", row.Children[1].Label);
        var disconnected = state with { Discovery = DiscoveryState.Disconnected, AppHosts = [host with { CanStart = false }] };
        row = LinuxTrayMenu.Build(disconnected, true, false)[0];
        Assert.Equal("Example - Discovery unavailable", row.Label);
        Assert.False(row.Children![0].Enabled);
        Assert.Equal(LinuxTrayIcon.Warning, row.Icon);
    }

    [Theory]
    [InlineData(true, false, null, "Warning")]
    [InlineData(false, true, null, "Warning")]
    [InlineData(false, false, "Stop failed", "Unhealthy")]
    public void PendingActionsAndErrorsOverrideHealthyIcons(bool starting, bool stopping, string? error, string expected)
    {
        var host = new AppHostMenuItem(new(Path.GetFullPath("apphost.cs"), 1, 2),
            "Example", "PID 1", "Example", true, true, stopping, error)
        {
            Health = AppHostHealth.Healthy, IsStarting = starting
        };
        var row = LinuxTrayMenu.Build(new(DiscoveryState.Live, [host], ""), true, false)
            .Single(item => item.Host == host);
        Assert.Equal(Enum.Parse<LinuxTrayIcon>(expected), row.Icon);
    }

    [Fact]
    public void AutostartIsOptInAndRefusesExternalEdits()
    {
        using var installation = new TestTrayStartupInstallation();
        LinuxTrayTestPayload.Create(installation.Root, "linux-x64");
        var cli = Path.Combine(installation.Root, "aspire-tray");
        var directory = Path.Combine(installation.Root, "autostart");
        var options = new TrayOptions(cli, null, installation.BundleRoot, cli);
        var settings = new LinuxTrayStartupSettings(options, true, directory);
        Assert.False(settings.Read().Enabled);
        Assert.True(settings.Read().CanEnable);
        Assert.False(Directory.Exists(directory));
        Assert.True(settings.SetEnabled(true).Enabled);
        Assert.True(new LinuxTrayStartupSettings(options, true, directory).Read().Enabled);
        var registration = Path.Combine(directory, LinuxTrayStartupSettings.FileName);
        var modified = File.ReadAllText(registration) + "Hidden=true\n";
        File.WriteAllText(registration, modified);
        Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(false));
        Assert.Equal(modified, File.ReadAllText(registration));
    }

    [Fact]
    public void AutostartCanBeDisabledAfterTheInstallationDisappears()
    {
        using var installation = new TestTrayStartupInstallation();
        LinuxTrayTestPayload.Create(installation.Root, "linux-x64");
        var cli = Path.Combine(installation.Root, "aspire-tray");
        var directory = Path.Combine(installation.Root, "autostart");
        var options = new TrayOptions(cli, null, installation.BundleRoot, cli);
        new LinuxTrayStartupSettings(options, true, directory).SetEnabled(true);
        File.Delete(cli);
        var settings = new LinuxTrayStartupSettings(options, true, directory);
        Assert.False(settings.Read().CanEnable);
        Assert.True(settings.Read().Enabled);
        Assert.False(settings.SetEnabled(false).Enabled);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public void LinuxPayloadRejectsWrongArchitectureAndManagedLoaders(string rid)
    {
        using var installation = new TestTrayStartupInstallation();
        LinuxTrayTestPayload.Create(installation.Root, rid);
        LinuxTrayPayload.Validate(installation.Root, rid);
        Assert.Throws<InvalidDataException>(() => LinuxTrayPayload.Validate(installation.Root,
            rid == "linux-x64" ? "linux-arm64" : "linux-x64"));
        File.WriteAllText(Path.Combine(installation.Root, "aspire-tray.runtimeconfig.json"), "{}");
        Assert.Throws<InvalidDataException>(() => LinuxTrayPayload.Validate(installation.Root, rid));
    }

    [Fact(Skip = "Uses Linux flock.", SkipUnless = nameof(SupportsLinux))]
    [SupportedOSPlatform("linux")]
    public void SingletonUsesPrivatePersistentLockAndRejectsLinks()
    {
        using var installation = new TestTrayStartupInstallation();
        var directory = Path.Combine(installation.Root, "runtime");
        using (var first = LinuxTrayRuntime.TryAcquire(directory))
        {
            Assert.NotNull(first);
            Assert.Null(LinuxTrayRuntime.TryAcquire(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "instance.lock")));
        }
        Assert.True(File.Exists(Path.Combine(directory, "instance.lock")));
        using (var next = LinuxTrayRuntime.TryAcquire(directory))
        {
            Assert.NotNull(next);
        }
        var lockPath = Path.Combine(directory, "instance.lock");
        File.Delete(lockPath);
        File.CreateSymbolicLink(lockPath, installation.MacCli);
        Assert.Throws<Win32Exception>(() => LinuxTrayRuntime.TryAcquire(directory));
    }

    [Fact(Skip = "Uses Linux launch paths.", SkipUnless = nameof(SupportsLinux))]
    [SupportedOSPlatform("linux")]
    public void DetachedLaunchPassesPathsAsArguments()
    {
        using var installation = new TestTrayStartupInstallation();
        LinuxTrayTestPayload.Create(Path.Combine(installation.BundleRoot, "tray"), "linux-x64");
        var options = new TrayOptions(installation.MacCli, null, installation.BundleRoot, installation.MacCli);
        var start = LinuxTrayRuntime.CreateStartInfo(options);
        Assert.False(start.UseShellExecute);
        Assert.Equal(Path.Combine(installation.BundleRoot, "tray", "aspire-tray"), start.FileName);
        Assert.Equal(["--detached", "--cli", options.CliPath, "--bundle-root", options.BundleRoot!,
            "--startup-cli", options.StartupCliPath!], start.ArgumentList);
    }
}
