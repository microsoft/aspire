// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Builds menu items for resource context menus and action buttons.
/// </summary>
public sealed class ResourceMenuBuilder
{
    private static readonly Icon s_viewDetailsIcon = new Icons.Regular.Size16.Info();
    private static readonly Icon s_consoleLogsIcon = new Icons.Regular.Size16.SlideText();
    private static readonly Icon s_terminalIcon = new Icons.Regular.Size20.WindowConsole();
    private static readonly Icon s_structuredLogsIcon = new Icons.Regular.Size16.SlideTextSparkle();
    private static readonly Icon s_tracesIcon = new Icons.Regular.Size16.GanttChart();
    private static readonly Icon s_metricsIcon = new Icons.Regular.Size16.ChartMultiple();
    private static readonly Icon s_linkIcon = new Icons.Regular.Size16.Link();
    private static readonly Icon s_toolboxIcon = new Icons.Regular.Size16.Toolbox();
    private static readonly Icon s_linkMultipleIcon = new Icons.Regular.Size16.LinkMultiple();
    private static readonly Icon s_bracesIcon = new Icons.Regular.Size16.Braces();
    private static readonly Icon s_exportEnvIcon = new Icons.Regular.Size16.DocumentText();
    private static readonly Icon s_tagIcon = new Icons.Regular.Size16.Tag();

