// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using MetricsResources = Aspire.Dashboard.Resources.Metrics;
using ToastNotificationService = Microsoft.FluentUI.AspNetCore.Components.INotificationService;

namespace Aspire.Dashboard.Components;

public partial class MetricPinButton : ComponentBase, IDisposable
{
    private static readonly Icon s_pinnedIcon = new Icons.Filled.Size16.Pin();
    private static readonly Icon s_unpinnedIcon = new Icons.Regular.Size16.Pin();
    private bool _isPinned;

    [Inject]
    public required MetricPinStore PinStore { get; init; }

    [Inject]
    public required IStringLocalizer<MetricsResources> Loc { get; init; }

    [Inject]
    public required ToastNotificationService ToastService { get; init; }

    [Parameter, EditorRequired]
    public required OtlpInstrumentKey InstrumentKey { get; set; }

    /// <summary>
    /// Gets or sets the names of the resources the pin applies to: those of the current selection. The button shows
    /// the instrument pinned when any of them has it pinned, and unpinning it unpins it for all of them.
    /// </summary>
    [Parameter, EditorRequired]
    public required IReadOnlyList<string> ResourceNames { get; set; }

    [Parameter]
    public string? Class { get; set; }

    /// <summary>
    /// Gets or sets whether the button has a square border, for headers next to other icon buttons.
    /// </summary>
    [Parameter]
    public bool Bordered { get; set; }

    protected override async Task OnInitializedAsync()
    {
        // Another button for the same instrument (e.g. the card and the chart header) can change the pin.
        PinStore.Changed += OnPinsChanged;
        await PinStore.EnsureLoadedAsync();
    }

    protected override void OnParametersSet()
    {
        _isPinned = PinStore.IsPinned(InstrumentKey, ResourceNames);
    }

    private async Task TogglePinnedAsync()
    {
        var update = PinStore.SetPinned(InstrumentKey, ResourceNames, !_isPinned);
        _isPinned = PinStore.IsPinned(InstrumentKey, ResourceNames);
        if (await update.Completion == SettingUpdateResult.LimitReached)
        {
            await ToastService.ShowWarningToastAsync(Loc[nameof(MetricsResources.MetricsPinLimitReached), MetricPinStore.MaxSavedPins]);
        }
    }

    private void OnPinsChanged()
    {
        _ = InvokeAsync(() =>
        {
            _isPinned = PinStore.IsPinned(InstrumentKey, ResourceNames);
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        PinStore.Changed -= OnPinsChanged;
    }
}