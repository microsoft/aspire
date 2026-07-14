// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Builds menu items for trace context menus and action buttons.
/// </summary>
public sealed class TraceMenuBuilder
{
    private static readonly Icon s_viewDetailsIcon = new Icons.Regular.Size16.Info();
    private static readonly Icon s_structuredLogsIcon = new Icons.Regular.Size16.SlideTextSparkle();
    private static readonly Icon s_bracesIcon = new Icons.Regular.Size16.Braces();

    private readonly IStringLocalizer<ControlsStrings> _controlsLoc;
    private readonly NavigationManager _navigationManager;
    private readonly DashboardDialogService _dialogService;
    private readonly ITelemetryRepository _telemetryRepository;
    private readonly IOutgoingPeerResolver[] _outgoingPeerResolvers;

    /// <summary>
    /// Initializes a new instance of the <see cref="TraceMenuBuilder"/> class.
    /// </summary>
    public TraceMenuBuilder(
        IStringLocalizer<ControlsStrings> controlsLoc,
        NavigationManager navigationManager,
        DashboardDialogService dialogService,
        ITelemetryRepository telemetryRepository,
        IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers)
    {
        _controlsLoc = controlsLoc;
        _navigationManager = navigationManager;
        _dialogService = dialogService;
        _telemetryRepository = telemetryRepository;
        _outgoingPeerResolvers = outgoingPeerResolvers.ToArray();
    }

    /// <summary>
    /// Adds menu items for a trace to the provided list.
    /// </summary>
    /// <param name="menuItems">The list to add menu items to.</param>
    /// <param name="trace">The trace to create menu items for.</param>
    /// <param name="showViewDetails">Whether to include the View Details menu item. Defaults to <c>true</c>.</param>
    public void AddMenuItems(
        List<MenuButtonItem> menuItems,
        OtlpTrace trace,
        bool showViewDetails = true)
    {
        if (showViewDetails)
        {
            menuItems.Add(new MenuButtonItem
            {
                Text = _controlsLoc[nameof(ControlsStrings.ActionViewDetailsText)],
                Icon = s_viewDetailsIcon,
                OnClick = () =>
                {
                    _navigationManager.NavigateTo(DashboardUrls.TraceDetailUrl(trace.TraceId));
                    return Task.CompletedTask;
                }
            });
        }

        menuItems.Add(new MenuButtonItem
        {
            Text = _controlsLoc[nameof(ControlsStrings.ActionStructuredLogsText)],
            Icon = s_structuredLogsIcon,
            OnClick = () =>
            {
                _navigationManager.NavigateTo(DashboardUrls.StructuredLogsUrl(traceId: trace.TraceId));
                return Task.CompletedTask;
            }
        });

        menuItems.Add(new MenuButtonItem
        {
            Text = _controlsLoc[nameof(ControlsStrings.ViewJson)],
            Icon = s_bracesIcon,
            OnClick = async () =>
            {
                var result = ExportHelpers.GetTraceAsJson(trace, _telemetryRepository, _outgoingPeerResolvers);
                await TextVisualizerDialog.OpenDialogAsync(new OpenTextVisualizerDialogOptions
                {
                    DialogService = _dialogService,
                    ValueDescription = result.FileName,
                    Value = result.Content,
                    DownloadFileName = result.FileName,
                    FixedFormat = DashboardUIHelpers.JsonFormat
                }).ConfigureAwait(false);
            }
        });
    }
}
