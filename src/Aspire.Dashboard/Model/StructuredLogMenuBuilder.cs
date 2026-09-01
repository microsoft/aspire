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
/// Builds menu items for structured log context menus and action buttons.
/// </summary>
public sealed class StructuredLogMenuBuilder
{
    private static readonly Icon s_viewDetailsIcon = new Icons.Regular.Size16.Info();
    private static readonly Icon s_messageOpenIcon = new Icons.Regular.Size16.Open();
    private static readonly Icon s_bracesIcon = new Icons.Regular.Size16.Braces();

    private readonly IStringLocalizer<StructuredLogs> _loc;
    private readonly IStringLocalizer<ControlsStrings> _controlsLoc;
    private readonly DashboardDialogService _dialogService;
    private readonly DashboardDataSource _dataSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructuredLogMenuBuilder"/> class.
    /// </summary>
    public StructuredLogMenuBuilder(
        IStringLocalizer<StructuredLogs> loc,
        IStringLocalizer<ControlsStrings> controlsLoc,
        DashboardDialogService dialogService,
        DashboardDataSource dataSource)
    {
        _loc = loc;
        _controlsLoc = controlsLoc;
        _dialogService = dialogService;
        _dataSource = dataSource;
    }

    /// <summary>
    /// Adds menu items for a structured log entry to the provided list.
    /// </summary>
    /// <param name="menuItems">The list to add menu items to.</param>
    /// <param name="logEntry">The log entry to create menu items for.</param>
    /// <param name="onViewDetails">Callback when View Details is clicked. Ignored when <paramref name="showViewDetails"/> is <c>false</c>.</param>
    /// <param name="showViewDetails">Whether to include the View Details menu item. Defaults to <c>true</c>.</param>
    public void AddMenuItems(
        List<MenuButtonItem> menuItems,
        OtlpLogEntry logEntry,
        EventCallback onViewDetails,
        bool showViewDetails = true)
    {
        AddMenuItems(menuItems, logEntry.Message, () => logEntry, onViewDetails, showViewDetails);
    }

    /// <summary>
    /// Adds menu items for a structured log summary to the provided list.
    /// </summary>
    /// <param name="menuItems">The list to add menu items to.</param>
    /// <param name="summary">The log summary to create menu items for.</param>
    /// <param name="onViewDetails">Callback when View Details is clicked. Ignored when <paramref name="showViewDetails"/> is <c>false</c>.</param>
    /// <param name="showViewDetails">Whether to include the View Details menu item. Defaults to <c>true</c>.</param>
    public void AddMenuItems(
        List<MenuButtonItem> menuItems,
        LogSummary summary,
        EventCallback onViewDetails,
        bool showViewDetails = true)
    {
        AddMenuItems(menuItems, summary.Message, () => _dataSource.TelemetryRepository.GetLog(summary.InternalId), onViewDetails, showViewDetails);
    }

    private void AddMenuItems(
        List<MenuButtonItem> menuItems,
        string message,
        Func<OtlpLogEntry?> getLogEntry,
        EventCallback onViewDetails,
        bool showViewDetails)
    {
        if (showViewDetails)
        {
            menuItems.Add(new MenuButtonItem
            {
                Text = _controlsLoc[nameof(ControlsStrings.ActionViewDetailsText)],
                Icon = s_viewDetailsIcon,
                OnClick = onViewDetails.InvokeAsync
            });
        }

        menuItems.Add(new MenuButtonItem
        {
            Text = _loc[nameof(StructuredLogs.ActionLogMessageText)],
            Icon = s_messageOpenIcon,
            OnClick = async () =>
            {
                var header = _loc[nameof(StructuredLogs.StructuredLogsMessageColumnHeader)];
                await TextVisualizerDialog.OpenDialogAsync(new OpenTextVisualizerDialogOptions
                {
                    DialogService = _dialogService,
                    ValueDescription = header,
                    Value = message
                }).ConfigureAwait(false);
            }
        });

        menuItems.Add(new MenuButtonItem
        {
            Text = _controlsLoc[nameof(ControlsStrings.ViewJson)],
            Icon = s_bracesIcon,
            OnClick = async () =>
            {
                var logEntry = getLogEntry();
                if (logEntry is null)
                {
                    return;
                }

                var result = ExportHelpers.GetLogEntryAsJson(logEntry);
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
