// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using ControlsResources = Aspire.Dashboard.Resources.ControlsStrings;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Controls;

public partial class PauseWarning : ComponentBase, IAsyncDisposable
{
    private static readonly TimeSpan s_refreshInterval = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _disposeCts = new();
    // Copied from the CTS so the refresh loop can keep reading it after DisposeAsync disposes the CTS.
    private readonly CancellationToken _disposeToken;
    private Task? _refreshTask;

    public PauseWarning()
    {
        _disposeToken = _disposeCts.Token;
    }

    [Parameter, EditorRequired]
    public required DateTime PausedAt { get; set; }

    [Parameter, EditorRequired]
    public required string CaptureType { get; set; }

    [Inject]
    public required IStringLocalizer<ControlsResources> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<LayoutResources> LayoutLoc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    private string RelativeAge => DashboardUIHelpers.FormatTimeAgo(
        TimeProvider.GetUtcNow() - ToUtcOffset(PausedAt),
        LayoutLoc,
        useShortFormat: false,
        lessThanMinuteText: LayoutLoc[nameof(LayoutResources.ResourceStartedAgeNow)]);
    private string PauseWarningText => string.Format(
        CultureInfo.CurrentCulture,
        Loc[nameof(ControlsResources.PauseWarningText)],
        CaptureType);
    private string AbsoluteTime => FormatHelpers.FormatTooltipDateTime(TimeProvider, PausedAt);
    private string AbsoluteTooltip => $"{PauseWarningText} {AbsoluteTime}";

    protected override void OnInitialized()
    {
        // Relative age must advance while capture stays paused; pages do not re-render on their own.
        _refreshTask = RefreshAgePeriodicallyAsync(_disposeToken);
    }

    private async Task RefreshAgePeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_refreshInterval, TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static DateTimeOffset ToUtcOffset(DateTime pausedAtUtc)
    {
        // PauseManager and console pause intervals store UTC DateTime values.
        return new DateTimeOffset(DateTime.SpecifyKind(pausedAtUtc, DateTimeKind.Utc));
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync();
        await TaskHelpers.WaitIgnoreCancelAsync(_refreshTask);
        _disposeCts.Dispose();
    }
}
