// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aspire.Tray;

internal sealed class NativeSmokeHarness
{
    private readonly SmokeAppHostClient _client = new();
    private readonly AppHostInfo _first = new("/smoke/First/AppHost.cs", 41001, "http://localhost:19001/")
    {
        ProcessStartTimeUnixMilliseconds = 1_700_000_000_001
    };
    private readonly AppHostInfo _second = new("/smoke/Second/AppHost.cs", 41002, null)
    {
        ProcessStartTimeUnixMilliseconds = 1_700_000_000_002
    };
    private readonly AppHostInfo _third = new("/smoke/Third/AppHost.cs", 41003, "http://localhost:19003/")
    {
        ProcessStartTimeUnixMilliseconds = 1_700_000_000_003
    };
    private readonly AppHostInfo _trackingFirst = new("/smoke/TrackingFirst/AppHost.cs", 41004, null)
    {
        ProcessStartTimeUnixMilliseconds = 1_700_000_000_004
    };
    private readonly AppHostInfo _trackingOther = new("/smoke/TrackingOther/AppHost.cs", 41005, null)
    {
        ProcessStartTimeUnixMilliseconds = 1_700_000_000_005
    };
    private readonly CancellationTokenSource _trackingShutdown = new();
    private readonly TrayController _controller;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-tray-smoke-");
    private readonly AppHostInfo _savedHost;
    private MacTrayApplication _application = null!;
    private TrayController? _discovery;
    private int _phase;
    private int _dashboardCalls;
    private int _confirmations;
    private bool _finished;
    private bool _trackingInProgress;
    private bool _trackingRefreshVerified;
    private Task? _trackingWorker;
    private Task? _restoreTask;
    private int _clearConfirmations;
    private int _removeConfirmations;
    private bool _inspectionMode;

    private NativeSmokeHarness()
    {
        _controller = new(_client);
        var path = Path.Combine(_directory.FullName, "Saved.AppHost.cs");
        File.WriteAllText(path, "// Native smoke fixture; never executed.");
        _savedHost = new(path, 41006, null)
        {
            ProcessStartTimeUnixMilliseconds = 1_700_000_000_006,
            Health = AppHostHealth.Healthy
        };
    }

    public static int Run(string cliPath, int seconds) => new NativeSmokeHarness().RunCore(cliPath, seconds);

