// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using Aspire.Shared;
using Aspire.Tests.Utils;
using Aspire.Tray.Tests.Helpers;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Tray.Tests;

public class LinuxTrayTests
{
    public static bool SupportsLinux => OperatingSystem.IsLinux();

    [Theory]
    [InlineData("GNOME", "gnome", true)]
    [InlineData("ubuntu:GNOME", "ubuntu", true)]
    [InlineData("gnome-classic", null, true)]
    [InlineData(null, "GNOME", true)]
    [InlineData("KDE", "gnome", false)]
    [InlineData("XFCE", "xfce", false)]
    [InlineData("sway", "sway", false)]
    [InlineData("COSMIC", "cosmic", false)]
    [InlineData(null, null, false)]
    public void OnlyGnomeKeepsTheNativeMenu(string? desktop, string? session, bool nativeMenu)
    {
        Assert.Equal(nativeMenu, LinuxDesktopEnvironment.IsGnome(desktop, session));
    }

    [Theory]
    [InlineData("Typical")]
    [InlineData("Empty")]
    [InlineData("RecentOnly")]
    [InlineData("Disconnected")]
    [InlineData("Errors")]
    public Task NativeMenuUsesTheSharedPanelHierarchy(string sample)
    {
        var state = sample switch
        {
            "Typical" => TrayPanelSamples.Typical(),
            "Empty" => TrayPanelSamples.Empty() with { RecentAppHosts = [], CanClearRecent = false },
            "RecentOnly" => TrayPanelSamples.Empty(),
            "Disconnected" => TrayPanelSamples.Disconnected(),
            "Errors" => TrayPanelSamples.Errors(),
            _ => throw new ArgumentOutOfRangeException(nameof(sample))
        };
        var description = new StringBuilder();
        Append(LinuxTrayMenu.Build(state, []), 0);

        return Verifier.Verify(description.ToString(), "txt").UseDirectory("Snapshots").UseParameters(sample);

        void Append(IReadOnlyList<LinuxTrayMenuItem> items, int depth)
        {
            foreach (var item in items)
            {
                description.Append(' ', depth * 2);
                description.Append(item.Action == LinuxTrayAction.Separator ? "---"
                    : $"{item.Label.Replace('\\', '/')}{(item.Enabled ? "" : " [disabled]")}");
                description.AppendLine();
                if (item.Children is not null)
                {
                    Append(item.Children, depth + 1);
                }
            }
        }
    }

    [Fact]
    public void SectionsGroupByInstanceWithoutDuplicatingRunningPins()
    {
        var offline = TrayPanelSamples.Offline(Path.GetFullPath("Offline.AppHost/apphost.cs"));
        var running = TrayPanelSamples.Running(Path.GetFullPath("Running.AppHost/apphost.cs"), 42) with { IsPinned = true };
        var state = new TrayViewState(DiscoveryState.Live, [offline, running], "One AppHost");

        var menu = LinuxTrayMenu.Build(state, []);

        Assert.Equal([running, offline], menu.Where(item => item.Host is not null).Select(item => item.Host));
        Assert.Equal(["RUNNING", "PINNED"], menu.Where(item => item.Label is "RUNNING" or "PINNED").Select(item => item.Label));
        Assert.All(menu.Where(item => item.Label is "RUNNING" or "PINNED"), item =>
        {
            Assert.False(item.Enabled);
            Assert.Equal(LinuxTrayAction.None, item.Action);
            Assert.Null(item.Children);
        });
        Assert.Equal([LinuxTrayAction.Settings, LinuxTrayAction.Documentation, LinuxTrayAction.Quit],
            menu.TakeLast(3).Select(item => item.Action));
    }

    [Theory]
    [InlineData(500, 15, 320, 34)]
    [InlineData(20, 15, 4, 34)]
    [InlineData(980, 15, 636, 34)]
    [InlineData(500, 820, 320, 616)]
    public void FlyoutPlacementFollowsTheIconAndStaysInsideTheWorkarea(double anchorX, double anchorY, double x, double y)
    {
        var bounds = LinuxTrayPanel.Place(new(0, 30, 1000, 770), anchorX, anchorY, 180, 1);

        Assert.Equal(new PanelRect(x, y, TrayPanel.Width, 180), bounds);
    }

