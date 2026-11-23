// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aspire.Tray;

internal sealed class NativeSmokeHarness
{
    private readonly SmokeAppHostClient _client = new();
    private readonly MemoryTrayStartupSettings _startupSettings = new();
    private readonly MemoryTraySavedStateStore _savedStateStore = new();
    private readonly List<string> _copiedPaths = [];
    private bool _failCopy;
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
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TrayController _controller;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-tray-smoke-");
    private readonly AppHostInfo _savedHost;
    private MacTrayApplication _application = null!;
    private TrayController? _discovery;
    private int _phase;
    private int _dashboardCalls;
    private int _documentationCalls;
    private bool _finished;
    private Task? _restoreTask;
    private AppHostMenuItem? _staleFirst;
    private bool _inspectionMode;
    private readonly bool _interactiveSmokeRequested;

    private NativeSmokeHarness(bool interactiveSmoke)
    {
        _interactiveSmokeRequested = interactiveSmoke
            || Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_INTERACTIVE") == "1";
        _controller = new(_client, _savedStateStore);
        // Tabs and non-ASCII text must survive copying even though presentation replaces
        // control characters: ".../Saved.\t雪🧪.AppHost.cs" is an ordinary macOS filename.
        var path = Path.Combine(_directory.FullName, "Saved.\t雪🧪.AppHost.cs");
        File.WriteAllText(path, "// Native smoke fixture; never executed.");
        _savedHost = new(path, 41006, null)
        {
            ProcessStartTimeUnixMilliseconds = 1_700_000_000_006,
            Health = AppHostHealth.Healthy
        };
    }

    public static int Run(string cliPath, int seconds, bool interactiveSmoke)
        => new NativeSmokeHarness(interactiveSmoke).RunCore(cliPath, seconds);