    private int RunCore(string cliPath, int seconds)
    {
        // The normal tray's single-instance lock is intentionally not acquired: deterministic
        // smoke never uses real AppHost actions and may coexist with the user's running tray.
        using var application = new MacTrayApplication(_controller, "AspireTray.Smoke",
            OpenDashboard, ConfirmStop, ConfirmAction);
        _application = application;
        try
        {
            Require(!application.WaitUntilReadyAsync(_trackingShutdown.Token).IsCompleted,
                "Readiness was acknowledged before the native event loop ran.");
            application.MenuUpdated += Advance;
            _client.StopStarted = application.RequestRefresh;
            _client.StartStarted = application.RequestRefresh;
            application.SetSmokeDeadline(seconds, OnTimeout);
            if (Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_VERIFY_CLI") == "1")
            {
                // Optional real discovery is read-only. It owns a separate watcher, and its
                // identities are never fed into the fake action client or native menu.
                _discovery = new(new CliAppHostClient(cliPath));
                _discovery.Changed += application.RequestRefresh;
                _discovery.Start();
            }
            _controller.Start();
            return application.Run();
        }
        finally
        {
            application.MenuUpdated -= Advance;
            _trackingShutdown.Cancel();
            try
            {
                _trackingWorker?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_trackingShutdown.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Native tracking worker failed ({ex.GetType().Name}).");
            }
            _trackingShutdown.Dispose();
            if (_discovery is not null)
            {
                _discovery.Changed -= application.RequestRefresh;
                _discovery.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            _controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _client.StopStarted = null;
            _directory.Delete(recursive: true);
        }
    }

    private void Advance(TrayViewState state)
    {
        if (_finished)
        {
            return;
        }
        try
        {
            AdvanceCore(state);
        }
        catch (Exception ex)
        {
            FailSmoke(ex);
        }
    }

    private void AdvanceCore(TrayViewState state)
    {
        Require(_application.WaitUntilReadyAsync(_trackingShutdown.Token).IsCompletedSuccessfully,
            "The native event loop did not acknowledge readiness.");
        var menu = _application.InspectMenu();
        Require(menu.AutosaveName == "AspireTray.Smoke", "Smoke must not share the production item's saved placement.");
        Require(menu.HasColorIcon && menu.HasIconOnlyTitle && menu.HasExpectedConnectionBadge && menu.HasNoItemTooltips
            && menu.HasCommandQ && menu.DispatcherSupportsAllModes, "Icon, tooltip, keyboard, or dispatcher contract failed.");
        Require(menu.ItemCount == menu.Rows.Count + 6 + (menu.StatusNotice is null ? 0 : 1), "Unexpected menu structure.");
        if (menu.StatusNotice is not null)
        {
            Require(menu.StatusNotice == state.Status, "Status notice was not updated.");
        }
        Require(menu.Rows.All(row => row.HasActionIcons && row.HasCorrectActions), "Missing native action/icon.");

        if (_trackingInProgress)
        {
            if (menu.IsInMenuTrackingMode
                && state.AppHosts.Single(row => row.Id == _trackingFirst.Id).IsStopping && _client.HasStop(_trackingFirst.Id))
            {
                VerifyRows(state, menu);
                Require(!menu.Rows.Single(row => row.Id == _trackingFirst.Id).CanStop
                    && menu.Rows.Single(row => row.Id == _trackingOther.Id).CanStop,
                    "The native tracked menu did not preserve the other host's Stop control.");
                if (!_trackingRefreshVerified)
                {
                    _application.CompleteRealMenuTrackingForSmoke();
                    _trackingRefreshVerified = true;
                }
            }
            return;
        }

        // Each phase publishes once through the same asynchronous controller used in production.
        // MenuUpdated is raised only by the native main-loop source; no timer polls for progress.
        switch (_phase)
        {
            case 0:
                Require(menu.HasEmptyPlaceholder, "Connecting placeholder missing.");
                _phase++;
                Publish([], DiscoveryState.Live);
                break;
            case 1:
                Require(state.Discovery == DiscoveryState.Live && menu.HasEmptyPlaceholder, "Empty live presentation failed.");
                _phase++;
                Publish([], DiscoveryState.Disconnected);
                break;
            case 2:
                Require(state.Discovery == DiscoveryState.Disconnected && menu.HasEmptyPlaceholder, "Disconnected presentation failed.");
                _phase++;
                Publish([_first, _second, _third]);
                break;
            case 3:
                VerifyRows(state, menu);
                Require(!menu.Rows.Single(row => row.Id == _second.Id).CanOpenDashboard, "Missing dashboard is enabled.");
                _application.PerformDashboardForSmoke(_first.Id);
                Require(_dashboardCalls == 1, "Dashboard did not use the real native callback.");
                _application.PerformStopForSmoke(_first.Id);
                Require(_confirmations == 1 && _client.StopCount == 0, "Cancel sent a stop request.");
                _application.SetTrackingForSmoke(null, open: true);
                _application.SetTrackingForSmoke(_first.Id, open: true);
                _phase++;
                Publish([_third, _first, _second]);
                break;
            case 4:
                Require(menu.Rows.Select(row => row.Id).SequenceEqual(new[] { _first.Id, _second.Id, _third.Id }),
                    "An actively tracked menu was reordered.");
                _phase++;
                _application.PerformStopForSmoke(_first.Id);
                break;
            case 5:
                if (!state.AppHosts.Single(row => row.Id == _first.Id).IsStopping || !_client.HasStop(_first.Id))
                {
                    return;
                }
                VerifyRows(state, menu);
                Require(!menu.Rows.Single(row => row.Id == _first.Id).CanStop
                    && menu.Rows.Where(row => row.Id != _first.Id).All(row => row.CanStop),
                    "Stopping one host disabled another host.");
                _phase++;
                _application.PerformStopForSmoke(_third.Id);
                break;
            case 6:
                if (!state.AppHosts.Single(row => row.Id == _third.Id).IsStopping || !_client.HasStop(_third.Id))
                {
                    return;
                }
                VerifyRows(state, menu);
                Require(menu.Rows.Single(row => row.Id == _second.Id).CanStop, "A second stop disabled the remaining host.");
                _phase++;
                Publish([_third, Replacement(_first), _second]);
                break;
            case 7:
                Require(menu.Rows.Select(row => row.Id).SequenceEqual(new[] { _first.Id, _second.Id, _third.Id }),
                    "A tracked row was replaced.");
                Require(!menu.Rows.Single(row => row.Id == _first.Id).Enabled, "The removed lifetime is still enabled.");
                _application.PerformStaleStopForSmoke(_first.Id);
                Require(_confirmations == 3, "A stale action created a confirmation alert.");
                _phase++;
                _application.PerformStopForSmoke(_second.Id);
                break;
            case 8:
                Require(!menu.Rows.Single(row => row.Id == _second.Id).Enabled, "A lifetime replaced during confirmation is still enabled.");
                Require(_confirmations == 4, "Modal confirmation callback missing.");
                _phase++;
                _client.Complete(_first.Id, new(StopOutcome.Stopped, 0));
                _client.Complete(_third.Id, new(StopOutcome.Failed, 7));
                break;
            case 9:
                if (state.AppHosts.Single(row => row.Id == _third.Id).IsStopping)
                {
                    return;
                }
                var failed = menu.Rows.Single(row => row.Id == _third.Id);
                Require(failed.CanStop && (failed.Subtitle ?? failed.Title).Contains("Unable to stop AppHost", StringComparison.Ordinal),
                    "Per-host failure was not shown or retry was disabled.");
                Require(_client.StoppedIds.ToHashSet().SetEquals([_first.Id, _third.Id]), "Stable identity routed to the wrong AppHost.");
                _phase++;
                _application.SetTrackingForSmoke(null, open: false);
                break;
            case 10:
                Require(menu.Rows.Select(row => row.Id).SequenceEqual(new[] { _first.Id, _second.Id, _third.Id }),
                    "Closing the parent replaced an open submenu.");
                _phase++;
                _application.SetTrackingForSmoke(_first.Id, open: false);
                break;
            case 11:
                Require(menu.Rows.Select(row => row.Id).SequenceEqual(new[] { _third.Id, Replacement(_first).Id, Replacement(_second).Id }),
                    "Deferred menu rebuild did not apply the latest identities/order.");
                VerifyRows(state, menu);
                _phase++;
                Publish([_third, Replacement(_first), Replacement(_second)], DiscoveryState.Disconnected);
                break;
            case 12:
                Require(menu.Rows.All(row => !row.CanOpenDashboard && !row.CanStop), "Disconnected actions are still enabled.");
                VerifyRows(state, menu);
                _phase++;
                Publish([], DiscoveryState.Live);
                break;
            case 13:
                Require(menu.HasEmptyPlaceholder, "Final empty state was not rendered.");
                _phase++;
                Publish([_trackingFirst, _trackingOther]);
                break;
            case 14:
                VerifyRows(state, menu);
                _application.RetainStopSenderForSmoke(_trackingOther.Id);
                _trackingInProgress = true;
                _application.BeginRealMenuTrackingForSmoke(
                    () => _trackingWorker = Task.Run(() => _controller.RequestStop(_trackingFirst.Id), _trackingShutdown.Token),
                    FinishRealTracking);
                break;
            case 15:
                if (state.AppHosts.Single(row => row.Id == _trackingFirst.Id).IsStopping)
                {
                    return;
                }
                VerifyRows(state, menu);
                _phase++;
                Publish([_trackingOther, _trackingFirst]);
                break;
            case 16:
                Require(menu.Rows.Select(row => row.Id).SequenceEqual(new[] { _trackingOther.Id, _trackingFirst.Id })
                    && _application.RetainedStopSenderIsFromPreviousMenuForSmoke(_trackingOther.Id),
                    "The delayed-action test did not replace the closed native menu.");
                _phase++;
                var confirmationsBeforeDelayedAction = _confirmations;
                _application.PerformRetainedStopForSmoke();
                Require(_confirmations == confirmationsBeforeDelayedAction + 1
                    && _controller.State.AppHosts.Single(row => row.Id == _trackingOther.Id).IsStopping,
                    "A valid delayed action lost or changed its selected identity after menuDidClose.");
                break;
            case 17:
                if (!state.AppHosts.Single(row => row.Id == _trackingOther.Id).IsStopping || !_client.HasStop(_trackingOther.Id))
                {
                    return;
                }
                VerifyRows(state, menu);
                Require(menu.Rows.Single(row => row.Id == _trackingFirst.Id).CanStop,
                    "The delayed action stopped or disabled the wrong AppHost.");
                _phase++;
                Publish([Replacement(_trackingOther), _trackingFirst]);
                break;
            case 18:
                var confirmationsBeforeStaleAction = _confirmations;
                _application.PerformRetainedStopForSmoke();
                Require(_confirmations == confirmationsBeforeStaleAction
                    && !_client.HasStop(Replacement(_trackingOther).Id)
                    && _controller.State.AppHosts.Single(row => row.Id == Replacement(_trackingOther).Id).CanStop,
                    "The old native sender was re-targeted to a replacement process lifetime.");
                _application.ReleaseRetainedStopSenderForSmoke();
                _phase++;
                _client.Complete(_trackingOther.Id, new(StopOutcome.Failed, 7));
                Publish([], DiscoveryState.Live);
                break;
            case 19:
                Require(menu.HasEmptyPlaceholder, "The real tracking pass did not clean up.");
                if (_discovery is not null && _discovery.State.Discovery != DiscoveryState.Live)
                {
                    return;
                }
                _phase++;
                _application.HideStatusItemForSmoke();
                _application.SetTrackingForSmoke(null, open: true);
                _restoreTask = _application.RestoreIconAsync(_trackingShutdown.Token);
                // WaitAsync's completion can run after the UI has finished restoration.
                // Resume inspection from that acknowledgement, not from the same refresh.
                _restoreTask.ConfigureAwait(false).GetAwaiter().OnCompleted(_application.RequestRefresh);
                break;
            case 20:
                Require(!menu.IsStatusItemVisible && _restoreTask is { IsCompleted: false },
                    "Restoring the icon mutated a tracked native menu or acknowledged before restoration.");
                _phase++;
                _application.SetTrackingForSmoke(null, open: false);
                break;
            case 21:
                if (_restoreTask is { IsCompleted: false })
                {
                    return;
                }
                var restoredPlacement = _application.HasRestoredPlacementForSmoke();
                Require(menu.IsStatusItemVisible && _restoreTask is { IsCompletedSuccessfully: true }
                    && restoredPlacement,
                    $"Icon restoration incomplete: visible={menu.IsStatusItemVisible}, acknowledgement={_restoreTask?.Status}, recoveryPreference={restoredPlacement}.");
                _application.VerifyInformationItemsForSmoke();
                _application.VerifyStatusArtworkForSmoke();
                _controller.ClearRecent();
                _phase++;
                Publish([_savedHost]);
                break;
            case 22:
                VerifyRows(state, menu);
                Require(menu.Rows.Single().Health == AppHostHealth.Healthy && menu.StatusNotice is null,
                    "Healthy resources should show a checkmark without a redundant header.");
                _application.PerformPinForSmoke(_savedHost.AppHostPath, useContextMenu: true);
                _application.SetTrackingForSmoke(null, open: true);
                _phase++;
                Publish([_savedHost with { Health = AppHostHealth.Warning }]);
                break;
            case 23:
                Require(menu.Rows.Single() is { Health: AppHostHealth.Warning, PinTitle: "Unpin AppHost" },
                    "Waiting resource state or pin action did not update during menu tracking.");
                _phase++;
                Publish([_savedHost with { Health = AppHostHealth.Unhealthy }]);
                break;
            case 24:
                Require(menu.Rows.Single().Health == AppHostHealth.Unhealthy, "Failed resources did not update the native status icon.");
                _application.SetTrackingForSmoke(null, open: false);
                _phase++;
                Publish([]);
                break;
            case 25:
                Require(menu.Rows.Single() is { Health: AppHostHealth.Unknown, CanStart: true, CanStop: false },
                    "An offline pin should remain available with a neutral icon and explicit Start.");
                Require(menu.RecentPaths.Count == 0, "Pinned AppHost was also listed in Open Recent.");
                _phase++;
                _application.PerformStartForSmoke(_savedHost.AppHostPath);
                break;
            case 26:
                if (_client.StartCount == 0)
                {
                    return;
                }
                _phase++;
                Publish([_savedHost]);
                break;
            case 27:
                Require(menu.Rows.Single() is { Health: AppHostHealth.Healthy, CanStop: true, CanStart: false },
                    "A started pin did not become a live row.");
                Require(menu.RecentPaths.Count == 0, "A running AppHost appeared in Open Recent.");
                _phase++;
                _application.PerformPinForSmoke(_savedHost.AppHostPath, useContextMenu: false);
                Publish([]);
                break;
            case 28:
                Require(menu.Rows.Count == 0 && menu.RecentPaths.Contains(_savedHost.AppHostPath),
                    "A stopped unpinned AppHost was not retained in Open Recent.");
                var recentBeforeCancel = menu.RecentPaths.ToArray();
                _application.PerformClearRecentForSmoke();
                Require(_controller.State.RecentAppHosts.Select(host => host.Id.AppHostPath).SequenceEqual(recentBeforeCancel),
                    "Cancel cleared recent history.");
                _phase++;
                _application.PerformClearRecentForSmoke();
                break;
            case 29:
                Require(menu.RecentPaths.Count == 0 && !menu.CanClearRecent && _clearConfirmations == 2,
                    "Confirmed Clear did not empty recent history.");
                _phase++;
                Publish([_first]);
                Publish([]);
                break;
            case 30:
                Require(menu.RecentPaths.Contains(_first.AppHostPath), "Missing-path recent fixture was not listed.");
                _phase++;
                _application.PerformStartForSmoke(_first.AppHostPath);
                break;
            case 31:
                Require(_removeConfirmations == 1 && menu.RecentPaths.Count == 0 && _client.StartCount == 1,
                    "Missing recent AppHost was not removed safely, or attempted a launch.");
                _phase++;
                Publish([_savedHost]);
                _controller.SetPinned(_savedHost.AppHostPath, true);
                Publish([]);
                break;
            case 32:
                Require(menu.Rows.Single() is { CanStart: true, PinTitle: "Unpin AppHost" },
                    "Missing-pin removal fixture was not initially available.");
                File.Delete(_savedHost.AppHostPath);
                _phase++;
                _application.SetTrackingForSmoke(null, open: true);
                _application.RequestRefresh();
                break;
            case 33:
                Require(state.AppHosts.Count == 0 && state.RecentAppHosts.Count == 0 && !menu.Rows.Single().Enabled,
                    "A missing pin was not removed, or its stale tracked action remained enabled.");
                _phase++;
                _application.SetTrackingForSmoke(null, open: false);
                break;
            case 34:
                Require(menu.Rows.Count == 0 && menu.RecentPaths.Count == 0 && menu.HasEmptyPlaceholder,
                    "A deleted pin remained in the native menu or returned through recent history.");
                File.WriteAllText(_savedHost.AppHostPath, "// Native smoke fixture; never executed.");
                var warning = CreatePreviewHost(_first, AppHostHealth.Warning);
                var unhealthy = CreatePreviewHost(_second, AppHostHealth.Unhealthy);
                var notStarted = CreatePreviewHost(new("/preview/Not started/apphost.cs", 41007, null)
                {
                    ProcessStartTimeUnixMilliseconds = 1_700_000_000_007
                }, AppHostHealth.Unknown);
                _phase++;
                Publish([_savedHost, warning, unhealthy, notStarted]);
                _controller.SetPinned(notStarted.AppHostPath, true);
                Publish([_savedHost, warning, unhealthy]);
                break;
            case 35:
                VerifyRows(state, menu);
                Require(menu.Rows.Count == 4 && state.AppHosts.Single(host => host.Title == "Not started")
                    is { IsRunning: false, CanStart: true, Health: AppHostHealth.Unknown },
                    "The preview must include a stopped pin with a white status icon.");
                _finished = true;
                Console.WriteLine($"Native smoke passed: 36 phases; supplied tray assets at 1x/2x with preserved alpha; lower-right purple badge with transparent border; solid status circles; white not-started preview example; context pin/unpin; missing-pin pruning; explicit start; filtered recents; safe clear/remove dialogs; top-level Documentation/About; standard Stop contrast; identity and tracking regressions; CLI discovery: {(_discovery is null ? "not requested" : "live")}.");
                if (Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_INTERACTIVE") == "1")
                {
                    _inspectionMode = true;
                    _application.EnableInteractiveSmoke();
                    Console.WriteLine("Native smoke inspection is ready with fake AppHosts. Use Quit Aspire to close it.");
                    break;
                }
                _application.FinishSmoke(success: true);
                break;
        }
    }

    private AppHostInfo CreatePreviewHost(AppHostInfo host, AppHostHealth health)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory.FullName, AppHostPresentation.GetDisplayName(host)));
        var path = Path.Combine(directory.FullName, "apphost.cs");
        File.WriteAllText(path, "// Native smoke fixture; never executed.");
        return host with { AppHostPath = path, Health = health };
    }

