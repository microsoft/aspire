// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Controls;

public partial class DashboardRunSelect : ComponentBase, IDisposable
{
    private static readonly TimeSpan s_bubbleShowDelay = TimeSpan.FromSeconds(1);

    private DashboardRunDescriptor? _activeRun;
    private bool _bubbleOpen;
    private string? _note;
    private CancellationTokenSource? _showBubbleCancellation;

    private string TimelineTitle => Loc[nameof(LayoutResources.DashboardRunTimelineTitle)];
    private IReadOnlyList<DashboardRunDescriptor> Runs => RunStore.GetRuns()
        .Where(run => !run.IsPruned)
        .OrderByDescending(run => run.IsCurrent)
        .ThenByDescending(run => run.StartedAtUtc)
        .ToArray();

    [Parameter, EditorRequired]
    public required string SelectedRunId { get; set; }

    [Parameter]
    public EventCallback<string?> SelectedRunIdChanged { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IDashboardRunStore RunStore { get; init; }

    [Inject]
    public required ILogger<DashboardRunSelect> Logger { get; init; }

    private Task SelectRunAsync(DashboardRunDescriptor run)
        => SelectedRunIdChanged.InvokeAsync(run.IsCurrent ? null : run.RunId);

    private async Task ShowBubbleAfterDelayAsync(DashboardRunDescriptor run)
    {
        if (_bubbleOpen && ReferenceEquals(_activeRun, run))
        {
            return;
        }

        CancelAndDispose(ref _showBubbleCancellation);
        var cancellation = _showBubbleCancellation = new();
        try
        {
            await Task.Delay(s_bubbleShowDelay, cancellation.Token);
            _activeRun = run;
            _note = run.Note;
            _bubbleOpen = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private void CancelPendingBubble() => CancelAndDispose(ref _showBubbleCancellation);

    private void SaveNote(string? note)
    {
        if (_activeRun is null)
        {
            return;
        }

        _note = note;
        try
        {
            RunStore.SetRunNote(_activeRun, _note);
            _note = _activeRun.Note;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to save the note for dashboard run '{RunId}'.", _activeRun.RunId);
        }
    }

    private void TogglePinned()
    {
        if (_activeRun is null)
        {
            return;
        }

        try
        {
            RunStore.SetRunPinned(_activeRun, !_activeRun.IsPinned);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to update the pinned state of dashboard run '{RunId}'.", _activeRun.RunId);
        }
    }

    private string FormatRun(DashboardRunDescriptor run) => run.IsCurrent
        ? Loc[nameof(LayoutResources.DashboardRunSelectCurrent)]
        : FormatRunTimestamp(run);

    private string FormatRunTimestamp(DashboardRunDescriptor run)
        => FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, run.StartedAtUtc.UtcDateTime);

    private static string GetNodeId(DashboardRunDescriptor run) => $"dashboard-run-{run.RunId}";

    private static string GetNodeClass(DashboardRunDescriptor run, bool isSelected)
    {
        var classes = "application-run-node";
        if (run.IsCurrent)
        {
            classes += " current";
        }
        if (isSelected)
        {
            classes += " selected";
        }
        if (run.Note is not null)
        {
            classes += " annotated";
        }

        return classes;
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellation)
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    public void Dispose()
    {
        CancelAndDispose(ref _showBubbleCancellation);
        GC.SuppressFinalize(this);
    }
}