    [Fact]
    public void FlyoutPlacementSupportsMonitorsWithNegativeCoordinates()
    {
        var bounds = LinuxTrayPanel.Place(new(-1920, 30, 1920, 1050), -900, 15, 300, 1);

        Assert.Equal(new PanelRect(-1080, 34, TrayPanel.Width, 300), bounds);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(96, 1)]
    [InlineData(120, 1.25)]
    [InlineData(139.2, 1.45)]
    [InlineData(163.2, 1.7)]
    [InlineData(192, 2)]
    public void FlyoutScaleUsesGtkFontResolution(double resolution, double expected)
    {
        Assert.Equal(expected, LinuxTrayPanel.GetScale(resolution), precision: 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-96)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDesktopResolutionFailsExplicitly(double resolution)
    {
        Assert.Throws<InvalidOperationException>(() => LinuxTrayPanel.GetScale(resolution));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.45)]
    [InlineData(1.7)]
    [InlineData(2)]
    public void ScaledFlyoutPlacementKeepsMarginsAndFitsTheWorkarea(double scale)
    {
        var workarea = new PanelRect(-1920, 30, 1920, 1050);
        var bounds = LinuxTrayPanel.Place(workarea, -20, workarea.Bottom, 300, scale);

        Assert.Equal(TrayPanel.Width * scale, bounds.Width);
        Assert.Equal(300 * scale, bounds.Height);
        Assert.Equal(workarea.Right - 4 * scale, bounds.Right, precision: 10);
        Assert.Equal(workarea.Bottom - 4 * scale, bounds.Bottom, precision: 10);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.45)]
    [InlineData(1.7)]
    [InlineData(2)]
    public void ScaledAccessibleBoundsAndPointerCoordinatesMatchTheScene(double scale)
    {
        var geometry = new PanelRect(-1920, 30, 1920, 1050);
        var panel = LinuxTrayPanel.Place(geometry, -900, 15, 300, scale);
        var target = new PanelRect(20, 100, 64, 30);
        var control = LinuxPanelAccessibility.PlaceControl(target, panel.X - geometry.X, panel.Y - geometry.Y, scale);

        var (x, y) = LinuxTrayPanel.ToPanelPoint(panel, geometry, control.CenterX, control.CenterY, scale);

        Assert.Equal(target.CenterX, x, precision: 10);
        Assert.Equal(target.CenterY, y, precision: 10);
        Assert.Equal(target.Width * scale, control.Width);
        Assert.Equal(target.Height * scale, control.Height);
        Assert.True(target.Contains(x, y));
    }

    [Theory]
    [InlineData(0xFF52u, false, "Up")]
    [InlineData(0xFF54u, false, "Down")]
    [InlineData(0xFF09u, false, "Tab")]
    [InlineData(0xFF09u, true, "ShiftTab")]
    [InlineData(0xFE20u, false, "ShiftTab")]
    [InlineData(0xFF0Du, false, "Enter")]
    [InlineData(0xFF8Du, false, "Enter")]
    [InlineData(0x20u, false, "Space")]
    [InlineData(0xFF1Bu, false, "Escape")]
    [InlineData(0xFF50u, false, "Home")]
    [InlineData(0xFF57u, false, "End")]
    [InlineData(0x61u, false, null)]
    public void FlyoutKeysUseTheSharedPanelNavigation(uint key, bool shift, string? expected)
    {
        Assert.Equal(expected is null ? null : Enum.Parse<PanelKey>(expected), LinuxTrayPanel.MapKey(key, shift));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReadinessRequiresWatcherRegistrationAndPanelHost(bool watcherConnected, bool hostRegistered)
    {
        await using var controller = new TrayController(new TestAppHostClient());
        using var application = new LinuxTrayApplication(controller, new MemoryTrayStartupSettings());

        application.UpdateAvailability(watcherConnected, hostRegistered);

        Assert.Equal(watcherConnected && hostRegistered, application.IsAvailable);
        Assert.Equal(application.IsAvailable, application.ReadyAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HostLossRevokesAvailabilityWithoutDisconnectingTheWatcher()
    {
        await using var controller = new TrayController(new TestAppHostClient());
        using var application = new LinuxTrayApplication(controller, new MemoryTrayStartupSettings());
        application.UpdateAvailability(watcherConnected: true, hostRegistered: true);
        await application.ReadyAsync(CancellationToken.None);

        application.UpdateAvailability(watcherConnected: true, hostRegistered: false);
        Assert.False(application.IsAvailable);
        var restore = application.RestoreAsync(CancellationToken.None);
        application.ProcessPending();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => restore);
        Assert.Equal("No registered StatusNotifierHost is available for the tray. Enable the desktop's tray support.", error.Message);

        application.UpdateAvailability(watcherConnected: true, hostRegistered: true);
        Assert.True(application.IsAvailable);

        application.UpdateAvailability(watcherConnected: false, hostRegistered: true);
        Assert.False(application.IsAvailable);
    }

    [Fact]
    public async Task PeriodicRefreshPrunesDeletedPinsWithoutDiscoveryUpdates()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(path).SetPinned(path, true));
        var client = new TestAppHostClient();
        await using var controller = new TrayController(client, store);
        var timeProvider = new FakeTimeProvider();
        using var application = new LinuxTrayApplication(controller, new MemoryTrayStartupSettings(), timeProvider);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        var displayed = Assert.IsType<TrayViewState>(application.GetStateForRefresh());
        Assert.True(Assert.Single(displayed.AppHosts).IsPinned);
        File.Delete(path);

        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Null(application.GetStateForRefresh());
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        var refreshed = Assert.IsType<TrayViewState>(application.GetStateForRefresh());

        Assert.NotSame(displayed, refreshed);
        Assert.Empty(refreshed.AppHosts);
        Assert.Empty(new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
        Assert.Null(application.GetStateForRefresh());
        Assert.Empty(client.StartRequests);
    }

    [Fact]
    public async Task PendingShutdownSkipsDialog()
    {
        await using var controller = new TrayController(new TestAppHostClient());
        using var application = new LinuxTrayApplication(controller, new MemoryTrayStartupSettings());
        application.RequestQuit();

        var response = application.RunDialog(42, _ => throw new InvalidOperationException("Dialog must not open during shutdown."));

        Assert.Equal(-6, response);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-6)]
    [InlineData(-7)]
    public async Task DialogResponseIsCanceledWhenShutdownIsRequested(int dialogResponse)
    {
        await using var controller = new TrayController(new TestAppHostClient());
        using var application = new LinuxTrayApplication(controller, new MemoryTrayStartupSettings());

        var response = application.RunDialog(42, _ =>
        {
            // Simulate shutdown arriving during the modal loop, before a GTK timer tick.
            application.RequestQuit();
            return dialogResponse;
        });

        Assert.Equal(-6, response);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-6)]
    [InlineData(-7)]
    public async Task DialogResponseIsPreservedWithoutShutdown(int dialogResponse)
    {
        await using var controller = new TrayController(new TestAppHostClient());
        using var application = new LinuxTrayApplication(controller, new MemoryTrayStartupSettings());

        var response = application.RunDialog(42, _ => dialogResponse);

        Assert.Equal(dialogResponse, response);
    }

    [Fact(Skip = "Uses Unix absolute path syntax.", SkipUnless = nameof(SupportsLinux))]
    public Task AutostartEscapesDesktopArgumentsAndFieldCodes()
        => Verifier.Verify(LinuxTrayStartupSettings.Serialize("/opt/Aspire \"A & B\"/$100%`back\\slash/aspire"), "txt")
            .UseDirectory("Snapshots");

    [Fact]
    public void LiveMenuSurfacesControllerNotices()
    {
        var host = new AppHostMenuItem(new(Path.GetFullPath("apphost.cs"), 1, 2),
            "Example", "PID 1", "Example", true, true, false, null);
        var state = new TrayViewState(DiscoveryState.Live, [host], "One AppHost")
        {
            Notice = "Unable to save AppHost history. The saved file was left unchanged."
        };

        var menu = LinuxTrayMenu.Build(state, []);

        Assert.Equal("Aspire \u00b7 1 running", menu[0].Label);
        Assert.False(menu[0].Enabled);
        Assert.Equal(state.Notice, menu[1].Label);
        Assert.False(menu[1].Enabled);
        Assert.Same(host, Assert.Single(menu, item => item.Host is not null).Host);
    }

    [Fact]
    public void MenuActionsRetainExactIdentity()
    {
        var id = new AppHostId(Path.GetFullPath("Example.AppHost.csproj"), 42, 1000);
        var host = new AppHostMenuItem(id, "Example", "PID 42", "Example", true, true, false, null)
        {
            Health = AppHostHealth.Healthy
        };
        var state = new TrayViewState(DiscoveryState.Live, [host], "One AppHost");
        var application = new LinuxFolderApplication("Visual Studio Code", Path.GetFullPath("code"), ["--"]);
        var menu = LinuxTrayMenu.Build(state, [application]);
        var row = Assert.Single(menu, item => item.Host is not null);
        Assert.Equal("Example \u00b7 Healthy", row.Label);
        Assert.Equal(LinuxTrayAction.None, row.Action);
        Assert.Same(host, row.Host);
        var actions = row.Children!.Where(item => item.Action is not (LinuxTrayAction.None or LinuxTrayAction.Separator)).ToArray();
        Assert.Equal([LinuxTrayAction.Dashboard, LinuxTrayAction.Stop,
            LinuxTrayAction.Folder, LinuxTrayAction.CopyPath, LinuxTrayAction.TogglePin], actions.Select(item => item.Action));
        Assert.All(actions, action => Assert.Equal(id, action.Host!.Id));
        Assert.Equal("Stop", actions[1].Label);
        var openIn = row.Children!.Single(item => item.Label == "Open in");
        var editor = Assert.Single(openIn.Children!);
        Assert.Equal(LinuxTrayAction.OpenIn, editor.Action);
        Assert.Same(application, editor.Application);
        Assert.Equal(id, editor.Host!.Id);
        Assert.Equal(LinuxTrayIcon.Healthy, row.Icon);
        Assert.Equal(LinuxTrayIcon.Documentation, menu.Single(item => item.Action == LinuxTrayAction.Documentation).Icon);
        Assert.Equal(LinuxTrayIcon.Settings, menu.Single(item => item.Action == LinuxTrayAction.Settings).Icon);
    }

    [Fact]
    public void MenuLocationsUseCompactPathsForRunningAndRecentHosts()
    {
        var path = Path.GetFullPath("home/developer/worktrees/worktree-a/Shop.AppHost/apphost.cs");
        var host = new AppHostMenuItem(new(path, 42, 1000), "Shop", "PID 42", "Shop", true, true, false, null);
        var recent = host with { Id = new(path, 0, null), IsRunning = false, CanStart = true };
        var state = new TrayViewState(DiscoveryState.Live, [host], "One AppHost") { RecentAppHosts = [recent] };

        var menu = LinuxTrayMenu.Build(state, []);

        var location = Path.Combine("worktree-a", "Shop.AppHost");
        var row = Assert.Single(menu, item => item.Host is not null);
        Assert.Equal($"{location} \u00b7 PID 42", row.Children![^1].Label);
        var recentRow = Assert.Single(menu.Single(item => item.Label == "Recently opened \u00b7 1").Children!, item => item.Host is not null);
        Assert.Equal(location, recentRow.Children![^1].Label);
        Assert.False(row.Children![^1].Enabled);
        Assert.False(recentRow.Children![^1].Enabled);
        Assert.Equal(path, row.Host!.Id.AppHostPath);
        Assert.Equal(path, recentRow.Host!.Id.AppHostPath);
    }

    [Theory]
    [InlineData("worktree\nbranch/Shop\tAppHost", "worktree branch/Shop AppHost")]
    [InlineData("abcdefghijklmnopqrstuvwxyz/ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstu\u2026EFGHIJKLMNOPQRSTUVWXYZ")]
    public void MenuLocationsAreSingleLineAndMiddleTruncated(string directory, string expected)
    {
        var path = Path.GetFullPath($"{directory}/apphost.cs");
        var host = new AppHostMenuItem(new(path, 42, 1000), "Shop", path, "Shop", true, true, false, null);

        var menu = LinuxTrayMenu.Build(new(DiscoveryState.Live, [host], "One AppHost"), []);

        var row = Assert.Single(menu, item => item.Host is not null);
        Assert.Equal($"{expected.Replace('/', Path.DirectorySeparatorChar)} \u00b7 PID 42", row.Children![^1].Label);
        Assert.Equal(path, row.Host!.Id.AppHostPath);
    }

    [Fact]
    public async Task RemovingOneRecentItemPreservesOtherHistoryAndPins()
    {
        using var directory = new TestTrayStateDirectory();
        var removed = directory.CreateAppHost("removed/apphost.cs");
        var kept = directory.CreateAppHost("kept/apphost.cs");
        var pinned = directory.CreateAppHost("pinned/apphost.cs");
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(removed).Remember(kept).Remember(pinned).SetPinned(pinned, true));
        var client = new TestAppHostClient();
        await using var controller = new TrayController(client, store);
        using var application = new LinuxTrayApplication(controller, new MemoryTrayStartupSettings());
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        var menu = LinuxTrayMenu.Build(controller.State, []);
        var recent = menu.Single(item => item.Label == "Recently opened \u00b7 2");
        var row = recent.Children!.Single(item => item.Host?.Id.AppHostPath == removed);
        var action = row.Children!.Single(item => item.Action == LinuxTrayAction.RemoveRecent);

        application.Execute(action);

        Assert.Equal(kept, Assert.Single(controller.State.RecentAppHosts).Id.AppHostPath);
        Assert.Equal(pinned, Assert.Single(controller.State.AppHosts).Id.AppHostPath);
        Assert.True(File.Exists(removed));
        Assert.Equal([kept, pinned], store.Load().AppHosts.Select(host => host.AppHostPath).Order().ToArray());
        Assert.Empty(client.StartRequests);
        Assert.All(menu.Where(item => item.Host is not null), item =>
            Assert.Equal([LinuxTrayAction.Start, LinuxTrayAction.Separator,
                LinuxTrayAction.Folder, LinuxTrayAction.CopyPath, LinuxTrayAction.TogglePin, LinuxTrayAction.Separator, LinuxTrayAction.None,
                LinuxTrayAction.None], item.Children!.Select(child => child.Action)));
    }

    [Fact]
    public void OpenInListsEveryDiscoveredApplicationForRunningAndRecentHosts()
    {
        var applications = new[]
        {
            new LinuxFolderApplication("Visual Studio Code", Path.GetFullPath("code"), ["--"]),
            new LinuxFolderApplication("Konsole", Path.GetFullPath("konsole"), ["--workdir"])
        };
        var state = TrayPanelSamples.Typical();
        var menu = LinuxTrayMenu.Build(state, applications);
        var rows = menu.Where(item => item.Host is not null)
            .Concat(menu.Single(item => item.Label == "Recently opened \u00b7 2").Children!.Where(item => item.Host is not null));

        Assert.All(rows, row =>
        {
            var openIn = row.Children!.Single(item => item.Label == "Open in");
            Assert.Equal(applications, openIn.Children!.Select(item => item.Application));
            Assert.All(openIn.Children!, item =>
            {
                Assert.Equal(LinuxTrayAction.OpenIn, item.Action);
                Assert.Same(row.Host, item.Host);
            });
        });
        var withoutApplications = LinuxTrayMenu.Build(state, []);
        Assert.Equal([LinuxTrayAction.Dashboard, LinuxTrayAction.Stop,
            LinuxTrayAction.Separator, LinuxTrayAction.Folder, LinuxTrayAction.CopyPath, LinuxTrayAction.TogglePin,
            LinuxTrayAction.Separator, LinuxTrayAction.None, LinuxTrayAction.None],
            withoutApplications.First(item => item.Host is not null).Children!.Select(item => item.Action));
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
        var menu = LinuxTrayMenu.Build(new(DiscoveryState.Live, [host], ""), []);
        var row = Assert.Single(menu, item => item.Host is not null);
        Assert.Equal($"Example \u00b7 {expected}", row.Label);
        Assert.Equal(Enum.Parse<LinuxTrayIcon>(health), row.Icon);
    }

    [Fact]
    public void EmptyAppHostShowsNoResourcesWithANeutralIcon()
    {
        var host = new AppHostMenuItem(new(Path.GetFullPath("apphost.cs"), 1, 2),
            "Example", "PID 1", "Example", true, true, false, null) { Health = AppHostHealth.NoResources };
        var row = Assert.Single(LinuxTrayMenu.Build(new(DiscoveryState.Live, [host], ""), []), item => item.Host is not null);

        Assert.Equal("Example \u00b7 No resources", row.Label);
        Assert.Equal(LinuxTrayIcon.Unknown, row.Icon);
        Assert.Equal("No resources", row.Children![^2].Label);
        Assert.Equal("Example \u00b7 Stopped",
            Assert.Single(LinuxTrayMenu.Build(new(DiscoveryState.Live, [host with { IsRunning = false }], ""), []),
                item => item.Host is not null).Label);
        Assert.Equal("Example \u00b7 Discovery unavailable",
            LinuxTrayMenu.Build(new(DiscoveryState.Disconnected, [host], ""), [])
                .Single(item => item.Host == host).Label);
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
        var row = Assert.Single(LinuxTrayMenu.Build(state, []), item => item.Host is not null);
        Assert.Equal(LinuxTrayAction.None, row.Action);
        Assert.Equal(LinuxTrayAction.Start, row.Children![0].Action);
        Assert.True(row.Children[0].Enabled);
        Assert.Equal(LinuxTrayIcon.Stopped, row.Icon);
        Assert.Equal("Unpin", row.Children.Single(item => item.Action == LinuxTrayAction.TogglePin).Label);
        var disconnected = state with { Discovery = DiscoveryState.Disconnected, AppHosts = [host with { CanStart = false }] };
        row = LinuxTrayMenu.Build(disconnected, []).Single(item => item.Host?.Id == host.Id);
        Assert.Equal("Example \u00b7 Discovery unavailable", row.Label);
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
        var row = LinuxTrayMenu.Build(new(DiscoveryState.Live, [host], ""), [])
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
