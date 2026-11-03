// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Layout;

public partial class MainLayout : IGlobalKeydownListener, IAsyncDisposable
{
    private bool _isNavMenuOpen;
    private bool _isDisposing;

    private bool _runSelectionChanged;
    private bool _isSwitchingRuns;
    private NavigationAvailability _navigationAvailability = NavigationAvailability.Default;
    // Fluent v5 has no API to notify the provider after mutating an existing toast's options. This value is
    // rendered as an additional provider attribute so changing it forces FluentToastProvider to read them again.
    private int _toastProviderUpdateVersion;

    private IDisposable? _themeChangedSubscription;
    private IDisposable? _locationChangingRegistration;
    private IJSObjectReference? _jsModule;
    private IJSObjectReference? _keyboardHandlers;
    private DotNetObjectReference<ShortcutManager>? _shortcutManagerReference;
    private DotNetObjectReference<MainLayout>? _layoutReference;
    private DashboardDialogReference? _openPageDialog;
    private TerminalDock? _terminalDock;
    private string? _pendingReturnFocusElementId;
    private bool _suppressNextDialogFocusRestore;
    private const string SettingsDialogId = "SettingsDialog";
    private const string HelpDialogId = "HelpDialog";
    private const string NotificationsDialogId = "NotificationsDialog";
    private const string RecordingsDialogId = "RecordingsDialog";
    internal const string HelpButtonId = "dashboard-help-button";
    internal const string SettingsButtonId = "dashboard-settings-button";
    internal const string NavigationButtonId = "dashboard-navigation-button";
    internal const string RunSelectButtonId = "dashboard-run-select-button";
    internal const string DashboardDocumentationUrl = "https://aspire.dev/dashboard/overview/";
    internal const string AspireRepoUrl = "https://aka.ms/aspire/repo";

    /// <summary>
    /// Name of the section in the desktop navigation row where nested layouts can render page navigation.
    /// </summary>
    internal const string HeaderContextSectionName = "header-context";

    [Inject]
    public required ThemeManager ThemeManager { get; init; }