    private readonly NavigationManager _navigationManager;
    private readonly DashboardDataSource _dataSource;
    private readonly IStringLocalizer<ControlsStrings> _controlLoc;
    private readonly IStringLocalizer<Resources.Resources> _loc;
    private readonly IStringLocalizer<TerminalStrings> _terminalLoc;
    private readonly IconResolver _iconResolver;
    private readonly DashboardDialogService _dialogService;
    private readonly IDashboardClient _dashboardClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceMenuBuilder"/> class.
    /// </summary>
    public ResourceMenuBuilder(
        NavigationManager navigationManager,
        DashboardDataSource dataSource,
        IStringLocalizer<ControlsStrings> controlLoc,
        IStringLocalizer<Resources.Resources> loc,
        IStringLocalizer<TerminalStrings> terminalLoc,
        IconResolver iconResolver,
        DashboardDialogService dialogService,
        IDashboardClient dashboardClient)
    {
        _navigationManager = navigationManager;
        _dataSource = dataSource;
        _controlLoc = controlLoc;
        _loc = loc;
        _terminalLoc = terminalLoc;
        _iconResolver = iconResolver;
        _dialogService = dialogService;
        _dashboardClient = dashboardClient;
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
        bool showTerminalItem,
        bool showConsoleLogsItem,
        bool showUrls)
    {
        if (showViewDetails)
        {
            menuItems.Add(new MenuButtonItem
            {
                Text = _controlLoc[nameof(ControlsStrings.ActionViewDetailsText)],
                Icon = s_viewDetailsIcon,
                OnClick = onViewDetails.InvokeAsync
            });
        }

        if (showTerminalItem && CanViewTerminal(resource))
        {
            menuItems.Add(CreateTerminalMenuItem(resource, resourceByName));
        }

        if (showConsoleLogsItem)
        {
            menuItems.Add(CreateConsoleLogsMenuItem(resource, resourceByName));
        }

        menuItems.Add(CreateViewJsonMenuItem(resource, resourceByName));

        if (resource.Environment.Any(e => e.FromSpec))
        {
            menuItems.Add(new MenuButtonItem
            {
                Text = _controlLoc[nameof(ControlsStrings.ExportEnv)],
                Icon = s_exportEnvIcon,
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

        AddTelemetryMenuItems(menuItems, resource, resourceByName);

        AddCommandMenuItems(menuItems, resource, commandSelected, isCommandExecuting);

        if (showUrls)
        {
            AddUrlMenuItems(menuItems, resource);
        }
    }

    internal MenuButtonItem CreateViewJsonMenuItem(ResourceViewModel resource, IDictionary<string, ResourceViewModel> resourceByName)
    {
        return new MenuButtonItem
        {
            Text = _controlLoc[nameof(ControlsStrings.ViewJson)],
            Icon = s_bracesIcon,
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
        };
    }

    internal MenuButtonItem CreateViewOutputMenuItem(ResourceViewModel resource, IDictionary<string, ResourceViewModel> resourceByName)
    {
        return CanViewTerminal(resource)
            ? CreateTerminalMenuItem(resource, resourceByName)
            : CreateConsoleLogsMenuItem(resource, resourceByName);
    }

    private bool CanViewTerminal(ResourceViewModel resource)
    {
        return _dashboardClient.IsEnabled && !_dashboardClient.IsReadOnly && resource.HasTerminal();
    }

    private MenuButtonItem CreateTerminalMenuItem(ResourceViewModel resource, IDictionary<string, ResourceViewModel> resourceByName)
    {
        return new MenuButtonItem
        {
            Text = _terminalLoc[nameof(TerminalStrings.TerminalTitle)],
            Icon = s_terminalIcon,
            OnClick = () =>
            {
                _navigationManager.NavigateTo(DashboardUrls.TerminalsUrl(resource: ResourceViewModel.GetResourceName(resource, resourceByName)));
                return Task.CompletedTask;
            }
        };
    }

    private MenuButtonItem CreateConsoleLogsMenuItem(ResourceViewModel resource, IDictionary<string, ResourceViewModel> resourceByName)
    {
        return new MenuButtonItem
        {
            Text = _loc[nameof(Resources.Resources.ResourceActionConsoleLogsText)],
            Icon = s_consoleLogsIcon,
            OnClick = () =>
            {
                _navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: ResourceViewModel.GetResourceName(resource, resourceByName)));
                return Task.CompletedTask;
            }
        };
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
                Icon = s_linkMultipleIcon,
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
        return MenuButtonItem.CreateExternalLink(url.Text, url.Url!, s_linkIcon, tooltip: url.Url);
    }

    private void AddTelemetryMenuItems(List<MenuButtonItem> menuItems, ResourceViewModel resource, IDictionary<string, ResourceViewModel> resourceByName)
    {
        // Show telemetry menu items if there is telemetry for the resource.
        var telemetryResource = _dataSource.TelemetryRepository.GetResourceByCompositeName(resource.Name);
        if (telemetryResource != null)
        {
            menuItems.Add(new MenuButtonItem { IsDivider = true });

            if (!telemetryResource.UninstrumentedPeer)
            {
                menuItems.Add(new MenuButtonItem
                {
                    Text = _loc[nameof(Resources.Resources.ResourceActionStructuredLogsText)],
                    Tooltip = _loc[nameof(Resources.Resources.ResourceActionStructuredLogsText)],
                    Icon = s_structuredLogsIcon,
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
                Icon = s_tracesIcon,
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
                    Icon = s_metricsIcon,
                    OnClick = () =>
                    {
                        _navigationManager.NavigateTo(DashboardUrls.MetricsUrl(resource: ResourceViewModel.GetResourceName(resource, resourceByName)));
                        return Task.CompletedTask;
                    }
                });
            }
        }
    }

    internal List<MenuButtonItem> CreateTagGroupMenuItems(
        string tagName,
        string headerId,
        Func<IReadOnlyList<ResourceViewModel>> getMembers,
        Func<ResourceViewModel, CommandViewModel, bool> isCommandExecuting,
        Func<ResourceViewModel, CommandViewModel, Task> executeCommand) =>
        CreateGroupMenuItems(tagName, headerId, s_tagIcon, getMembers, isCommandExecuting, executeCommand, showEmptyGroup: false);

    internal List<MenuButtonItem> CreateGroupMenuItems(
        string groupName,
        string headerId,
        Icon? headerIcon,
        Func<IReadOnlyList<ResourceViewModel>> getMembers,
        Func<ResourceViewModel, CommandViewModel, bool> isCommandExecuting,
        Func<ResourceViewModel, CommandViewModel, Task> executeCommand,
        bool showEmptyGroup)
    {
        var members = getMembers();
        if (members.Count == 0 && !showEmptyGroup)
        {
            return [];
        }

        var memberNames = members.Select(r => r.Name).ToHashSet(StringComparers.ResourceName);
        var items = new List<MenuButtonItem>
        {
            new() { Id = headerId, IsHeader = true, Text = groupName, Icon = headerIcon }
        };
        var commands = members
            // Parameter actions operate on individual rows. Count parameters in the group total, but don't offer their commands for bulk execution.
            .Where(resource => !resource.IsParameter)
            .SelectMany(resource => resource.Commands
                .Where(command => command.State != CommandViewModelState.Hidden)
                .Select(command => (Resource: resource, Command: command)))
            .GroupBy(pair => pair.Command.Name, StringComparers.CommandName)
            .Select(group => (
                Command: group.First().Command,
                Highlighted: group.Any(pair => pair.Command.IsHighlighted),
                EligibleCount: group.Count(pair => CanExecute(pair.Resource, pair.Command))))
            .OrderByDescending(entry => entry.Highlighted)
            .ThenBy(entry => entry.Command.GetDisplayName(), StringComparer.CurrentCultureIgnoreCase);

        foreach (var (command, _, eligibleCount) in commands)
        {
            var item = CreateCommandMenuItem(command, () => ExecuteForGroupAsync(command.Name), isDisabled: eligibleCount == 0);
            if (eligibleCount > 0 && eligibleCount < members.Count)
            {
                item.Description = _loc[nameof(Resources.Resources.ResourceOverviewCountOfTotal), eligibleCount, members.Count];
            }
            items.Add(item);
        }

        if (items.Count == 1)
        {
            items.Add(new MenuButtonItem { Text = _loc[nameof(Resources.Resources.ResourcesGraphTagNoCommands)], IsDisabled = true });
        }

        return items;

        bool CanExecute(ResourceViewModel resource, CommandViewModel command) =>
            !resource.IsParameter && !_dashboardClient.IsReadOnly && command.State == CommandViewModelState.Enabled && !isCommandExecuting(resource, command);

        Task ExecuteForGroupAsync(string commandName)
        {
            // Re-read membership and commands at activation. Never extend the action to nodes added since the menu opened.
            return Task.WhenAll(getMembers().Where(resource => memberNames.Contains(resource.Name)).Select(async resource =>
            {
                var command = resource.Commands.FirstOrDefault(c => string.Equals(c.Name, commandName, StringComparisons.CommandName));
                if (command is not null && CanExecute(resource, command))
                {
                    await executeCommand(resource, command).ConfigureAwait(false);
                }
            }));
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
                Icon = s_toolboxIcon,
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

        MenuButtonItem CreateMenuItem(CommandViewModel command) => CreateCommandMenuItem(
            command,
            () => commandSelected.InvokeAsync(command),
            _dashboardClient.IsReadOnly || command.State == CommandViewModelState.Disabled || isCommandExecuting(resource, command));
    }

    private MenuButtonItem CreateCommandMenuItem(CommandViewModel command, Func<Task> onClick, bool isDisabled) => new()
    {
        Text = command.GetDisplayName(),
        Tooltip = command.GetDisplayDescription(),
        Icon = _iconResolver.ResolveCommandIcon(command.IconName, command.IconVariant),
        OnClick = onClick,
        IsDisabled = isDisabled
    };
}