    private void FinishRealTracking(bool deadlineFired)
    {
        _trackingInProgress = false;
        if (_finished)
        {
            return;
        }
        try
        {
            Require(deadlineFired, "The real menu closed before its scheduled main-thread callback.");
            Require(_trackingWorker is { IsCompletedSuccessfully: true } && _trackingRefreshVerified,
                "No worker-published per-host update was rendered while the real menu was tracking.");
            Console.WriteLine("Real native menu tracking verified: worker progress rendered in NSEventTrackingRunLoopMode; other Stop enabled; menu auto-closed.");
            _phase++;
            _client.Complete(_trackingFirst.Id, new(StopOutcome.Failed, 7));
        }
        catch (Exception ex)
        {
            FailSmoke(ex);
        }
    }

    private void FailSmoke(Exception exception)
    {
        _finished = true;
        Console.Error.WriteLine($"Native smoke failed at phase {_phase} ({exception.GetType().Name}): {exception.Message}");
        _application.FinishSmoke(success: false);
    }

    private static void VerifyRows(TrayViewState state, NativeMenuInspection menu)
    {
        foreach (var native in menu.Rows)
        {
            var row = state.AppHosts.Single(host => host.Id == native.Id);
            Require(native.Title == (native.Subtitle is null ? $"{row.Title} - {row.Subtitle}" : row.Title)
                && (native.Subtitle is null || native.Subtitle == row.Subtitle), "Native title/subtitle mismatch.");
            Require(native.AccessibilityLabel.StartsWith($"{row.DisplayName}, ", StringComparison.Ordinal)
                && native.AccessibilityLabel.EndsWith($", {row.Subtitle}, AppHost actions", StringComparison.Ordinal),
                "Accessibility label mismatch.");
            Require(native.CanOpenDashboard == row.CanOpenDashboard && native.CanStop == row.CanStop
                && native.StopTitle == (row.IsStopping ? "Stopping AppHost..." : "Stop AppHost\u2026"), "Native action state mismatch.");
        }
    }

