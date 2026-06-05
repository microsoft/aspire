// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Layout;

public partial class AspirePageContentLayout : ComponentBase
{
    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; init; }

    [Parameter]
    public required RenderFragment PageTitleSection { get; set; }

    [Parameter]
    public RenderFragment? MobilePageTitleToolbarSection { get; set; }

    [Parameter]
    public RenderFragment? ToolbarSection { get; set; }

    [Parameter]
    public bool AddNewlineOnToolbar { get; set; }

    [Parameter]
    public RenderFragment? MainSection { get; set; }

    [Parameter]
    public RenderFragment? FooterSection { get; set; }

    [Parameter]
    public string? MobileToolbarButtonText { get; set; }

    [Parameter]
    public string? HeaderStyle { get; set; }

    [Parameter]
    public string? MainContentStyle { get; set; }

    [Parameter]
    public bool IsSummaryDetailsViewOpen { get; set; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    private IDialogReference? _toolbarPanel;

    public bool IsToolbarPanelOpen => _toolbarPanel is not null;

    public Dictionary<string, Func<Task>> DialogCloseListeners { get; } = new();

    protected override async Task OnParametersSetAsync()
    {
        if (ViewportInformation.IsDesktop && IsToolbarPanelOpen)
        {
            await CloseMobileToolbarAsync();
        }
    }

    private string GetMobileMainStyle()
    {
        var style = "grid-area: main;" + MainContentStyle;
        if (!ViewportInformation.IsUltraLowHeight)
        {
            style += "overflow: auto;";
        }

        return style;
    }

    public async Task OpenMobileToolbarAsync()
    {
        _toolbarPanel = await DialogService.ShowPanelAsync<ToolbarPanel>(
            new MobileToolbar(
                ToolbarSection!,
                MobileToolbarButtonText ?? LayoutLoc[nameof(Resources.Layout.PageLayoutViewFilters)]),
            new DialogParameters
            {
                Alignment = HorizontalAlignment.Center,
                Title = MobileToolbarButtonText ?? ControlsStringsLoc[nameof(ControlsStrings.ChartContainerFiltersHeader)],
                Width = "100%",
                Height = "90%",
                Modal = false,
                PrimaryAction = null,
                SecondaryAction = null,
                OnDialogClosing = EventCallback.Factory.Create<DialogInstance>(this, async () =>
                {
                    await InvokeListenersAsync();
                    _toolbarPanel = null;
                })
            });
    }

    public async Task CloseMobileToolbarAsync()
    {
        if (_toolbarPanel is not null)
        {
            await _toolbarPanel.CloseAsync();
            // CloseAsync doesn't invoke OnDialogClosing, so we need to call InvokeListeners ourselves
            await InvokeListenersAsync();

            _toolbarPanel = null;
        }
    }

    private async Task InvokeListenersAsync()
    {
        foreach (var dialogCloseListener in DialogCloseListeners.Values)
        {
            await dialogCloseListener.Invoke();
        }

        DialogCloseListeners.Clear();
    }

    public record MobileToolbar(RenderFragment ToolbarSection, string MobileToolbarButtonText);
}

