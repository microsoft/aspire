// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Model.Assistant.Prompts;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Builds menu items for resource context menus and action buttons.
/// </summary>
public sealed class ResourceMenuBuilder
{
    private const DeckIconName ViewDetailsIcon = DeckIconName.Info;
    private const DeckIconName ConsoleLogsIcon = DeckIconName.Console;
    private const DeckIconName StructuredLogsIcon = DeckIconName.Logs;
    private const DeckIconName TracesIcon = DeckIconName.Traces;
    private const DeckIconName MetricsIcon = DeckIconName.Metrics;
    private const DeckIconName LinkIcon = DeckIconName.Link;
    private const DeckIconName GitHubCopilotIcon = DeckIconName.Sparkle;
    private const DeckIconName ToolboxIcon = DeckIconName.Toolbox;
    private const DeckIconName LinkMultipleIcon = DeckIconName.Link;
    private const DeckIconName BracesIcon = DeckIconName.Braces;
    private const DeckIconName ExportEnvIcon = DeckIconName.DocumentText;

    private readonly NavigationManager _navigationManager;
    private readonly TelemetryRepository _telemetryRepository;
    private readonly IAIContextProvider _aiContextProvider;
    private readonly IStringLocalizer<ControlsStrings> _controlLoc;
    private readonly IStringLocalizer<Resources.Resources> _loc;
    private readonly IStringLocalizer<Resources.AIAssistant> _aiAssistantLoc;
    private readonly IStringLocalizer<Resources.AIPrompts> _aiPromptsLoc;
    private readonly DashboardDialogService _dialogService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceMenuBuilder"/> class.
    /// </summary>
    public ResourceMenuBuilder(
        NavigationManager navigationManager,
        TelemetryRepository telemetryRepository,
        IAIContextProvider aiContextProvider,
        IStringLocalizer<ControlsStrings> controlLoc,
        IStringLocalizer<Resources.Resources> loc,
        IStringLocalizer<Resources.AIAssistant> aiAssistantLoc,
        IStringLocalizer<Resources.AIPrompts> aiPromptsLoc,
        DashboardDialogService dialogService)
    {
        _navigationManager = navigationManager;
        _telemetryRepository = telemetryRepository;
        _aiContextProvider = aiContextProvider;
        _controlLoc = controlLoc;
        _loc = loc;
        _aiAssistantLoc = aiAssistantLoc;
        _aiPromptsLoc = aiPromptsLoc;
        _dialogService = dialogService;
    }