    [Inject]
    public required IThemeService FluentThemeService { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Layout> Loc { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required ShortcutManager ShortcutManager { get; init; }

    [Inject]
    public required DashboardMessageBarService MessageService { get; init; }

    [Inject]
    public required IOptionsMonitor<DashboardOptions> Options { get; init; }

    [Inject]
    public required ILocalStorage LocalStorage { get; init; }

    [Inject]
    public required ISessionStorage SessionStorage { get; init; }

    [Inject]
    public required IDashboardRunStore RunStore { get; init; }

    [Inject]
    public required IDashboardRunSelection RunSelection { get; init; }

    [Inject]
    public required ILogger<MainLayout> Logger { get; init; }

    [Inject]
    public required DashboardViewPreferences ViewPreferences { get; init; }

    // The "Recordings" setting picks between the recordings icon and panel, and the "Live run" menu.
    private bool UseRunMenu => ViewPreferences.RecordingsStyle == RecordingsStyle.Menu;

    [Inject]
    public required Aspire.Dashboard.Model.INotificationService NotificationService { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    private bool IsTerminalDockEnabled => !_isSwitchingRuns && DashboardClient.IsEnabled && !DashboardClient.IsReadOnly;

    private Task ToggleTerminalDockAsync() => _terminalDock?.ToggleAsync() ?? Task.CompletedTask;

    protected override async Task OnInitializedAsync()
    {
        ViewPreferences.Changed += OnViewPreferencesChanged;
        await ViewPreferences.EnsureLoadedAsync();

        if (RunStore.SupportsRunSelection)
        {
            var selectedRunResult = await SessionStorage.GetAsync<string>(BrowserStorageKeys.SelectedDashboardRunId);
            var selectedRunId = selectedRunResult is { Success: true } ? selectedRunResult.Value : null;
            if (!_runSelectionChanged && !string.IsNullOrEmpty(selectedRunId))
            {
                try
                {
                    RunSelection.SelectRun(selectedRunId);
                }
                catch (Exception exception)
                {
                    Logger.LogError(exception, "Failed to restore dashboard run '{RunId}'. Falling back to the current run.", selectedRunId);
                    RunSelection.SelectRun(runId: null);
                }

                if (RunSelection.SelectedRun.IsCurrent)
                {
                    await SessionStorage.SetAsync(BrowserStorageKeys.SelectedDashboardRunId, string.Empty);
                }
            }
        }

        NotificationService.OnChange += HandleNotificationsChanged;

        // Theme change can be triggered from the settings dialog. This logic applies the new theme to the browser window.
        // Note that this event could be raised from a settings dialog opened in a different browser window.
        _themeChangedSubscription = ThemeManager.OnThemeChanged(async () =>
        {
            if (_jsModule is not null)
            {
                var newValue = ThemeManager.SelectedTheme!;
                await ApplyThemeAsync(newValue);
            }
        });

        // Redirect to the structured logs page if the dashboard has no resource service.
        if (!DashboardClient.IsEnabled)
        {
            _locationChangingRegistration = NavigationManager.RegisterLocationChangingHandler((context) =>
            {
                if (TargetLocationInterceptor.InterceptTargetLocation(NavigationManager.BaseUri, context.TargetLocation, out var newTargetLocation))
                {
                    context.PreventNavigation();
                    NavigationManager.NavigateTo(newTargetLocation);
                }

                return ValueTask.CompletedTask;
            });
        }

        try
        {
            var result = await JS.InvokeAsync<BrowserInfo>("window.getBrowserInfo");
            TimeProvider.SetBrowserTimeZone(result.TimeZone);
            TimeProvider.SetBrowserTimeFormat(result.Is24HourTime ? TimeFormat.TwentyFourHour : TimeFormat.TwelveHour);
            TelemetryContextProvider.SetBrowserUserAgent(result.UserAgent);

            var timeFormatResult = await LocalStorage.GetAsync<TimeFormat>(BrowserStorageKeys.TimeFormat);
            if (timeFormatResult.Success)
            {
                TimeProvider.SetConfiguredTimeFormat(timeFormatResult.Value);
            }

            await DisplayUnsecuredEndpointsMessageAsync();
            await DisplayDashboardFeedbackMessageAsync();
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "Dashboard layout initialization stopped because the circuit disconnected.");
        }
        catch (ObjectDisposedException ex) when (_isDisposing)
        {
            Logger.LogDebug(ex, "Dashboard layout initialization stopped because the layout was disposed.");
        }
    }

    private async Task DisplayDashboardFeedbackMessageAsync()
    {
        var dismissed = await LocalStorage.GetUnprotectedAsync<bool>(BrowserStorageKeys.DashboardFeedbackPromptDismissed);
        var dismissedForSession = await SessionStorage.GetAsync<bool>(BrowserStorageKeys.DashboardFeedbackPromptDismissedForSession);
        if ((dismissed.Success && dismissed.Value) || (dismissedForSession.Success && dismissedForSession.Value))
        {
            return;
        }

        await MessageService.ShowAsync(
            new DashboardMessageBarContent
            {
                Title = DialogsLoc[nameof(Resources.Dialogs.DashboardFeedbackPromptTitle)],
                Message = DialogsLoc[nameof(Resources.Dialogs.DashboardFeedbackPromptMessage)],
                PrimaryAction = DialogsLoc[nameof(Resources.Dialogs.DashboardFeedbackPromptShare)],
                SecondaryAction = DialogsLoc[nameof(Resources.Dialogs.DashboardFeedbackPromptIgnore)]
            },
            MessageBarIntent.Info,
            DashboardUIHelpers.MessageBarSection,
            OnDashboardFeedbackMessageClosedAsync);
    }

    private async Task OnDashboardFeedbackMessageClosedAsync(MessageBarResult result)
    {
        if (result.Data is false)
        {
            await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.DashboardFeedbackPromptDismissed, true);
            return;
        }

        await SessionStorage.SetAsync(BrowserStorageKeys.DashboardFeedbackPromptDismissedForSession, true);
        if (result.Data is true)
        {
            await InvokeAsync(LaunchDashboardFeedbackAsync);
        }
    }

    private async Task LaunchDashboardFeedbackAsync()
    {
        var parameters = new DialogParameters
        {
            Title = DialogsLoc[nameof(Resources.Dialogs.DashboardFeedbackDialogTitle)],
            PrimaryAction = string.Empty,
            SecondaryAction = string.Empty,
            Width = "min(640px, calc(100vw - 24px))"
        };
        var feedback = new DashboardFeedback { IsAppHost = DashboardClient.IsEnabled };
        await DialogService.ShowDialogAsync<DashboardFeedbackDialog>(feedback, parameters);
    }

    private async Task DisplayUnsecuredEndpointsMessageAsync()
    {
        var unsecuredEndpointsMessage = new StringBuilder();
        if (ShouldShowUnsecuredTelemetryMessage())
        {
            unsecuredEndpointsMessage.AppendLine(Loc[nameof(Resources.Layout.MessageUnsecuredEndpointTelemetryBody)]);
        }
        if (ShouldShowUnsecuredApiMessage())
        {
            unsecuredEndpointsMessage.AppendLine(Loc[nameof(Resources.Layout.MessageUnsecuredEndpointApiBody)]);
        }

        if (unsecuredEndpointsMessage.Length > 0)
        {
            // Check UnsecuredTelemetryMessageDismissedKey for backwards compatibility.
            var skipMessage = (await ShouldSkipMessageAsync(LocalStorage, BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey) ||
                await ShouldSkipMessageAsync(LocalStorage, BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey));

            if (!skipMessage)
            {
                await MessageService.ShowAsync(
                    new DashboardMessageBarContent
                    {
                        Title = Loc[nameof(Resources.Layout.MessageUnsecuredEndpointTitle)],
                        Message = unsecuredEndpointsMessage.ToString(),
                        LinkText = Loc[nameof(Resources.Layout.MessageUnsecuredEndpointLink)],
                        LinkUrl = "https://aka.ms/aspire/api-endpoint-unsecured"
                    },
                    MessageBarIntent.Warning,
                    DashboardUIHelpers.MessageBarSection,
                    _ => LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey, true));
            }
        }

        static async Task<bool> ShouldSkipMessageAsync(ILocalStorage localStorage, string storageKey)
        {
            var dismissedResult = await localStorage.GetUnprotectedAsync<bool>(storageKey);
            return dismissedResult.Success && dismissedResult.Value;
        }
    }

