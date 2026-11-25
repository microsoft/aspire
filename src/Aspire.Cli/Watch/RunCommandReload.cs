// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Watch;

internal enum RunReloadMode
{
    None,
    AppHost,
    Services
}

internal sealed class RunCommandReload : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly IConsoleInput _input;
    private readonly ConsoleLogBufferContext _promptContext;
    private readonly IInteractionService _interaction;
    private readonly DotnetWatchAppHostGeneration? _generation;
    private readonly AspireCliTelemetry _telemetry;
    private readonly Activity? _runActivity;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop;
    private IAppHostCliBackchannel? _backchannel;
    private Task? _listener;
    private RunReloadMode _mode;
    private bool _supportsServicesReload;
    private bool _hasWatchGroups;
    private bool _reloadHintShown;
    private Action<RunReloadMode>? _showHint;
    private Activity? _summary;
    private int _reloadCount;
    private int _disposed;

    internal RunCommandReload(
        bool available,
        IConsoleInput input,
        ConsoleLogBufferContext promptContext,
        IInteractionService interaction,
        DotnetWatchAppHostGeneration? generation,
        AspireCliTelemetry telemetry,
        Activity? runActivity,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        IsAvailable = available;
        _input = input;
        _promptContext = promptContext;
        _interaction = interaction;
        _generation = generation;
        _telemetry = telemetry;
        _runActivity = runActivity;
        _timeProvider = timeProvider;
        _logger = logger;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    internal bool IsAvailable { get; private set; }

    internal RunReloadMode Mode
    {
        get
        {
            lock (_lock)
            {
                return _mode;
            }
        }
    }

    internal async Task BackchannelEstablishedAsync(IAppHostCliBackchannel backchannel, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return;
        }

        if (_generation?.IsEnabled == true)
        {
            EnableAppHost();
            return;
        }

        try
        {
            var capabilities = await backchannel.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _backchannel = backchannel;
                _supportsServicesReload = capabilities.Contains(ResourceWatch.ReloadCapability, StringComparer.Ordinal);
            }
            UpdateServicesMode();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // An additive shortcut must not turn an otherwise usable older AppHost into a startup failure.
            _logger.LogDebug(exception, "Could not negotiate watched-service reload support.");
        }
    }

    internal bool EnableAppHost() => SetMode(RunReloadMode.AppHost);

    internal void UpdateWatchStatus(ResourceWatchStatus? status)
    {
        lock (_lock)
        {
            _hasWatchGroups = status?.Resources.Any(resource =>
                !string.IsNullOrEmpty(resource.LogResource) &&
                resource.State is ResourceWatch.Pending or ResourceWatch.Watching or ResourceWatch.BuildFailed or ResourceWatch.Failed) == true;
        }
        UpdateServicesMode();
    }

    internal void RegisterHint(Action<RunReloadMode> renderInitialHint, Action<RunReloadMode> showLateHint)
    {
        lock (_lock)
        {
            _showHint = showLateHint;
            _reloadHintShown = _mode != RunReloadMode.None;
            // Render and subscribe atomically so a newly discovered group cannot race
            // initial output and append a duplicate Ctrl+R hint.
            renderInitialHint(_mode);
        }
    }

    /// <summary>
    /// Completes the Ctrl+R reload that started this AppHost, if any, now that the AppHost has started.
    /// </summary>
    internal void RecordAppHostStarted() =>
        _generation?.PendingReload?.Complete(TelemetryConstants.RunReloadOutcomes.Succeeded);

    /// <summary>
    /// Completes the Ctrl+R reload that started this AppHost, if any, with the failure that stopped the AppHost from
    /// starting. Only the first outcome counts, so a specific failure recorded earlier wins over a later generic one.
    /// </summary>
    /// <param name="outcome">One of the failure values of <see cref="TelemetryConstants.RunReloadOutcomes"/>.</param>
    internal void RecordAppHostFailure(string outcome) => _generation?.PendingReload?.Complete(outcome);

    private void UpdateServicesMode()
    {
        lock (_lock)
        {
            if (_mode == RunReloadMode.AppHost || _generation?.IsEnabled == true)
            {
                return;
            }
            SetMode(_supportsServicesReload && _hasWatchGroups ? RunReloadMode.Services : RunReloadMode.None);
        }
    }

    private bool SetMode(RunReloadMode mode)
    {
        Action<RunReloadMode>? showHint = null;
        lock (_lock)
        {
            if (!IsAvailable || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            if (mode != RunReloadMode.None && _mode == RunReloadMode.None)
            {
                try
                {
                    // Discard input buffered during startup rather than interpreting an earlier
                    // keypress as a request to reload a run that was not ready at the time.
                    while (_promptContext.TryReadKey(_input, out _))
                    {
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    IsAvailable = false;
                    _logger.LogDebug(exception, "Console input is unavailable for run shortcuts.");
                    return false;
                }

                _listener ??= ListenAsync();
                // The summary covers the reloads that this AppHost offered; runs that never offer Ctrl+R don't record it.
                _summary ??= RunReloadActivity.StartWithoutChangingCurrent(
                    _telemetry, TelemetryConstants.Activities.RunAppHostReloadSummary, _runActivity);
                _telemetry.SetActivityProperty(_summary, TelemetryConstants.Tags.RunReloadCount, _reloadCount);
            }

            _mode = mode;
            if (mode != RunReloadMode.None)
            {
                _telemetry.SetActivityProperty(_summary, TelemetryConstants.Tags.RunReloadMode, RunReloadActivity.ToTagValue(mode));
                if (!_reloadHintShown && _showHint is not null)
                {
                    _reloadHintShown = true;
                    showHint = _showHint;
                }
            }
        }

        showHint?.Invoke(mode);
        return mode != RunReloadMode.None;
    }

    private async Task ListenAsync()
    {
        var reload = Task.CompletedTask;
        var pending = false;
        try
        {
            // Do not change TreatControlCAsInput: Ctrl+C must keep using the command's
            // cooperative cancellation and graceful-shutdown machinery.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), _timeProvider);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (Mode == RunReloadMode.None)
                {
                    pending = false;
                    continue;
                }
                if (_promptContext.IsInteractivePromptActive)
                {
                    continue;
                }

                for (var index = 0; index < 64 && _promptContext.TryReadKey(_input, out var key); index++)
                {
                    if (key.Key == ConsoleKey.R && key.Modifiers == ConsoleModifiers.Control)
                    {
                        pending = true;
                    }
                }

                if (pending && reload.IsCompleted)
                {
                    await reload.ConfigureAwait(false);
                    // Mode changes also acquire the prompt lock while draining stale input.
                    // Keep the same lock order when starting a request.
                    lock (_lock)
                    {
                        _promptContext.TryRunWithoutInteractivePrompt(() =>
                        {
                            pending = false;
                            // Keep polling while the RPC builds, but retain only one queued request.
                            reload = ReloadAsync(_stop.Token);
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            lock (_lock)
            {
                IsAvailable = false;
                _mode = RunReloadMode.None;
            }
            _logger.LogDebug(exception, "Console input ended while listening for run shortcuts.");
        }
        finally
        {
            try
            {
                await reload.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        IAppHostCliBackchannel? backchannel = null;
        RunReloadActivity? reload = null;
        var stoppingAppHost = false;
        lock (_lock)
        {
            if (_mode == RunReloadMode.AppHost)
            {
                if (_generation is { } generation && generation.RequestReload())
                {
                    // The replacement AppHost completes this reload once it starts or fails.
                    generation.RequestedReload = StartReload(RunReloadMode.AppHost);
                    stoppingAppHost = true;
                }
                // Replacement owns the next opportunity for input. Buffered teardown keys
                // must not cause another replacement before that generation has started.
                _mode = RunReloadMode.None;
            }
            else if (_mode == RunReloadMode.Services && _backchannel is { } services)
            {
                backchannel = services;
                reload = StartReload(RunReloadMode.Services);
            }
        }

        if (stoppingAppHost)
        {
            // Stopping the AppHost can take seconds (its graceful shutdown), and the reloading message follows only
            // once it has stopped, so acknowledge the key press now.
            _interaction.DisplayMessage(KnownEmojis.Information, RunCommandStrings.StoppingAppHostForReload);
        }
        if (backchannel is null || reload is null)
        {
            return;
        }

        _interaction.DisplayMessage(KnownEmojis.Information, RunCommandStrings.RebuildingWatchedServices);
        var outcome = TelemetryConstants.RunReloadOutcomes.RpcFailed;
        try
        {
            // Builds and service startup can take minutes. Only shutdown/cancellation bounds this RPC.
            var result = await backchannel.ReloadWatchedResourcesAsync(cancellationToken).ConfigureAwait(false);
            outcome = GetOutcome(result);
            if (result.Groups.Length == 0)
            {
                // The services stopped being watched between the last watch status and this request.
                _interaction.DisplayMessage(KnownEmojis.Information, RunCommandStrings.NoWatchedServicesToRebuild);
            }
            foreach (var group in result.Groups)
            {
                var message = group.Message ?? string.Format(CultureInfo.CurrentCulture,
                    group.Success ? RunCommandStrings.WatchedServicesReloaded : RunCommandStrings.WatchedServicesReloadFailed,
                    group.LogResource);
                if (group.Success)
                {
                    _interaction.DisplaySuccess(message);
                }
                else
                {
                    _interaction.DisplayError(message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = TelemetryConstants.RunReloadOutcomes.Canceled;
            throw;
        }
        catch (Exception exception)
        {
            // Shutdown can also surface as a lost connection rather than as cancellation.
            outcome = cancellationToken.IsCancellationRequested
                ? TelemetryConstants.RunReloadOutcomes.Canceled
                : TelemetryConstants.RunReloadOutcomes.RpcFailed;
            _logger.LogDebug(exception, "Could not rebuild watched services.");
            _interaction.DisplayError(RunCommandStrings.CouldNotReloadWatchedServices);
        }
        finally
        {
            reload.Complete(outcome);
        }
    }

    /// <summary>Counts an accepted reload and starts its activity. Runs under <c>_lock</c>.</summary>
    private RunReloadActivity StartReload(RunReloadMode mode)
    {
        _telemetry.SetActivityProperty(_summary, TelemetryConstants.Tags.RunReloadCount, ++_reloadCount);
        return RunReloadActivity.Start(_telemetry, _summary, mode);
    }

    /// <summary>
    /// Classifies the result of rebuilding the watched services. A failed group is classified by its watch state right
    /// after its Rebuild: a failed fresh build leaves it <c>build-failed</c> and a failed group leaves it <c>failed</c>;
    /// otherwise the group was rebuilt but a service didn't start.
    /// </summary>
    internal static string GetOutcome(ResourceWatchReloadResult result)
    {
        if (result.Groups.Length == 0)
        {
            return TelemetryConstants.RunReloadOutcomes.NoWatchedServices;
        }

        var failures = result.Groups.Where(group => !group.Success).Select(group => group.State switch
        {
            ResourceWatch.Failed => TelemetryConstants.RunReloadOutcomes.GroupFailed,
            ResourceWatch.BuildFailed => TelemetryConstants.RunReloadOutcomes.BuildFailed,
            _ => TelemetryConstants.RunReloadOutcomes.StartFailed
        }).ToHashSet(StringComparer.Ordinal);

        // With several failed groups, report the failure least likely to come from the user's code, so frequent compile
        // errors in one group don't hide a failed group in another.
        return failures.Count == 0 ? TelemetryConstants.RunReloadOutcomes.Succeeded
            : failures.Contains(TelemetryConstants.RunReloadOutcomes.GroupFailed) ? TelemetryConstants.RunReloadOutcomes.GroupFailed
            : failures.Contains(TelemetryConstants.RunReloadOutcomes.StartFailed) ? TelemetryConstants.RunReloadOutcomes.StartFailed
            : TelemetryConstants.RunReloadOutcomes.BuildFailed;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listener is not null)
        {
            await _listener.ConfigureAwait(false);
        }

        // A reload that started this AppHost and is still pending ends with it: an automatic restart or another Ctrl+R
        // replaced the AppHost before it finished starting, or the run is stopping.
        if (_generation is { } generation)
        {
            generation.PendingReload?.Complete(!generation.Invalidated.IsCompleted
                ? TelemetryConstants.RunReloadOutcomes.Canceled
                : generation.ReloadRequested
                    ? TelemetryConstants.RunReloadOutcomes.SupersededByReload
                    : TelemetryConstants.RunReloadOutcomes.Superseded);
        }

        Activity? summary;
        lock (_lock)
        {
            summary = _summary;
            _summary = null;
        }
        RunReloadActivity.StopWithoutChangingCurrent(summary);
        _stop.Dispose();
    }
}