    /// <summary>
    /// Adds menu items for a resource to the provided list.
    /// </summary>
    public void AddMenuItems(
        List<MenuButtonItem> menuItems,
        ResourceViewModel resource,
        IDictionary<string, ResourceViewModel> resourceByName,
        EventCallback onViewDetails,
        EventCallback<CommandViewModel> commandSelected,
        Func<ResourceViewModel, CommandViewModel, bool> isCommandExecuting,
        bool showViewDetails,
        bool showConsoleLogsItem,
        bool showUrls)
    {
        if (showViewDetails)
        {
            menuItems.Add(new MenuButtonItem
            {
                Text = _controlLoc[nameof(ControlsStrings.ActionViewDetailsText)],
                Icon = ViewDetailsIcon,
                OnClick = onViewDetails.InvokeAsync
            });
        }

        if (showConsoleLogsItem)
        {
            menuItems.Add(new MenuButtonItem
            {
                Text = _loc[nameof(Resources.Resources.ResourceActionConsoleLogsText)],
                Icon = ConsoleLogsIcon,
                OnClick = () =>
                {
                    _navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: ResourceViewModel.GetResourceName(resource, resourceByName)));
                    return Task.CompletedTask;
                }
            });
        }

        menuItems.Add(new MenuButtonItem
        {
            Text = _controlLoc[nameof(ControlsStrings.ViewJson)],
            Icon = BracesIcon,
            OnClick = async () =>
            {
                var result = ExportHelpers.GetResourceAsJson(resource, resourceByName);
                await TextVisualizerDialog.OpenDialogAsync(new OpenTextVisualizerDialogOptions
                {
                    DialogService = _dialogService,
                    ValueDescription = result.FileName,
                    Value = result.Content,
                    DownloadFileName = result.FileName,
                    ContainsSecret = true,
                    FixedFormat = DashboardUIHelpers.JsonFormat
                }).ConfigureAwait(false);
            }
        });

        if (resource.Environment.Any(e => e.FromSpec))
        {
            menuItems.Add(new MenuButtonItem
            {
                Text = _controlLoc[nameof(ControlsStrings.ExportEnv)],
                Icon = ExportEnvIcon,
                OnClick = async () =>
                {
                    var result = ExportHelpers.GetEnvironmentVariablesAsEnvFile(resource, resourceByName);
                    await TextVisualizerDialog.OpenDialogAsync(new OpenTextVisualizerDialogOptions
                    {
                        DialogService = _dialogService,
                        ValueDescription = result.FileName,
                        Value = result.Content,
                        DownloadFileName = result.FileName,
                        ContainsSecret = true,
                        FixedFormat = DashboardUIHelpers.PropertiesFormat
                    }).ConfigureAwait(false);
                }
            });
        }

        if (_aiContextProvider.Enabled)
        {
            menuItems.Add(new MenuButtonItem
            {
                Text = _aiAssistantLoc[nameof(AIAssistant.MenuTextAskGitHubCopilot)],
                Icon = GitHubCopilotIcon,
                OnClick = async () =>
                {
                    await _aiContextProvider.LaunchAssistantSidebarAsync(
                        promptContext => PromptContextsBuilder.AnalyzeResource(
                            promptContext,
                            _aiPromptsLoc.GetString(nameof(AIPrompts.PromptAnalyzeResource), resource.Name),
                            resource)).ConfigureAwait(false);
                }
            });
        }

        AddTelemetryMenuItems(menuItems, resource, resourceByName);

        AddCommandMenuItems(menuItems, resource, commandSelected, isCommandExecuting);

        if (showUrls)
        {
            AddUrlMenuItems(menuItems, resource);
        }
    }

    private void AddUrlMenuItems(List<MenuButtonItem> menuItems, ResourceViewModel resource)
    {
        var urls = ResourceUrlHelpers.GetUrls(resource, includeInternalUrls: false, includeNonEndpointUrls: true)
            .Where(u => !string.IsNullOrEmpty(u.Url))
            .ToList();

        if (urls.Count == 0)
        {
            return;
        }

        menuItems.Add(new MenuButtonItem { IsDivider = true });

        if (urls.Count > 5)
        {
            var urlItems = new List<MenuButtonItem>();

            foreach (var url in urls)
            {
                urlItems.Add(CreateUrlMenuItem(url));
            }

            menuItems.Add(new MenuButtonItem
            {
                Text = _loc[nameof(Resources.Resources.ResourceActionUrlsText)],
                Tooltip = "", // No tooltip for the commands menu item.
                Icon = LinkMultipleIcon,
                NestedMenuItems = urlItems
            });
        }
        else
        {
            foreach (var url in urls)
            {
                menuItems.Add(CreateUrlMenuItem(url));
            }
        }
    }

    private static MenuButtonItem CreateUrlMenuItem(DisplayedUrl url)
    {
        // Opens the URL in a new window when clicked.
        // It's important that this is done in the onclick event so the browser popup allows it.
        return new MenuButtonItem
        {
            Text = url.Text,
            Tooltip = url.Url,
            Icon = LinkIcon,
            AdditionalAttributes = new Dictionary<string, object>
            {
                ["data-openbutton"] = "true",
                ["data-url"] = url.Url!,
                ["data-target"] = "_blank"
            }
        };
    }

    private void AddTelemetryMenuItems(List<MenuButtonItem> menuItems, ResourceViewModel resource, IDictionary<string, ResourceViewModel> resourceByName)
    {
        // Show telemetry menu items if there is telemetry for the resource.
        var telemetryResource = _telemetryRepository.GetResourceByCompositeName(resource.Name);
        if (telemetryResource != null)
        {
            menuItems.Add(new MenuButtonItem { IsDivider = true });

            if (!telemetryResource.UninstrumentedPeer)
            {
                menuItems.Add(new MenuButtonItem
                {
                    Text = _loc[nameof(Resources.Resources.ResourceActionStructuredLogsText)],
                    Tooltip = _loc[nameof(Resources.Resources.ResourceActionStructuredLogsText)],
                    Icon = StructuredLogsIcon,
                    OnClick = () =>
                    {
                        _navigationManager.NavigateTo(DashboardUrls.StructuredLogsUrl(resource: ResourceViewModel.GetResourceName(resource, resourceByName)));
                        return Task.CompletedTask;
                    }
                });
            }

            menuItems.Add(new MenuButtonItem
            {
                Text = _loc[nameof(Resources.Resources.ResourceActionTracesText)],
                Tooltip = _loc[nameof(Resources.Resources.ResourceActionTracesText)],
                Icon = TracesIcon,
                OnClick = () =>
                {
                    _navigationManager.NavigateTo(DashboardUrls.TracesUrl(resource: ResourceViewModel.GetResourceName(resource, resourceByName)));
                    return Task.CompletedTask;
                }
            });

            if (!telemetryResource.UninstrumentedPeer)
            {
                menuItems.Add(new MenuButtonItem
                {
                    Text = _loc[nameof(Resources.Resources.ResourceActionMetricsText)],
                    Tooltip = _loc[nameof(Resources.Resources.ResourceActionMetricsText)],
                    Icon = MetricsIcon,
                    OnClick = () =>
                    {
                        _navigationManager.NavigateTo(DashboardUrls.MetricsUrl(resource: ResourceViewModel.GetResourceName(resource, resourceByName)));
                        return Task.CompletedTask;
                    }
                });
            }
        }
    }

    private void AddCommandMenuItems(List<MenuButtonItem> menuItems, ResourceViewModel resource, EventCallback<CommandViewModel> commandSelected, Func<ResourceViewModel, CommandViewModel, bool> isCommandExecuting)
    {
        var menuCommands = resource.Commands
                    .Where(c => c.State != CommandViewModelState.Hidden)
                    .ToList();

        if (menuCommands.Count == 0)
        {
            return;
        }

        var highlightedMenuCommands = menuCommands.Where(c => c.IsHighlighted).ToList();
        var otherMenuCommands = menuCommands.Where(c => !c.IsHighlighted).ToList();

        menuItems.Add(new MenuButtonItem { IsDivider = true });

        // Always show the highlighted commands first and not in a sub-menu.
        foreach (var highlightedCommand in highlightedMenuCommands)
        {
            menuItems.Add(CreateMenuItem(highlightedCommand));
        }

        // If there are more than 5 commands, we group them under a "Commands" menu item. This is done to avoid the menu going off the end of the screen.
        // A scenario where this could happen is viewing the menu for a resource and the resource is in the middle of the screen.
        if (highlightedMenuCommands.Count + otherMenuCommands.Count > 5)
        {
            var commands = new List<MenuButtonItem>();

            foreach (var command in otherMenuCommands)
            {
                commands.Add(CreateMenuItem(command));
            }

            menuItems.Add(new MenuButtonItem
            {
                Text = _loc[nameof(Resources.Resources.ResourceActionCommandsText)],
                Tooltip = "", // No tooltip for the commands menu item.
                Icon = ToolboxIcon,
                NestedMenuItems = commands
            });
        }
        else
        {
            foreach (var command in otherMenuCommands)
            {
                menuItems.Add(CreateMenuItem(command));
            }
        }

        MenuButtonItem CreateMenuItem(CommandViewModel command)
        {
            return new MenuButtonItem
            {
                Text = command.GetDisplayName(),
                Tooltip = command.GetDisplayDescription(),
                FluentIconName = command.IconName,
                FluentIconVariant = command.IconVariant,
                OnClick = () => commandSelected.InvokeAsync(command),
                IsDisabled = command.State == CommandViewModelState.Disabled || isCommandExecuting(resource, command)
            };
        }
    }
}