    private bool ShouldShowUnsecuredTelemetryMessage()
    {
        // Only show warning if at least one OTLP endpoint is configured
        return (Options.CurrentValue.Otlp.GetGrpcEndpointAddress() != null || Options.CurrentValue.Otlp.GetHttpEndpointAddress() != null) &&
               Options.CurrentValue.Otlp.AuthMode == OtlpAuthMode.Unsecured &&
               !Options.CurrentValue.Otlp.SuppressUnsecuredMessage;
    }

    private bool ShouldShowUnsecuredApiMessage()
    {
        // Only show warning if API is enabled and unsecured
        return !Options.CurrentValue.Api.Disabled.GetValueOrDefault() &&
               Options.CurrentValue.Api.AuthMode == ApiAuthMode.Unsecured;
    }

    private async Task ApplyThemeAsync(string theme)
    {
        var mode = theme switch
        {
            ThemeManager.ThemeSettingDark => ThemeMode.Dark,
            ThemeManager.ThemeSettingLight => ThemeMode.Light,
            _ => ThemeMode.System
        };

        await FluentThemeService.SetThemeAsync(mode);
        ThemeManager.EffectiveTheme = await _jsModule!.InvokeAsync<string>("updateTheme", theme);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_isDisposing)
        {
            return;
        }

