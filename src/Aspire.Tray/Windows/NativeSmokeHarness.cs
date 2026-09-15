// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Aspire.Tray;

/// <summary>
/// Exercises real Win32 resources and dispatch with isolated, non-executable AppHost fixtures.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeSmokeHarness
{
    private readonly SmokeClient _client = new();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-tray-smoke-");
    private readonly MemoryTraySavedStateStore _store = new();
    private readonly SmokeStartupSettings _startupSettings = new();
    private readonly bool _interactiveSmoke;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly AppHostInfo[] _hosts;
    private readonly string _pinned;
    private readonly string _recent;
    private readonly string _missing;
    private readonly string _missingPin;
    private TrayController _controller = null!;
    private TrayApplication _application = null!;
    private int _phase;
    private int _lastLoggedPhase = -1;
    private int _dashboardCalls;
    private int _documentationCalls;
    private int _confirmations;
    private int _errors;
    private readonly List<string> _copiedPaths = [];
    private bool _accept;
    private bool _finished;
    private bool _replaceDuringConfirmation;
    private bool _tooltipSelectionRequested;
    private bool _tooltipHideRequested;
    private int _retainedStop;
    private nint _trackingMenu;
    private nint _oldIcon;
    private IReadOnlyList<AppHostId> _trackingRows = [];
    private Task? _restore;
    private Task? _quit;

    private NativeSmokeHarness(bool interactiveSmoke)
    {
        _interactiveSmoke = interactiveSmoke || Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_INTERACTIVE") == "1";
        _hosts = Enum.GetValues<AppHostHealth>().Select((health, index) => new AppHostInfo(
            CreateFile(index == 0 ? "Host0 & caf\u00e9.AppHost.cs" : $"Host{index}.AppHost.cs"),
            41001 + index, index == 0 ? "http://localhost:19001/" : null)
        {
            ProcessStartTimeUnixMilliseconds = 1_700_000_000_001 + index, Health = health
        }).ToArray();
        _pinned = CreateFile("Pinned.AppHost.cs");
        _recent = CreateFile("Recent & caf\u00e9.AppHost.cs");
        _missing = Path.Combine(_directory.FullName, "Missing.AppHost.cs");
        _missingPin = CreateFile("MissingPin.AppHost.cs");
        _store.Save(new TraySavedState([
            new(_pinned, true, true), new(_recent, false, true), new(_missing, false, true), new(_missingPin, true, false)
        ]));
    }

    internal static int Run(string cliPath, int seconds, bool interactiveSmoke)
    {
        _ = cliPath; // Smoke identities are never passed to a real CLI client.
        var harness = new NativeSmokeHarness(interactiveSmoke);
        return harness.RunCore(seconds);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory.FullName, name);
        File.WriteAllText(path, "// Native smoke fixture; never executed.");
        return path;
    }

    private int RunCore(int seconds)
    {
        _controller = new(_client, _store);
        _application = new(_controller, seconds, _startupSettings)
        {
            IconAddFailuresForSmoke = 3,
            SmokeTick = Advance,
            OpenUrlForSmoke = uri =>
            {
                if (uri.AbsoluteUri == "https://aspire.dev/")
                {
                    _documentationCalls++;
                }
                else
                {
                    Require(uri.AbsoluteUri == "http://localhost:19001/", "Unexpected dashboard URL.");
                    _dashboardCalls++;
                }
            },
            CopyPathForSmoke = path => _copiedPaths.Add(path),
            ConfirmForSmoke = (title, detail, flags) =>
            {
                Require(flags == NativeMethods.SafeConfirmation && (flags & 0x100) != 0, "Confirmation must default to Cancel.");
                Require(!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(detail), "Confirmation has no context.");
                if (title == "Stop AppHost")
                {
                    Require(!detail.Contains(_directory.FullName, StringComparison.Ordinal)
                        && detail.Contains("PID", StringComparison.Ordinal)
                        && detail.Contains("running resources", StringComparison.Ordinal),
                        "Stop confirmation must explain its consequence without the AppHost path.");
                }
                _confirmations++;
                if (_replaceDuringConfirmation)
                {
                    _replaceDuringConfirmation = false;
                    Publish([Replacement(_hosts[0]), Replacement(_hosts[1]), _hosts[2], _hosts[3]]);
                    _application.DialogReadyForSmoke = () => _controller.State.AppHosts.Any(host => host.Id == Replacement(_hosts[1]).Id);
                }
                return _accept;
            },
            ErrorForSmoke = error =>
            {
                Require(error is InvalidOperationException, "Unexpected action failure in smoke.");
                _errors++;
            }
        };
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(_directory.FullName, "project;new-tab")).FullName;
            var terminal = new FolderApplication("Windows Terminal",
                Environment.ProcessPath ?? throw new InvalidOperationException("The smoke executable path is unavailable."), true);
            var launch = terminal.CreateFolderStartInfo(folder);
            Require(launch.WorkingDirectory == folder && launch.ArgumentList.SequenceEqual(["-d", "."]),
                "Terminal folder launch must keep semicolons out of its command grammar.");
            Require(!_application.WaitUntilReadyAsync(_shutdown.Token).IsCompleted, "Readiness completed without a running native loop.");
            _restore = Task.Run(() => _application.RestoreIconAsync(_shutdown.Token));
            _controller.Start();
            _application.Run();
            Require(_finished, "Smoke exited before completing its assertions.");
        }
        finally
        {
            _shutdown.Cancel();
            try
            {
                _controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                _application.Dispose();
                _shutdown.Dispose();
                _directory.Delete(recursive: true);
            }
        }
        return _application.ExitCode;
    }

    private void Advance()
    {
        if (_phase != _lastLoggedPhase)
        {
            Program.Log($"Windows native smoke phase {_phase}.");
            _lastLoggedPhase = _phase;
        }
        var state = _controller.State;
        Require(_application.WaitUntilReadyAsync(_shutdown.Token).IsCompletedSuccessfully, "Native loop readiness was not acknowledged.");
        switch (_phase)
        {
            case 0:
                if (_restore?.IsCompleted != true)
                {
                    return;
                }
                _restore.GetAwaiter().GetResult();
                Require(_application.IconAddFailuresForSmoke == 0, "Readiness was acknowledged before icon creation recovered.");
                _application.VerifyNativeStateForSmoke();
                Require(state.Discovery == DiscoveryState.Connecting && !state.HasActiveAppHosts, "Initial native state is incorrect.");
                VerifySettings();
                _phase = 1;
                Publish(_hosts);
                break;
            case 1:
                if (state.Discovery != DiscoveryState.Live)
                {
                    return;
                }
                _application.VerifyNativeStateForSmoke();
                Require(_client.Starts.IsEmpty, "A recent submenu started an AppHost automatically.");
                var paths = state.AppHosts.Concat(state.RecentAppHosts).Select(host => host.Id).ToArray();
                foreach (var id in paths)
                {
                    Invoke("CopyPath", id);
                }
                Require(_copiedPaths.SequenceEqual(_hosts.Select(host => host.AppHostPath)
                    .Concat([_pinned, _missingPin, _recent, _missing])),
                    "Copy Path must preserve the exact source path for live, pinned, and missing recent AppHosts.");
                Invoke("Dashboard", _hosts[0].Id);
                Require(_dashboardCalls == 1, "Native dashboard dispatch did not reach the URL handler.");
                Invoke("Documentation");
                Require(_documentationCalls == 1, "Documentation did not use aspire.dev.");
                _application.ShowMessageForSmoke();
                _retainedStop = _application.CaptureActionForSmoke("Stop", _hosts[0].Id);
                _application.SuppressStopForSmoke = true;
                _application.InvokeActionForSmoke(_retainedStop);
                Require(_confirmations == 1, "The stop confirmation was not shown exactly once.");
                Require(_client.Stops.IsEmpty, "Cancel dispatched a stop operation.");
                Require(_controller.ConfirmStop && _store.Load().ConfirmStop, "Cancel persisted the suppression checkbox.");
                _application.SuppressStopForSmoke = false;
                Invoke("TogglePin", _hosts[0].Id);
                Require(_controller.State.AppHosts.Single(host => host.Id == _hosts[0].Id).IsPinned, "Pin action did not persist.");
                _accept = true;
                _application.InvokeActionForSmoke(_retainedStop);
                _phase = 2;
                break;
            case 2:
                if (_client.Stops.Count != 1 || !state.AppHosts.Single(host => host.Id == _hosts[0].Id).IsStopping)
                {
                    return;
                }
                _application.VerifyNativeStateForSmoke();
                _phase = 3;
                _application.TrackForSmoke(_hosts[0].Id);
                Require(_phase == 5, "Native menu tracking ended before retained actions were verified.");
                _application.VerifyNoTooltipForSmoke();
                break;
            case 3:
                Require(_application.IsTrackingForSmoke, "The actual TrackPopupMenuEx loop is not active.");
                if (!_tooltipSelectionRequested)
                {
                    _application.SelectMenuDetailsForSmoke(_hosts[0].Id, details: true);
                    _tooltipSelectionRequested = true;
                    return;
                }
                if (!_application.IsTooltipVisibleForSmoke)
                {
                    return;
                }
                _application.VerifyMenuDetailsForSmoke(_hosts[0].Id,
                    $"{_hosts[0].AppHostPath} \u00b7 Stopping AppHost...");
                _application.VerifyDetailsTooltipForSmoke(_hosts[0].Id,
                    $"{_hosts[0].AppHostPath} \u00b7 Stopping AppHost...");
                _trackingMenu = _application.MenuForSmoke;
                _trackingRows = _application.RowIdsForSmoke;
                _phase = 4;
                Publish([_hosts[3], _hosts[2], _hosts[1], Replacement(_hosts[0])]);
                break;
            case 4:
                if (!state.AppHosts.Any(host => host.Id == Replacement(_hosts[0]).Id))
                {
                    return;
                }
                Require(_application.MenuForSmoke == _trackingMenu && _application.RowIdsForSmoke.SequenceEqual(_trackingRows),
                    "A tracked HMENU or row identity was replaced.");
                _application.VerifyNativeStateForSmoke(retained: true);
                _application.VerifyMenuDetailsForSmoke(_hosts[0].Id,
                    $"{_hosts[0].AppHostPath} \u00b7 AppHost no longer available");
                if (!_tooltipHideRequested)
                {
                    _application.VerifyDetailsTooltipForSmoke(_hosts[0].Id,
                        $"{_hosts[0].AppHostPath} \u00b7 AppHost no longer available");
                    _application.SelectMenuDetailsForSmoke(_hosts[0].Id, details: false);
                    _tooltipHideRequested = true;
                    return;
                }
                _application.VerifyNoTooltipForSmoke();
                _application.InvokeActionForSmoke(_retainedStop);
                Require(_errors == 1 && _confirmations == 2 && _client.Stops.Count == 1, "A stale sender stopped or confirmed a replacement lifetime.");
                _application.SelectMenuDetailsForSmoke(_hosts[0].Id, details: true);
                _phase = 5;
                _application.EndTrackingForSmoke();
                break;
            case 5:
                _application.VerifyNativeStateForSmoke();
                _replaceDuringConfirmation = true;
                _application.SuppressStopForSmoke = true;
                Invoke("Stop", _hosts[1].Id);
                Require(_errors == 2 && _client.Stops.Count == 1, "A lifetime replaced during a native confirmation was stopped.");
                Require(_controller.ConfirmStop && _store.Load().ConfirmStop, "A stale confirmation changed the saved preference.");
                Invoke("Stop", _hosts[2].Id);
                Require(!_controller.ConfirmStop && !_store.Load().ConfirmStop, "Accepted Stop did not persist the suppression checkbox.");
                var missing = state.RecentAppHosts.Single(host => TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, _missing));
                _accept = false;
                Invoke("Start", missing.Id);
                Require(_controller.State.RecentAppHosts.Any(host => host.Id == missing.Id), "Cancel removed a missing recent path.");
                _accept = true;
                Invoke("Start", missing.Id);
                Require(_controller.State.RecentAppHosts.All(host => host.Id != missing.Id), "Missing recent path was not removed.");
                var pinned = state.AppHosts.Single(host => TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, _pinned));
                Invoke("Start", pinned.Id);
                _phase = 6;
                break;
            case 6:
                if (_client.Starts.Count != 1 || _client.Stops.Count != 2)
                {
                    return;
                }
                Require(_client.Starts.Single() == _pinned, "Explicit Start did not preserve the selected path and casing.");
                _application.VerifyNativeStateForSmoke();
                var saved = _store.Load();
                _accept = false;
                Invoke("ClearRecent");
                Require(ReferenceEquals(saved, _store.Load()), "Cancel changed recent history.");
                _accept = true;
                Invoke("ClearRecent");
                Require(_store.Load().AppHosts.All(host => host.IsPinned && !host.IsRecent)
                    && _store.Load().AppHosts.Any(host => TrayAppHostPath.Comparer.Equals(host.AppHostPath, _pinned)), "Clear Recent did not preserve pins.");
                File.Delete(_missingPin);
                _phase = 7;
                break;
            case 7:
                if (state.AppHosts.Any(host => TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, _missingPin)))
                {
                    return;
                }
                Require(_store.Load().AppHosts.All(host => !TrayAppHostPath.Comparer.Equals(host.AppHostPath, _missingPin)),
                    "Missing offline pin was not persistently pruned.");
                var confirmations = _confirmations;
                Invoke("Stop", _hosts[3].Id);
                Require(_confirmations == confirmations, "Stop prompted despite the saved opt-out.");
                _application.RestartExplorerForSmoke();
                _oldIcon = _application.IconForSmoke;
                _application.InvalidateArtworkForSmoke();
                _phase = 8;
                break;
            case 8:
                if (_application.IconForSmoke == _oldIcon)
                {
                    return;
                }
                _application.VerifyNativeStateForSmoke();
                _restore = Task.Run(() => _application.RestoreIconAsync(_shutdown.Token));
                _phase = 9;
                break;
            case 9:
                if (_restore?.IsCompleted != true)
                {
                    return;
                }
                _restore.GetAwaiter().GetResult();
                _client.CompleteStart();
                // A successful start remains pending until discovery sees its instance.
                Publish([new(_pinned, 42001, null) { ProcessStartTimeUnixMilliseconds = 1_700_000_000_100 }]);
                _phase = 10;
                break;
            case 10:
                if (!state.AppHosts.Any(host => host.IsRunning && TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, _pinned)))
                {
                    return;
                }
                // Disconnection preserves the last live rows. Observe an empty live
                // snapshot before checking the disconnected, inactive presentation.
                Publish([]);
                _phase = 11;
                break;
            case 11:
                if (state.HasActiveAppHosts)
                {
                    return;
                }
                Publish([], DiscoveryState.Disconnected);
                _phase = 12;
                break;
            case 12:
                if (state.Discovery != DiscoveryState.Disconnected || state.HasActiveAppHosts)
                {
                    return;
                }
                _application.VerifyNativeStateForSmoke();
                Require(state.AppHosts.All(host => !host.CanStop && !host.CanStart), "Disconnected rows allow lifecycle actions.");
                _finished = true;
                _phase = 13;
                Program.Log("Windows native smoke passed: status circles, divided actions, 45-character final details, full-value details tooltips, live/stale refresh, tooltip focus/dismissal, compact names without status suffixes, menu tracking, immutable actions, native stop checkbox/opt-out, original-case Copy Path, pins/history, discovery icons, artwork invalidation, Explorer recovery, activation, terminal arguments, modeless Settings and isolated startup preferences.");
                Program.Log("Not covered by synthetic smoke: real per-monitor DPI transitions; verify these on the Windows desktop.");
                if (_interactiveSmoke)
                {
                    // Keep the isolated fake client/store; never connect preview gestures
                    // to a real CLI or the account's startup registration.
                    _controller.SetConfirmStop(true);
                    Publish(_hosts.Select(host => host with
                    {
                        ProcessStartTimeUnixMilliseconds = host.ProcessStartTimeUnixMilliseconds + 1000
                    }).ToArray());
                    _application.BeginInteractivePreviewForSmoke();
                    Program.Log("Windows native preview ready: fake AppHosts and in-memory preferences only. Quit Aspire to exit.");
                }
                else
                {
                    _quit = Task.Run(_application.RequestQuit);
                }
                break;
            case 13:
                _quit?.GetAwaiter().GetResult();
                break;
        }
    }

    private void Invoke(string kind, AppHostId id = default)
        => _application.InvokeActionForSmoke(_application.CaptureActionForSmoke(kind, id));

    private void VerifySettings()
    {
        const string off = "Launch at sign-in is off.\r\nIsolated smoke setting; no startup registration is changed.";
        const string on = "Launch at sign-in is on.\r\nIsolated smoke setting; no startup registration is changed.";
        Require(!_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 0, "Smoke startup preferences must default off.");
        Invoke("Settings");
        _application.VerifySettingsForSmoke(0, true, "");
        var firstWindow = _application.SettingsWindowForSmoke;
        Invoke("Settings");
        Require(_application.SettingsWindowForSmoke == firstWindow && _startupSettings.WriteCount == 0,
            "Opening Settings created a duplicate window or registered startup.");
        _application.ClickSettingsControlForSmoke("Startup");
        _application.VerifySettingsForSmoke(1, true, "");
        Require(_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 1, "The native checkbox did not enable the isolated setting.");
        _application.CloseSettingsForSmoke();
        Invoke("Settings");
        _application.VerifySettingsForSmoke(1, true, "");
        Require(_startupSettings.WriteCount == 1, "Reopening Settings changed startup registration.");
        _application.ClickSettingsControlForSmoke("Startup");
        _application.VerifySettingsForSmoke(0, true, "");
        Require(!_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 2, "The native checkbox did not disable the setting.");

        _startupSettings.CanEnable = false;
        _application.RefreshSettingsForSmoke();
        _application.VerifySettingsForSmoke(0, false,
            "Launch at sign-in is off. Enabling launch at sign-in is unavailable.\r\nIsolated smoke setting; no startup registration is changed.");
        _application.ClickSettingsControlForSmoke("Startup");
        Require(_startupSettings.WriteCount == 2, "An unavailable startup setting was enabled.");
        _startupSettings.Store.SetEnabled(true);
        _application.RefreshSettingsForSmoke();
        _application.VerifySettingsForSmoke(1, true,
            "Launch at sign-in is on. Enabling launch at sign-in is unavailable.\r\nIsolated smoke setting; no startup registration is changed.");
        _application.ClickSettingsControlForSmoke("Startup");
        Require(!_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 3,
            "An existing registration could not be disabled when enabling was unavailable.");

        _startupSettings.CanEnable = true;
        _startupSettings.FailWrite = true;
        _application.RefreshSettingsForSmoke();
        _application.ClickSettingsControlForSmoke("Startup");
        _application.VerifySettingsForSmoke(0, true,
            $"Could not change launch at sign-in: Simulated startup write failure.\r\nReview the status below, then retry.\r\n{off}");
        Require(!_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 4,
            "A failed write produced a phantom enabled setting.");
        _startupSettings.FailWrite = false;
        _startupSettings.FailAfterWrite = true;
        _application.ClickSettingsControlForSmoke("Startup");
        _application.VerifySettingsForSmoke(1, true,
            $"Could not change launch at sign-in: Simulated partial startup write failure.\r\nReview the status below, then retry.\r\n{on}");
        Require(_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 5,
            "A partially completed write was not re-read from the backing store.");
        _startupSettings.FailAfterWrite = false;
        _application.ClickSettingsControlForSmoke("Startup");
        _application.VerifySettingsForSmoke(0, true, "");
        _startupSettings.FailRead = true;
        _application.RefreshSettingsForSmoke();
        const string unknown = "Startup state is unknown. Could not read the sign-in setting: Simulated startup read failure.\r\nSelect Refresh startup status to retry.";
        _application.VerifySettingsForSmoke(2, false, unknown);
        _application.CloseSettingsForSmoke();
        Invoke("Settings");
        _application.VerifySettingsForSmoke(2, false, unknown);
        Require(_startupSettings.WriteCount == 6, "Opening an unknown startup state attempted registration.");
        _startupSettings.FailRead = false;
        _startupSettings.FailWrite = false;
        _application.RefreshSettingsForSmoke();
        _application.VerifySettingsForSmoke(0, true, "");
        _startupSettings.CanEnable = false;
        _startupSettings.Detail = string.Join(" ", Enumerable.Repeat(
            "Launch at sign-in requires a verified stable native CLI installation; development builds cannot register startup.", 8));
        _application.RefreshSettingsForSmoke();
        _application.VerifySettingsForSmoke(0, false,
            $"Launch at sign-in is off. Enabling launch at sign-in is unavailable.\r\n{_startupSettings.Detail}");
        _startupSettings.Detail = null;
        _startupSettings.CanEnable = true;
        _application.RefreshSettingsForSmoke();
        _application.VerifySettingsForSmoke(0, true, "");
        _application.VerifySettingsScrollingForSmoke();
        Require(_documentationCalls == 0, "Settings unexpectedly opened documentation.");
        _application.CloseSettingsForSmoke();
        Require(_startupSettings.WriteCount == 6 && !_startupSettings.Store.Read().Enabled,
            "Closing Settings changed startup state.");
    }

    private void Publish(IReadOnlyList<AppHostInfo> hosts, DiscoveryState discovery = DiscoveryState.Live)
        => _client.Publish(new(hosts, discovery));

    private static AppHostInfo Replacement(AppHostInfo host)
        => host with { ProcessStartTimeUnixMilliseconds = host.ProcessStartTimeUnixMilliseconds + 100 };

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    // Fault injection wraps only MemoryTrayStartupSettings. This harness cannot register
    // startup with the OS, including when simulating a registration that already exists.
    private sealed class SmokeStartupSettings : ITrayStartupSettings
    {
        internal MemoryTrayStartupSettings Store { get; } = new();
        internal bool CanEnable { get; set; } = true;
        internal bool FailRead { get; set; }
        internal bool FailWrite { get; set; }
        internal bool FailAfterWrite { get; set; }
        internal string? Detail { get; set; }
        internal int WriteCount { get; private set; }

        public TrayStartupState Read()
        {
            if (FailRead)
            {
                throw new IOException("Simulated startup read failure.");
            }
            var state = Store.Read();
            return state with { CanEnable = CanEnable, Detail = Detail ?? state.Detail };
        }

        public TrayStartupState SetEnabled(bool enabled)
        {
            WriteCount++;
            if (FailWrite)
            {
                throw new IOException("Simulated startup write failure.");
            }
            Require(!enabled || CanEnable, "Startup enabling is unavailable.");
            var state = Store.SetEnabled(enabled) with { CanEnable = CanEnable };
            if (FailAfterWrite)
            {
                throw new IOException("Simulated partial startup write failure.");
            }
            return state;
        }
    }

    private sealed class SmokeClient : IAppHostClient
    {
        private readonly Channel<AppHostSnapshot> _snapshots = Channel.CreateUnbounded<AppHostSnapshot>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        internal ConcurrentBag<AppHostId> Stops { get; } = [];
        internal ConcurrentBag<string> Starts { get; } = [];
        private readonly TaskCompletionSource<StartResult> _start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void CompleteStart() => _start.SetResult(new(StartOutcome.Started, 0));

        internal void Publish(AppHostSnapshot snapshot)
            => Require(_snapshots.Writer.TryWrite(snapshot), "The smoke snapshot channel is closed.");

        public async IAsyncEnumerable<AppHostSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return snapshot;
            }
        }

        public async Task<StopResult> StopAsync(AppHostId id, CancellationToken cancellationToken)
        {
            Stops.Add(id);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The fake stop must be cancelled by smoke cleanup.");
        }

        public async Task<StartResult> StartAsync(string appHostPath, CancellationToken cancellationToken)
        {
            Starts.Add(appHostPath);
            return await _start.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