    private void OpenDashboard(Uri uri)
    {
        Require(uri == _first.DashboardUri, "Dashboard callback used the wrong identity.");
        _dashboardCalls++;
    }

    private bool ConfirmStop(StopConfirmation confirmation)
    {
        Require(confirmation.ButtonCount == 2 && confirmation.CancelIsDefault
            && confirmation.StopRequiresExplicitChoice && confirmation.HasColorIcon && confirmation.HasStandardButtonContrast,
            "The real NSAlert has an unsafe default or missing icon.");
        _confirmations++;
        if (_confirmations == 1)
        {
            return false;
        }
        if (confirmation.AppHost == _second.Id)
        {
            // Acknowledge the replacement on the watcher's thread before returning from the
            // actual confirmation hook. RequestStop must reject the old lifetime afterwards.
            Publish([_third, Replacement(_first), Replacement(_second)]);
        }
        return true;
    }

    private bool ConfirmAction(TrayConfirmation confirmation)
    {
        Require(confirmation.CancelIsDefault && confirmation.ActionRequiresExplicitChoice, "Saved-state dialog has an unsafe default.");
        if (confirmation.ActionTitle == "Clear")
        {
            Require(confirmation.Message == "Do you want to clear all recently opened AppHosts?"
                && confirmation.Detail == "This action is irreversible.\n\nPinned AppHosts will be kept.", "Clear confirmation text mismatch.");
            return ++_clearConfirmations > 1;
        }
        Require(confirmation.ActionTitle == "Remove", "Unexpected native confirmation.");
        _removeConfirmations++;
        return true;
    }