        try
        {
            if (firstRender)
            {
                _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", $"/{Assets["js/app-theme.js"]}");
                await ThemeManager.EnsureInitializedAsync();
                await ApplyThemeAsync(ThemeManager.SelectedTheme ?? ThemeManager.ThemeSettingSystem);
                _shortcutManagerReference = DotNetObjectReference.Create(ShortcutManager);
                _layoutReference = DotNetObjectReference.Create(this);
                _keyboardHandlers = await JS.InvokeAsync<IJSObjectReference>("window.registerGlobalKeydownListener", _shortcutManagerReference);
                ShortcutManager.AddGlobalKeydownListener(this);
            }

            if (_pendingReturnFocusElementId is { } elementId && _openPageDialog is null)
            {
                _pendingReturnFocusElementId = null;
                await JS.InvokeVoidAsync("focusElement", elementId);
            }
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "Dashboard layout rendering stopped because the circuit disconnected.");
        }
        catch (ObjectDisposedException ex) when (_isDisposing)
        {
            Logger.LogDebug(ex, "Dashboard layout rendering stopped because the layout was disposed.");
        }
    }

    protected override void OnParametersSet()
    {
        if (ViewportInformation.IsDesktop && _isNavMenuOpen)
        {
            _isNavMenuOpen = false;
            CloseMobileNavMenu();
        }
    }

    private string GetDefaultReturnFocusElementId(string desktopButtonId) => ViewportInformation.IsDesktop ? desktopButtonId : NavigationButtonId;

    internal async Task SwitchDashboardRunAsync(string? runId)
    {
        _runSelectionChanged = true;
        var selectedRunId = RunSelection.SelectedRun is { IsCurrent: false } selectedRun ? selectedRun.RunId : null;
        if (string.Equals(runId, selectedRunId, StringComparison.Ordinal))
        {
            await SessionStorage.SetAsync(BrowserStorageKeys.SelectedDashboardRunId, runId ?? string.Empty);
            return;
        }

        _isSwitchingRuns = true;
        _navigationAvailability = NavigationAvailability.Default;
        await InvokeAsync(StateHasChanged);

        try
        {
            RunSelection.SelectRun(runId);
        }
        catch (Exception exception)
        {
            Logger.LogError(
                exception,
                "Failed to switch to dashboard run '{RunId}'. Keeping dashboard run '{SelectedRunId}' selected.",
                runId,
                RunSelection.SelectedRun.RunId);
        }
        finally
        {
            _isSwitchingRuns = false;
            await InvokeAsync(StateHasChanged);
        }

        var persistedRunId = RunSelection.SelectedRun is { IsCurrent: false } actualSelectedRun ? actualSelectedRun.RunId : string.Empty;
        await SessionStorage.SetAsync(BrowserStorageKeys.SelectedDashboardRunId, persistedRunId);
    }

    private string? GetVisibleReturnFocusElementId(string? returnFocusElementId, string desktopButtonId)
    {
        // Dialog launchers move between the desktop header and the mobile navigation menu.
        // Resolve the target when the dialog closes so viewport changes do not focus a removed element.
        return returnFocusElementId is null ? null : GetDefaultReturnFocusElementId(desktopButtonId);
    }

    private Task LaunchHelpAsync() => LaunchHelpAsync(GetDefaultReturnFocusElementId(HelpButtonId), HelpDialogTab.KeyboardShortcuts);

    // The help dialog has a tab for AI agents, so launching the agents help opens it on that tab.
    public Task LaunchAIAgentsAsync() => LaunchHelpAsync(GetDefaultReturnFocusElementId(HelpButtonId), HelpDialogTab.AIAgents);

    private async Task LaunchHelpAsync(string? returnFocusElementId, HelpDialogTab tab)
    {
        DialogParameters parameters = new()
        {
            Title = Loc[nameof(Resources.Layout.MainLayoutHelp)],
            PrimaryAction = Loc[nameof(Resources.Layout.MainLayoutSettingsDialogClose)],
            PrimaryActionEnabled = true,
            SecondaryAction = null,
            Modal = true,
            Alignment = HorizontalAlignment.Center,
            Width = "700px",
            Id = HelpDialogId,
            OnDialogClosing = EventCallback.Factory.Create<IDialogInstance>(this, _ => HandleDialogClose(GetVisibleReturnFocusElementId(returnFocusElementId, HelpButtonId)))
        };

        if (!await CloseOpenPageDialogForReplacementAsync(HelpDialogId).ConfigureAwait(true))
        {
            return;
        }

        _openPageDialog = await DialogService.ShowDialogAsync<HelpDialog>(tab, parameters).ConfigureAwait(true);
    }

    private void HandleDialogClose(string? returnFocusElementId = null)
    {
        _openPageDialog = null;
        if (!_suppressNextDialogFocusRestore)
        {
            _pendingReturnFocusElementId = returnFocusElementId;
        }
    }

    private async Task<bool> CloseOpenPageDialogForReplacementAsync(string dialogId)
    {
        if (_openPageDialog is null)
        {
            return true;
        }

        if (Equals(_openPageDialog.Id, dialogId) && !_openPageDialog.Result.IsCompleted)
        {
            return false;
        }

        _suppressNextDialogFocusRestore = true;
        try
        {
            await _openPageDialog.CloseAsync();
            _pendingReturnFocusElementId = null;
        }
        finally
        {
            _suppressNextDialogFocusRestore = false;
        }

        return true;
    }

    public Task LaunchSettingsAsync() => LaunchSettingsAsync(GetDefaultReturnFocusElementId(SettingsButtonId));

    private async Task LaunchSettingsAsync(string? returnFocusElementId)
    {
        var parameters = new DialogParameters
        {
            Title = Loc[nameof(Resources.Layout.MainLayoutSettingsDialogTitle)],
            PrimaryAction = Loc[nameof(Resources.Layout.MainLayoutSettingsDialogClose)].Value,
            SecondaryAction = null,
            Modal = true,
            Alignment = HorizontalAlignment.Right,
            Width = "min(480px, calc(100vw - 24px))",
            Id = SettingsDialogId,
            OnDialogClosing = EventCallback.Factory.Create<IDialogInstance>(this, _ => HandleDialogClose(GetVisibleReturnFocusElementId(returnFocusElementId, SettingsButtonId)))
        };

        if (!await CloseOpenPageDialogForReplacementAsync(SettingsDialogId).ConfigureAwait(true))
        {
            return;
        }

        // Ensure the currently set theme is immediately available to display in settings dialog.
        await ThemeManager.EnsureInitializedAsync();

        _openPageDialog = await DialogService.ShowPanelAsync<SettingsDialog>(parameters).ConfigureAwait(true);
    }

    public async Task LaunchNotificationsAsync()
    {
        var parameters = new DialogParameters
        {
            Title = Loc[nameof(Resources.Layout.MainLayoutNotificationCenterTitle)],
            PrimaryAction = Loc[nameof(Resources.Layout.MainLayoutSettingsDialogClose)].Value,
            SecondaryAction = null,
            Modal = true,
            Alignment = HorizontalAlignment.Right,
            Width = "350px",
            Id = NotificationsDialogId,
            OnDialogClosing = EventCallback.Factory.Create<IDialogInstance>(this, _ => HandleDialogClose())
        };

        if (!await CloseOpenPageDialogForReplacementAsync(NotificationsDialogId).ConfigureAwait(true))
        {
            return;
        }

        _openPageDialog = await DialogService.ShowPanelAsync<NotificationsDialog>(parameters).ConfigureAwait(true);
    }

    // The logo and the app name open the overview of all resources. Unlike the Resources entry of the rail, they don't
    // restore the last selected resources, so the stored selection is cleared first. The links keep "/" as their
    // address, so opening them in a new tab, which has no stored selection, shows the same view.
    private async Task GoToAllResourcesAsync()
    {
        if (DashboardClient.IsEnabled)
        {
            await SessionStorage.SetAsync(BrowserStorageKeys.LastSelectedResources, new List<string>());
            NavigationManager.NavigateTo(DashboardUrls.ResourceListUrl());
        }
        else
        {
            NavigationManager.NavigateTo(DashboardUrls.HomeUrl());
        }
    }

    private async Task LaunchRecordingsAsync()
    {
        var parameters = new DialogParameters
        {
            Title = Loc[nameof(Resources.Layout.DashboardRunSelectTitle)],
            PrimaryAction = Loc[nameof(Resources.Layout.MainLayoutSettingsDialogClose)].Value,
            SecondaryAction = null,
            Modal = true,
            Alignment = HorizontalAlignment.Right,
            Width = "350px",
            Id = RecordingsDialogId,
            // The recordings button is in the header on every viewport, so focus always returns to it.
            OnDialogClosing = EventCallback.Factory.Create<IDialogInstance>(this, _ => HandleDialogClose(RunSelectButtonId))
        };

        if (!await CloseOpenPageDialogForReplacementAsync(RecordingsDialogId).ConfigureAwait(true))
        {
            return;
        }

        var content = new DashboardRunsDialogContent(RunSelection.SelectedRun.RunId, SwitchDashboardRunAsync);
        _openPageDialog = await DialogService.ShowPanelAsync<DashboardRunsDialog>(content, parameters).ConfigureAwait(true);
    }

    private static readonly IReadOnlySet<AspireKeyboardShortcut> s_subscribedShortcuts = new HashSet<AspireKeyboardShortcut>
    {
        AspireKeyboardShortcut.Help,
        AspireKeyboardShortcut.Settings,
        AspireKeyboardShortcut.GoToStructuredLogs,
        AspireKeyboardShortcut.GoToTraces,
        AspireKeyboardShortcut.GoToMetrics,
        AspireKeyboardShortcut.GoToTelemetrySources
    };

    private static readonly IReadOnlySet<AspireKeyboardShortcut> s_resourceServiceSubscribedShortcuts = new HashSet<AspireKeyboardShortcut>(
        s_subscribedShortcuts)
    {
        AspireKeyboardShortcut.GoToResources,
        AspireKeyboardShortcut.GoToConsoleLogs,
        AspireKeyboardShortcut.GoToParameters,
        AspireKeyboardShortcut.GoToExtensions
    };

    public IReadOnlySet<AspireKeyboardShortcut> SubscribedShortcuts
    {
        get
        {
            if (!DashboardClient.IsEnabled)
            {
                return s_subscribedShortcuts;
            }

            var shortcuts = new HashSet<AspireKeyboardShortcut>(s_resourceServiceSubscribedShortcuts);
            if (Options.CurrentValue.UI.DisableResourceGraph != true)
            {
                shortcuts.Add(AspireKeyboardShortcut.GoToGraph);
            }
            if (_navigationAvailability.HasResourceTerminals)
            {
                shortcuts.Add(AspireKeyboardShortcut.GoToTerminals);
            }

            return shortcuts;
        }
    }

    public async Task OnPageKeyDownAsync(AspireKeyboardShortcut shortcut)
    {
        switch (shortcut)
        {
            case AspireKeyboardShortcut.Help:
                await LaunchHelpAsync();
                break;
            case AspireKeyboardShortcut.Settings:
                await LaunchSettingsAsync();
                break;
            case AspireKeyboardShortcut.GoToResources when DashboardClient.IsEnabled:
                NavigationManager.NavigateTo(DashboardUrls.ResourceListUrl());
                break;
            case AspireKeyboardShortcut.GoToConsoleLogs when DashboardClient.IsEnabled:
                NavigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl());
                break;
            case AspireKeyboardShortcut.GoToStructuredLogs:
                NavigationManager.NavigateTo(DashboardUrls.StructuredLogsUrl());
                break;
            case AspireKeyboardShortcut.GoToTraces:
                NavigationManager.NavigateTo(DashboardUrls.TracesUrl());
                break;
            case AspireKeyboardShortcut.GoToMetrics:
                NavigationManager.NavigateTo(DashboardUrls.MetricsUrl());
                break;
            case AspireKeyboardShortcut.GoToTerminals when DashboardClient.IsEnabled && _navigationAvailability.HasResourceTerminals:
                NavigationManager.NavigateTo(DashboardUrls.TerminalsUrl());
                break;
            case AspireKeyboardShortcut.GoToParameters when DashboardClient.IsEnabled:
                NavigationManager.NavigateTo(DashboardUrls.ParametersUrl());
                break;
            case AspireKeyboardShortcut.GoToGraph when DashboardClient.IsEnabled && Options.CurrentValue.UI.DisableResourceGraph != true:
                await ViewPreferences.SetGroupOverviewAsync(GroupOverview.Graph);
                NavigationManager.NavigateTo(DashboardUrls.ResourceListUrl());
                break;
            case AspireKeyboardShortcut.GoToExtensions when DashboardClient.IsEnabled:
                NavigationManager.NavigateTo(DashboardUrls.ExtensionsUrl());
                break;
            case AspireKeyboardShortcut.GoToTelemetrySources:
                NavigationManager.NavigateTo(DashboardUrls.TelemetrySourcesUrl());
                break;
        }
    }

    private void CloseMobileNavMenu()
    {
        _isNavMenuOpen = false;
        StateHasChanged();
    }

    private void HandleNotificationsChanged()
    {
        // Resource command toasts and notification-center entries are updated together. Use that notification
        // event to refresh any open toast whose retained ToastOptions instance was mutated with the result.
        _ = InvokeAsync(() =>
        {
            _toastProviderUpdateVersion++;
            StateHasChanged();
        });
    }

    private void OnViewPreferencesChanged() => _ = InvokeAsync(StateHasChanged);

    public async ValueTask DisposeAsync()
    {
        _isDisposing = true;
        ViewPreferences.Changed -= OnViewPreferencesChanged;

        _shortcutManagerReference?.Dispose();
        _layoutReference?.Dispose();
        _themeChangedSubscription?.Dispose();
        _locationChangingRegistration?.Dispose();
        NotificationService.OnChange -= HandleNotificationsChanged;
        ShortcutManager.RemoveGlobalKeydownListener(this);

        try
        {
            if (_keyboardHandlers is { } keyboardHandlers)
            {
                await JS.InvokeVoidAsync("window.unregisterGlobalKeydownListener", keyboardHandlers);
            }
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "Dashboard keyboard cleanup stopped because the circuit disconnected.");
        }

        await JSInteropHelpers.SafeDisposeAsync(_jsModule);
        await JSInteropHelpers.SafeDisposeAsync(_keyboardHandlers);
    }
}