    private int RunCore(string cliPath, int seconds)
    {
        // The normal tray's single-instance lock is intentionally not acquired: deterministic
        // smoke never uses real AppHost actions and may coexist with the user's running tray.
        using var application = new MacTrayApplication(_controller, "AspireTray.Smoke",
            _startupSettings, OpenDashboard, CopyPath);
        _application = application;
        try
        {
            Require(!application.WaitUntilReadyAsync(_shutdown.Token).IsCompleted,
                "Readiness was acknowledged before the native event loop ran.");
            application.StateRendered += Advance;
            _client.StopStarted = application.RequestRefresh;
            _client.StartStarted = application.RequestRefresh;
            application.SetSmokeDeadline(seconds, OnTimeout);
            if (Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_VERIFY_CLI") == "1")
            {
                // Optional real discovery is read-only. It owns a separate watcher, and its
                // identities are never fed into the fake action client or the panel.
                _discovery = new(new CliAppHostClient(cliPath));
                _discovery.Changed += application.RequestRefresh;
                _discovery.Start();
            }
            _controller.Start();
            return application.Run();
        }
        finally
        {
            application.StateRendered -= Advance;
            _shutdown.Cancel();
            _shutdown.Dispose();
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
        Require(_application.WaitUntilReadyAsync(_shutdown.Token).IsCompletedSuccessfully,
            "The native event loop did not acknowledge readiness.");
        var panel = _application.InspectPanelForSmoke();
        Require(panel.AutosaveName == "AspireTray.Smoke", "Smoke must not share the production item's saved placement.");
        Require(panel.HasTemplateIcon && panel.HasIconOnlyTitle && panel.HasExpectedConnectionBadge && panel.HasNoMenu
            && panel.DispatcherSupportsAllModes, "Icon, status item, or dispatcher contract failed.");
        var connectionDescription = state.Discovery switch
        {
            DiscoveryState.Connecting => "Connecting to discovery",
            DiscoveryState.Live when !state.HasActiveAppHosts => "No AppHosts running",
            DiscoveryState.Live => TrayIconStatusRules.Get(state) == TrayIconStatus.Attention ? "AppHosts need attention" : "AppHosts running",
            _ => "Discovery unavailable"
        };
        Require(panel.AccessibilityLabel == $"Aspire, {connectionDescription}, {state.Status}",
            "The tray must distinguish idle, running, attention, connecting, and disconnected states for accessibility.");

        // Each phase publishes once through the same asynchronous controller used in production.
        // StateRendered is raised only by the native main-loop source; no timer polls for progress.
        switch (_phase)
        {
            case 0:
                Require(state.Discovery == DiscoveryState.Connecting && panel.HeaderStatus == "Connecting…"
                    && panel.EmptyTitle == "Looking for AppHosts…", "Connecting presentation failed.");
                _phase++;
                Publish([], DiscoveryState.Live);
                break;
            case 1:
                Require(state.Discovery == DiscoveryState.Live && panel.EmptyTitle == "No AppHosts running", "Empty live presentation failed.");
                _phase++;
                Publish([], DiscoveryState.Disconnected);
                break;
            case 2:
                Require(state.Discovery == DiscoveryState.Disconnected && panel.HeaderStatus == "Reconnecting…"
                    && panel.EmptyTitle == "AppHosts unavailable", "Disconnected presentation failed.");
                _phase++;
                Publish([_first, _second, _third]);
                break;
            case 3:
                VerifyRows(state, panel);
                Require(!Row(panel, _second).CanOpenDashboard, "Missing dashboard is enabled.");
                _application.ClickPanelForSmoke(PanelTargetKind.Dashboard, _first.Id);
                Require(_dashboardCalls == 1, "Dashboard did not use the panel's native callback.");
                VerifyCopy(_first.Id, _first.AppHostPath);
                _application.VerifyActionListForSmoke(_second.Id);
                _phase++;
                _application.ClickPanelForSmoke(PanelTargetKind.Stop, _first.Id);
                break;
            case 4:
                if (!state.AppHosts.Single(row => row.Id == _first.Id).IsStopping || !_client.HasStop(_first.Id))
                {
                    return;
                }
                VerifyRows(state, panel);
                Require(!Row(panel, _first).CanStop && panel.Rows.Where(row => row.Id != _first.Id).All(row => row.CanStop),
                    "Stopping one host disabled another host.");
                _phase++;
                // Stop the next one the way VoiceOver does.
                _application.PressPanelForSmoke(PanelTargetKind.Stop, _third.Id);
                break;
            case 5:
                if (!state.AppHosts.Single(row => row.Id == _third.Id).IsStopping || !_client.HasStop(_third.Id))
                {
                    return;
                }
                VerifyRows(state, panel);
                Require(Row(panel, _second).CanStop, "A second stop disabled the remaining host.");
                _staleFirst = state.AppHosts.Single(row => row.Id == _first.Id);
                _phase++;
                Publish([_third, Replacement(_first), _second]);
                break;
            case 6:
                if (!state.AppHosts.Any(row => row.Id == Replacement(_first).Id))
                {
                    return;
                }
                // Stopping isn't confirmed, so nothing stands between a click and the request. An
                // action from a scene drawn before the lifetime was replaced must still be rejected.
                var stopsBeforeStaleAction = _client.StopCount;
                _application.RunPanelActionForSmoke(new(PanelActionKind.Stop, _staleFirst));
                Require(_client.StopCount == stopsBeforeStaleAction, "A stale panel action stopped a replacement lifetime.");
                _phase++;
                _client.Complete(_first.Id, new(StopOutcome.Stopped, 0));
                _client.Complete(_third.Id, new(StopOutcome.Failed, 7));
                break;
            case 7:
                if (state.AppHosts.Single(row => row.Id == _third.Id).IsStopping)
                {
                    return;
                }
                var failed = Row(panel, _third);
                Require(failed.CanStop && failed.Name.Contains("Unable to stop AppHost", StringComparison.Ordinal),
                    "Per-host failure was not shown or retry was disabled.");
                Require(_client.StoppedIds.ToHashSet().SetEquals([_first.Id, _third.Id]), "Stable identity routed to the wrong AppHost.");
                _phase++;
                Publish([_third, Replacement(_first), _second], DiscoveryState.Disconnected);
                break;
            case 8:
                Require(panel.Rows.Count == 3 && panel.Rows.All(row => !row.CanOpenDashboard && !row.CanStop),
                    "Disconnected actions are still enabled.");
                _phase++;
                Publish([], DiscoveryState.Live);
                break;
            case 9:
                Require(panel.EmptyTitle == "No AppHosts running", "Final empty state was not rendered.");
                if (_discovery is not null && _discovery.State.Discovery != DiscoveryState.Live)
                {
                    return;
                }
                _application.VerifyActivationForSmoke();
                _phase++;
                _application.HideStatusItemForSmoke();
                _restoreTask = _application.RestoreIconAsync(_shutdown.Token);
                // WaitAsync's completion can run after the UI has finished restoration.
                // Resume inspection from that acknowledgement, not from the same refresh.
                _restoreTask.ConfigureAwait(false).GetAwaiter().OnCompleted(_application.RequestRefresh);
                break;
            case 10:
                if (_restoreTask is { IsCompleted: false })
                {
                    return;
                }
                var restoredPlacement = _application.HasRestoredPlacementForSmoke();
                Require(panel.IsStatusItemVisible && _restoreTask is { IsCompletedSuccessfully: true } && restoredPlacement,
                    $"Icon restoration incomplete: visible={panel.IsStatusItemVisible}, acknowledgement={_restoreTask?.Status}, recoveryPreference={restoredPlacement}.");
                _application.VerifyStatusArtworkForSmoke();
                _application.ClickPanelForSmoke(PanelTargetKind.Documentation);
                Require(_documentationCalls == 1, "Documentation did not use the panel's native callback.");
                _controller.ClearRecent();
                _phase++;
                Publish([_savedHost]);
                break;
            case 11:
                VerifyRows(state, panel);
                VerifyCopy(_savedHost.Id, _savedHost.AppHostPath);
                Require(Row(panel, _savedHost).Name.Contains("Running", StringComparison.Ordinal) && panel.Notice is null,
                    "Healthy resources should read as running without a notice.");
                _application.PickRowActionForSmoke(_savedHost.Id, PanelTargetKind.TogglePin);
                Require(_controller.State.AppHosts.Single().IsPinned, "Pin from the action list did not persist.");
                _phase++;
                Publish([_savedHost with { Health = AppHostHealth.Warning }]);
                break;
            case 12:
                Require(Row(panel, _savedHost).Name.Contains("Needs attention", StringComparison.Ordinal),
                    "Waiting resources did not update the row.");
                _phase++;
                Publish([]);
                break;
            case 13:
                var offline = state.AppHosts.Single();
                Require(offline is { IsRunning: false, CanStart: true, IsPinned: true }
                    && panel.Rows.Single() is { Section: PanelSection.Pinned, CanStart: true, CanStop: false },
                    "An offline pin should remain available in Pinned with an explicit Start.");
                Require(state.RecentAppHosts.Count == 0, "Pinned AppHost was also listed in Recent.");
                VerifyCopy(offline.Id, _savedHost.AppHostPath);
                _phase++;
                _application.ClickPanelForSmoke(PanelTargetKind.Start, offline.Id);
                break;
            case 14:
                if (_client.StartCount == 0)
                {
                    return;
                }
                _phase++;
                Publish([_savedHost]);
                break;
            case 15:
                Require(panel.Rows.Single() is { Section: PanelSection.Running, CanStop: true, CanStart: false },
                    "A started pin did not become a live row.");
                Require(state.RecentAppHosts.Count == 0, "A running AppHost appeared in Recent.");
                _application.PickRowActionForSmoke(_savedHost.Id, PanelTargetKind.TogglePin);
                _phase++;
                Publish([]);
                break;
            case 16:
                var recent = state.RecentAppHosts.SingleOrDefault(host => host.Id.AppHostPath == _savedHost.AppHostPath);
                Require(state.AppHosts.Count == 0 && recent is not null, "A stopped unpinned AppHost was not retained in Recent.");
                _application.VerifyInaccessibleRecentProjectForSmoke(_savedHost.AppHostPath);
                VerifyCopy(recent!.Id, _savedHost.AppHostPath);
                _failCopy = true;
                var copiedBeforeFailure = _copiedPaths.Count;
                _application.PickRowActionForSmoke(recent.Id, PanelTargetKind.CopyPath);
                Require(_copiedPaths.Count == copiedBeforeFailure && _controller.State.Notice == "Clipboard unavailable in smoke.",
                    "Copy failure must be reported instead of pretending the clipboard was updated.");
                _failCopy = false;
                // Clear asks inline: Cancel keeps the list, Clear empties it.
                _application.ClickPanelForSmoke(PanelTargetKind.ClearRecent);
                Require(_application.InspectPanelForSmoke().Confirmation == PanelConfirmKind.ClearRecent, "Clear did not ask inline.");
                _application.ClickPanelForSmoke(PanelTargetKind.ConfirmCancel);
                Require(_controller.State.RecentAppHosts.Count == 1 && _application.InspectPanelForSmoke().Confirmation is null,
                    "Cancel cleared recent history.");
                _application.ClickPanelForSmoke(PanelTargetKind.ClearRecent);
                var again = _application.InspectPanelForSmoke();
                Require(again.Confirmation == PanelConfirmKind.ClearRecent,
                    $"Clear did not ask inline the second time: {again.Confirmation}; {_application.DescribePanelForSmoke()}");
                _phase++;
                _application.ClickPanelForSmoke(PanelTargetKind.ConfirmAccept);
                break;
            case 17:
                Require(state.RecentAppHosts.Count == 0 && !state.CanClearRecent, "Confirmed Clear did not empty recent history.");
                _phase++;
                Publish([_first]);
                Publish([]);
                break;
            case 18:
                var missing = state.RecentAppHosts.Single(host => host.Id.AppHostPath == _first.AppHostPath);
                // Its source file never existed: Show in Finder and Start both offer to remove it inline
                // instead of launching anything.
                _application.PickRowActionForSmoke(missing.Id, PanelTargetKind.ShowInFolder);
                Require(_application.InspectPanelForSmoke().Confirmation == PanelConfirmKind.RemoveMissing,
                    "Show in Finder on a missing AppHost did not offer to remove it.");
                _application.ClickPanelForSmoke(PanelTargetKind.ConfirmCancel, missing.Id);
                Require(_controller.State.RecentAppHosts.Count == 1, "Cancel removed a missing recent AppHost.");
                _application.ClickPanelForSmoke(PanelTargetKind.Start, missing.Id);
                Require(_application.InspectPanelForSmoke().Confirmation == PanelConfirmKind.RemoveMissing && _client.StartCount == 1,
                    "Start on a missing AppHost did not offer to remove it, or attempted a launch.");
                _phase++;
                _application.ClickPanelForSmoke(PanelTargetKind.ConfirmAccept, missing.Id);
                break;
            case 19:
                Require(state.RecentAppHosts.Count == 0 && _client.StartCount == 1, "Missing recent AppHost was not removed safely.");
                _phase++;
                Publish([_savedHost]);
                _controller.SetPinned(_savedHost.AppHostPath, true);
                Publish([]);
                break;
            case 20:
                Require(state.AppHosts.Single() is { CanStart: true, IsPinned: true }, "Missing-pin removal fixture was not initially available.");
                File.Delete(_savedHost.AppHostPath);
                _phase++;
                _application.ReopenPanelForSmoke();
                break;
            case 21:
                if (state.AppHosts.Count != 0)
                {
                    return;
                }
                Require(state.RecentAppHosts.Count == 0 && panel.EmptyTitle == "No AppHosts running",
                    "A deleted pin remained in the panel or returned through recent history.");
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
            case 22:
                VerifyRows(state, panel);
                Require(panel.Rows.Count == 4 && state.AppHosts.Single(host => host.Title == "Not started")
                    is { IsRunning: false, CanStart: true },
                    "The preview must include a stopped pin.");
                Console.WriteLine("Flyout " + _application.VerifyPanelForSmoke(
                    Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_PANEL_CAPTURE_DIR") is { Length: > 0 } capture ? capture : null) + ".");
                _finished = true;
                Console.WriteLine($"Native smoke passed: 23 phases; every status item activation opens the flyout (left, right, keyboard and VoiceOver with focus, AXPress) and none attaches a menu; Command-comma Settings; panel open/size/Escape and Settings view; action list items, keyboard and VoiceOver; monochrome 1x/2x logo template; idle/running/attention/dimmed status dot; exact Unicode Copy path for live/pinned/recent rows and clipboard failure reporting (injected, real clipboard untouched); unconfirmed Stop by click and AXPress with stale-lifetime safety; pin/unpin from the action list; missing-pin pruning; explicit start; inline Clear and missing-file removal; Documentation; CLI discovery: {(_discovery is null ? "not requested" : "live")}.");
                if (_interactiveSmokeRequested)
                {
                    _inspectionMode = true;
                    _application.EnableInteractiveSmoke();
                    Console.WriteLine("Native smoke inspection is ready with fake AppHosts. Use Quit in the panel to close it.");
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

    private void FailSmoke(Exception exception)
    {
        _finished = true;
        Console.Error.WriteLine($"Native smoke failed at phase {_phase} ({exception.GetType().Name}): {exception.Message}");
        _application.FinishSmoke(success: false);
    }

    /// <summary>Checks that the panel lists every current AppHost once, in its section, with its actions' state.</summary>
    private static void VerifyRows(TrayViewState state, NativePanelInspection panel)
    {
        Require(panel.Rows.Count(row => row.Section != PanelSection.Recent) == state.AppHosts.Count, "The panel must list every AppHost.");
        foreach (var native in panel.Rows.Where(row => row.Section != PanelSection.Recent))
        {
            var row = state.AppHosts.Single(host => host.Id == native.Id);
            Require(native.Section == (row.Id.AppHostPid != 0 ? PanelSection.Running : PanelSection.Pinned), "AppHost listed in the wrong section.");
            Require(native.Name.StartsWith($"{row.DisplayName}, ", StringComparison.Ordinal), "Row accessible name mismatch.");
            Require(native.CanOpenDashboard == row.CanOpenDashboard && native.CanStop == row.CanStop
                && native.CanStart == row.CanStart, "Panel action state mismatch.");
        }
    }

    private static PanelRowInspection Row(NativePanelInspection panel, AppHostInfo host)
        => panel.Rows.Single(row => row.Id.AppHostPath == host.AppHostPath && row.Section != PanelSection.Recent);

    private void OpenDashboard(Uri uri)
    {
        if (uri == new Uri("https://aspire.dev"))
        {
            _documentationCalls++;
            return;
        }
        Require(uri == _first.DashboardUri, "Dashboard callback used the wrong identity.");
        _dashboardCalls++;
    }

    private void CopyPath(string path)
    {
        // Both deterministic and interactive smoke must leave the user's clipboard intact.
        if (_failCopy)
        {
            throw new InvalidOperationException("Clipboard unavailable in smoke.");
        }
        _copiedPaths.Add(path);
    }

    private void VerifyCopy(AppHostId id, string path)
    {
        var count = _copiedPaths.Count;
        _application.PickRowActionForSmoke(id, PanelTargetKind.CopyPath);
        Require(_copiedPaths.Count == count + 1 && _copiedPaths[^1] == Path.GetDirectoryName(path),
            "Copy path did not copy the exact containing folder through the panel.");
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
