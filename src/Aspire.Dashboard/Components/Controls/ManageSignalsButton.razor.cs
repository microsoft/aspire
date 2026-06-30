// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Controls;

public partial class ManageSignalsButton : ComponentBase
{
    private static readonly Icon s_downloadLogsIcon = new Icons.Regular.Size16.ArrowDownload();
    private static readonly Icon s_clearSelectedResourceIcon = new Icons.Regular.Size16.Delete();
    private static readonly Icon s_clearAllResourcesIcon = new Icons.Regular.Size20.DeleteLines();

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Parameter]
    public required SelectViewModel<ResourceTypeDetails> SelectedResource { get; set; }

    [Parameter]
    public required Func<ResourceKey?, Task> HandleClearSignal { get; set; }

    [Parameter]
    public Func<Task>? HandleDownloadLogs { get; set; }

    private readonly List<MenuButtonItem> _clearMenuItems = new();

    protected override void OnParametersSet()
    {
        _clearMenuItems.Clear();

        if (HandleDownloadLogs is not null)
        {
            _clearMenuItems.Add(new()
            {
                Id = "clear-menu-download",
                Icon = s_downloadLogsIcon,
                OnClick = HandleDownloadLogs,
                Text = Dashboard.Resources.ConsoleLogs.DownloadLogs,
            });

            _clearMenuItems.Add(new()
            {
                IsDivider = true
            });
        }

        _clearMenuItems.Add(new()
        {
            Id = "clear-menu-all",
            Icon = s_clearAllResourcesIcon,
            OnClick = () => HandleClearSignal(null),
            Text = ControlsStringsLoc[name: nameof(ControlsStrings.ClearAllResources)],
        });

        _clearMenuItems.Add(new()
        {
            Id = "clear-menu-resource",
            Icon = s_clearSelectedResourceIcon,
            OnClick = () => HandleClearSignal(SelectedResource.Id?.GetResourceKey()),
            IsDisabled = SelectedResource.Id == null,
            Text = SelectedResource.Id == null
                ? ControlsStringsLoc[nameof(ControlsStrings.ClearPendingSelectedResource)]
                : string.Format(CultureInfo.InvariantCulture, ControlsStringsLoc[name: nameof(ControlsStrings.ClearSelectedResource)], SelectedResource.Name),
        });
    }
}