    private void Publish(IReadOnlyList<AppHostInfo> hosts, DiscoveryState discovery = DiscoveryState.Live)
        => _client.Publish(new(hosts, discovery));

    private static AppHostInfo Replacement(AppHostInfo host)
        => host with { ProcessStartTimeUnixMilliseconds = host.ProcessStartTimeUnixMilliseconds + 100 };

    private void OnTimeout()
    {
        if (_inspectionMode)
        {
            return;
        }
        if (!_finished)
        {
            _finished = true;
            Console.Error.WriteLine($"Native smoke deadline reached at phase {_phase}; CLI discovery: {_discovery?.State.Discovery.ToString() ?? "not requested"}.");
            _application.FinishSmoke(success: false);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class SmokeAppHostClient : IAppHostClient
    {
        private readonly Channel<Publication> _snapshots = Channel.CreateUnbounded<Publication>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        private readonly ConcurrentDictionary<AppHostId, TaskCompletionSource<StopResult>> _stops = new();

        public int StopCount => _stops.Count;
        public IEnumerable<AppHostId> StoppedIds => _stops.Keys;
        public Action? StopStarted { get; set; }
        public Action? StartStarted { get; set; }
        public int StartCount;
        public bool HasStop(AppHostId id) => _stops.ContainsKey(id);

        public void Publish(AppHostSnapshot snapshot)
        {
            var publication = new Publication(snapshot, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Require(_snapshots.Writer.TryWrite(publication), "Could not publish smoke discovery.");
            // A bounded acknowledgement, not UI polling. The controller starts its watcher on
            // a worker, so it can accept a lifetime replacement while the UI is in confirmation.
            Require(publication.Applied.Task.Wait(TimeSpan.FromSeconds(3)), "The smoke discovery worker did not acknowledge a snapshot.");
        }

        public async IAsyncEnumerable<AppHostSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var publication in _snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return publication.Snapshot;
                publication.Applied.TrySetResult();
            }
        }

        public Task<StopResult> StopAsync(AppHostId id, CancellationToken cancellationToken)
        {
            var pending = new TaskCompletionSource<StopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Require(_stops.TryAdd(id, pending), "Duplicate fake stop request.");
            StopStarted?.Invoke();
            return pending.Task.WaitAsync(cancellationToken);
        }

        public Task<StartResult> StartAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StartCount);
            StartStarted?.Invoke();
            return Task.FromResult(new StartResult(StartOutcome.Started, 0));
        }

        public void Complete(AppHostId id, StopResult result)
        {
            Require(_stops.TryGetValue(id, out var pending), "The expected exact-instance stop was not dispatched.");
            pending!.TrySetResult(result);
        }

        private sealed record Publication(AppHostSnapshot Snapshot, TaskCompletionSource Applied);
    }
}
