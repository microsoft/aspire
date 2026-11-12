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
    private int _errors;
    private readonly List<string> _copiedPaths = [];
    private bool _finished;
    private AppHostMenuItem? _staleHost;
    private nint _oldIcon;
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
                _application.VerifyActivationForSmoke();
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
                Require(_client.Starts.IsEmpty, "Listing a recent AppHost started it automatically.");
                foreach (var id in state.AppHosts.Concat(state.RecentAppHosts).Select(host => host.Id))
                {
                    _application.PickRowActionForSmoke(id, PanelTargetKind.CopyPath);
                }
                Require(_copiedPaths.SequenceEqual(_hosts.Select(host => host.AppHostPath)
                    .Concat([_pinned, _missingPin, _recent, _missing]).Select(Path.GetDirectoryName)),
                    "Copy path must copy the exact containing folder for live, pinned, and missing recent AppHosts.");
                _application.ClickPanelForSmoke(PanelTargetKind.Dashboard, _hosts[0].Id);
                Require(_dashboardCalls == 1, "The panel's Dashboard button did not reach the URL handler.");
                _application.ClickPanelForSmoke(PanelTargetKind.Documentation);
                Require(_documentationCalls == 1, "Documentation did not use aspire.dev.");
                _application.ShowMessageForSmoke();
                _application.VerifyActionListForSmoke(_hosts[1].Id);
                _application.PickRowActionForSmoke(_hosts[0].Id, PanelTargetKind.TogglePin);
                Require(_controller.State.AppHosts.Single(host => host.Id == _hosts[0].Id).IsPinned, "Pin from the action list did not persist.");
                _staleHost = _controller.State.AppHosts.Single(host => host.Id == _hosts[0].Id);
                _application.ClickPanelForSmoke(PanelTargetKind.Stop, _hosts[0].Id);
                _phase = 2;
                break;
            case 2:
                if (_client.Stops.Count != 1 || !state.AppHosts.Single(host => host.Id == _hosts[0].Id).IsStopping)
                {
                    return;
                }
                _application.VerifyNativeStateForSmoke();
                _phase = 3;
                Publish([_hosts[3], _hosts[2], _hosts[1], Replacement(_hosts[0])]);
                break;
            case 3:
                if (!state.AppHosts.Any(host => host.Id == Replacement(_hosts[0]).Id))
                {
                    return;
                }
                _application.VerifyNativeStateForSmoke();
                // An action from a scene drawn before the lifetime was replaced must be rejected.
                _application.RunPanelActionForSmoke(new(PanelActionKind.Stop, _staleHost));
                Require(_errors == 1 && _client.Stops.Count == 1, "A stale action stopped a replacement lifetime.");
                // Stopping isn't confirmed: the request goes out at once, here through UI Automation.
                _application.PressPanelForSmoke(PanelTargetKind.Stop, _hosts[2].Id);
                Require(_client.Stops.Count == 2, "Stop did not send its request without a dialog.");
                var missing = state.RecentAppHosts.Single(host => TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, _missing));
                _application.ClickPanelForSmoke(PanelTargetKind.Start, missing.Id);
                Require(_application.PanelForSmoke.Confirmation == PanelConfirmKind.RemoveMissing, "A missing recent AppHost was not offered for removal inline.");
                _application.ClickPanelForSmoke(PanelTargetKind.ConfirmCancel, missing.Id);
                Require(_controller.State.RecentAppHosts.Any(host => host.Id == missing.Id), "Cancel removed a missing recent path.");
                _application.ClickPanelForSmoke(PanelTargetKind.Start, missing.Id);
                _application.ClickPanelForSmoke(PanelTargetKind.ConfirmAccept, missing.Id);
                Require(_controller.State.RecentAppHosts.All(host => host.Id != missing.Id), "Missing recent path was not removed.");
                var pinned = state.AppHosts.Single(host => TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, _pinned));
                _application.ClickPanelForSmoke(PanelTargetKind.Start, pinned.Id);
                _phase = 4;
                break;
            case 4:
                if (_client.Starts.Count != 1 || _client.Stops.Count != 2)
                {
                    return;
                }
                Require(_client.Starts.Single() == _pinned, "Explicit Start did not preserve the selected path and casing.");
                _application.VerifyNativeStateForSmoke();
                var saved = _store.Load();
                _application.ClickPanelForSmoke(PanelTargetKind.ClearRecent);
                Require(_application.PanelForSmoke.Confirmation == PanelConfirmKind.ClearRecent, "Clear did not ask inline.");
                _application.ClickPanelForSmoke(PanelTargetKind.ConfirmCancel);
                Require(ReferenceEquals(saved, _store.Load()), "Cancel changed recent history.");
                _application.ClickPanelForSmoke(PanelTargetKind.ClearRecent);
                _application.ClickPanelForSmoke(PanelTargetKind.ConfirmAccept);
                Require(_store.Load().AppHosts.All(host => host.IsPinned && !host.IsRecent)
                    && _store.Load().AppHosts.Any(host => TrayAppHostPath.Comparer.Equals(host.AppHostPath, _pinned)), "Clear Recent did not preserve pins.");
                File.Delete(_missingPin);
                _application.ReopenPanelForSmoke();
                _phase = 5;
                break;
            case 5:
                if (state.AppHosts.Any(host => TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, _missingPin)))
                {
                    return;
                }
                Require(_store.Load().AppHosts.All(host => !TrayAppHostPath.Comparer.Equals(host.AppHostPath, _missingPin)),
                    "Missing offline pin was not persistently pruned.");
                _application.RestartExplorerForSmoke();
                _oldIcon = _application.IconForSmoke;
                _application.InvalidateArtworkForSmoke();
                _phase = 6;
                break;
            case 6:
                if (_application.IconForSmoke == _oldIcon)
                {
                    return;
                }
                _application.VerifyNativeStateForSmoke();
                _restore = Task.Run(() => _application.RestoreIconAsync(_shutdown.Token));
                _phase = 7;
                break;
            case 7:
                if (_restore?.IsCompleted != true)
                {
                    return;
                }
                _restore.GetAwaiter().GetResult();
                _client.CompleteStart();
                // A successful start remains pending until discovery sees its instance.
                Publish([new(_pinned, 42001, "http://localhost:19001/") { ProcessStartTimeUnixMilliseconds = 1_700_000_000_100 }]);
                _phase = 8;
                break;
            case 8:
                if (!state.AppHosts.Any(host => host.IsRunning && TrayAppHostPath.Comparer.Equals(host.Id.AppHostPath, _pinned)))
                {
                    return;
                }
                _application.VerifyNativeStateForSmoke();
                _application.ClickPanelForSmoke(PanelTargetKind.Dashboard, state.AppHosts.Single(host => host.IsRunning).Id);
                Require(_dashboardCalls == 2, "A started AppHost did not gain an actionable dashboard button.");
                // Disconnection preserves the last live rows. Observe an empty live
                // snapshot before checking the disconnected, inactive presentation.
                Publish([]);
                _phase = 9;
                break;
            case 9:
                if (state.HasActiveAppHosts)
                {
                    return;
                }
                Publish([], DiscoveryState.Disconnected);
                _phase = 10;
                break;
            case 10:
                if (state.Discovery != DiscoveryState.Disconnected || state.HasActiveAppHosts)
                {
                    return;
                }
                _application.VerifyNativeStateForSmoke();
                Require(state.AppHosts.All(host => !host.CanStop && !host.CanStart), "Disconnected rows allow lifecycle actions.");
                _finished = true;
                _phase = 11;
                Program.Log("Windows native smoke passed: every icon activation opens the flyout (left, right, keyboard with focus) and repeats keep it open, Ctrl+comma and the Settings view with isolated startup preferences, panel rows matching the view state, Dashboard and Documentation buttons, action list items with UI Automation and keyboard, original-case Copy path for live/pinned/recent rows, pin from the action list, unconfirmed Stop by click and UI Automation with stale-lifetime safety, inline missing-file removal and Clear, pin pruning, discovery icons, artwork invalidation, Explorer recovery, activation, terminal arguments.");
                Program.Log("Not covered by synthetic smoke: real per-monitor DPI transitions; verify these on the Windows desktop.");
                if (_interactiveSmoke)
                {
                    // Keep the isolated fake client/store; never connect preview gestures
                    // to a real CLI or the account's startup registration.
                    Publish(_hosts.Select(host => host with
                    {
                        ProcessStartTimeUnixMilliseconds = host.ProcessStartTimeUnixMilliseconds + 1000
                    }).ToArray());
                    _application.BeginInteractivePreviewForSmoke();
                    Program.Log("Windows native preview ready: fake AppHosts and in-memory preferences only. Exit in the panel to close it.");
                }
                else
                {
                    _quit = Task.Run(_application.RequestQuit);
                }
                break;
            case 11:
                _quit?.GetAwaiter().GetResult();
                break;
        }
    }

    /// <summary>
    /// Drives the panel's Settings view against the isolated startup settings, including the
    /// unavailable, failed-write, partial-write and failed-read states.
    /// </summary>
    private void VerifySettings()
    {
        Require(!_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 0, "Smoke startup preferences must default off.");
        _application.VerifySettingsViewForSmoke(false, true, TraySettingsText.StartupDescription);
        _application.ToggleLaunchAtSignInForSmoke();
        _application.VerifySettingsViewForSmoke(true, true, TraySettingsText.StartupDescription);
        Require(_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 1, "The switch did not enable the isolated setting.");
        _application.ToggleLaunchAtSignInForSmoke();
        Require(!_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 2, "The switch did not disable the setting.");

        _startupSettings.CanEnable = false;
        _application.VerifySettingsViewForSmoke(false, false, TraySettingsText.StableNativeInstallationRequired);
        _application.ToggleLaunchAtSignInForSmoke();
        Require(_startupSettings.WriteCount == 2, "An unavailable startup setting was enabled.");
        _startupSettings.Store.SetEnabled(true);
        _application.VerifySettingsViewForSmoke(true, true, TraySettingsText.StableNativeInstallationRequired);
        _application.ToggleLaunchAtSignInForSmoke();
        Require(!_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 3,
            "An existing registration could not be disabled when enabling was unavailable.");

        _startupSettings.CanEnable = true;
        _startupSettings.FailWrite = true;
        _application.VerifySettingsViewForSmoke(false, true, TraySettingsText.StartupDescription);
        _application.ToggleLaunchAtSignInForSmoke();
        Require(!_startupSettings.Store.Read().Enabled && _startupSettings.WriteCount == 4, "A failed write produced a phantom enabled setting.");
        Require(_application.PanelForSmoke.Scene.Accessibility.Single(element => element.Target?.Kind == PanelTargetKind.LaunchAtSignIn)
            .Description!.Contains("Simulated startup write failure.", StringComparison.Ordinal), "A failed write was not reported under the switch.");
        _startupSettings.FailWrite = false;
        _startupSettings.FailRead = true;
        _application.VerifySettingsViewForSmoke(false, false,
            "Could not read launch at sign-in: Simulated startup read failure. Close and reopen Settings to retry.");
        Require(_startupSettings.WriteCount == 4, "Opening an unknown startup state attempted registration.");
        _startupSettings.FailRead = false;
        _application.VerifySettingsViewForSmoke(false, true, TraySettingsText.StartupDescription);
        _application.CloseSettingsViewForSmoke();
        Require(_documentationCalls == 0, "Settings unexpectedly opened documentation.");
        Require(_startupSettings.WriteCount == 4 && !_startupSettings.Store.Read().Enabled, "Closing Settings changed startup state.");
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
