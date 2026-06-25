// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Model.Assistant.Prompts;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Aspire.Dashboard.Components.Deck;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Builds menu items for structured log context menus and action buttons.
/// </summary>
public sealed class StructuredLogMenuBuilder
{
    private const DeckIconName ViewDetailsIcon = DeckIconName.Info;
    private const DeckIconName MessageOpenIcon = DeckIconName.External;
    private const DeckIconName BracesIcon = DeckIconName.Braces;
    private const DeckIconName GitHubCopilotIcon = DeckIconName.Sparkle;

    private readonly IStringLocalizer<StructuredLogs> _loc;
    private readonly IStringLocalizer<ControlsStrings> _controlsLoc;
    private readonly IStringLocalizer<AIAssistant> _aiAssistantLoc;
    private readonly IStringLocalizer<AIPrompts> _aiPromptsLoc;
    private readonly DashboardDialogService _dialogService;
    private readonly IAIContextProvider _aiContextProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructuredLogMenuBuilder"/> class.
    /// </summary>
    public StructuredLogMenuBuilder(
        IStringLocalizer<StructuredLogs> loc,
        IStringLocalizer<ControlsStrings> controlsLoc,
        IStringLocalizer<AIAssistant> aiAssistantLoc,
        IStringLocalizer<AIPrompts> aiPromptsLoc,
        DashboardDialogService dialogService,
        IAIContextProvider aiContextProvider)
    {
        _loc = loc;
        _controlsLoc = controlsLoc;
        _aiAssistantLoc = aiAssistantLoc;
        _aiPromptsLoc = aiPromptsLoc;
        _dialogService = dialogService;
        _aiContextProvider = aiContextProvider;
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
        if (showViewDetails)
        {
            menuItems.Add(new MenuButtonItem
            {
                Text = _controlsLoc[nameof(ControlsStrings.ActionViewDetailsText)],
                Icon = ViewDetailsIcon,
                OnClick = onViewDetails.InvokeAsync
            });
        }

        menuItems.Add(new MenuButtonItem
        {
            Text = _loc[nameof(StructuredLogs.ActionLogMessageText)],
            Icon = MessageOpenIcon,
            OnClick = async () =>
            {
                var header = _loc[nameof(StructuredLogs.StructuredLogsMessageColumnHeader)];
                await TextVisualizerDialog.OpenDialogAsync(new OpenTextVisualizerDialogOptions
                {
                    DialogService = _dialogService,
                    ValueDescription = header,
                    Value = logEntry.Message
                }).ConfigureAwait(false);
            }
        });

        menuItems.Add(new MenuButtonItem
        {
            Text = _controlsLoc[nameof(ControlsStrings.ViewJson)],
            Icon = BracesIcon,
            OnClick = async () =>
            {
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

        if (_aiContextProvider.Enabled)
        {
            menuItems.Add(new MenuButtonItem
            {
                Text = _aiAssistantLoc[nameof(AIAssistant.MenuTextAskGitHubCopilot)],
                Icon = GitHubCopilotIcon,
                OnClick = async () =>
                {
                    await _aiContextProvider.LaunchAssistantSidebarAsync(
                        promptContext => PromptContextsBuilder.AnalyzeLogEntry(
                            promptContext,
                            _aiPromptsLoc.GetString(nameof(AIPrompts.PromptAnalyzeLogEntry), logEntry.InternalId),
                            logEntry)).ConfigureAwait(false);
                }
            });
        }
    }
}
