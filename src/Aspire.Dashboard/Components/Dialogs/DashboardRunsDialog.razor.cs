// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Dialogs;

/// <summary>
/// The recordings panel: switches between the live view and recordings of previous runs, and protects recordings
/// from automatic deletion.
/// </summary>
public partial class DashboardRunsDialog
{
    internal const string DashboardRunsHelpUrl = "https://aka.ms/aspire/run-persistence";

    private readonly string _idPrefix = $"dashboard-runs-{Guid.NewGuid():N}";
    private string? _pendingFocusElementId;

    private string RecordingsHeaderId => $"{_idPrefix}-recordings";

    [Parameter]
    public required DashboardRunsDialogContent Content { get; set; }

    [CascadingParameter]
    public IDialogInstance? Dialog { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IDashboardRunStore RunStore { get; init; }

    [Inject]
    public required ILogger<DashboardRunsDialog> Logger { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Pinning re-sorts the list, and moving the focused row in the DOM drops its focus. Put focus back on the
        // pin button that was clicked so keyboard users keep their place.
        if (_pendingFocusElementId is { } id)
        {
            _pendingFocusElementId = null;
            await JS.InvokeVoidAsync("focusElement", id);
        }
    }

    internal string GetPinButtonId(DashboardRunDescriptor run) => $"{_idPrefix}-pin-{Uri.EscapeDataString(run.RunId)}";

    private async Task SelectRunAsync(DashboardRunDescriptor run)
    {
        // Close first: switching runs re-renders the page under the panel, and the layout returns focus to the
        // header button once the panel is gone.
        if (Dialog is not null)
        {
            await Dialog.CloseAsync();
        }

        await Content.SelectRunAsync(run.IsCurrent ? null : run.RunId);
    }

    private void SetRunPinned(DashboardRunDescriptor run, bool isPinned)
    {
        try
        {
            RunStore.SetRunPinned(run, isPinned);
            _pendingFocusElementId = GetPinButtonId(run);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to update the pinned state of dashboard run '{RunId}'.", run.RunId);
        }
    }

    private string FormatRunOption(DashboardRunDescriptor run) => run.IsCurrent
        ? Loc[nameof(LayoutResources.DashboardRunSelectCurrent)]
        : DashboardRunSelect.FormatRunAge(run.StartedAtUtc, TimeProvider.GetUtcNow(), Loc);

    private string FormatRunDate(DateTimeOffset startedAtUtc) => FormatHelpers.FormatDateTime(TimeProvider, startedAtUtc.UtcDateTime);
}
