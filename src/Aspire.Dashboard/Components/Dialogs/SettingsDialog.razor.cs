// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class SettingsDialog : IDisposable
{
    private const string NewDashboardExperience = "modern";
    private const string PreviousDashboardExperience = "legacy";

    private string? _currentSetting;
    private List<CultureInfo> _languageOptions = null!;
    private CultureInfo? _selectedUiCulture;
    private TimeFormat _timeFormat;
    private IReadOnlyList<IconToggleOption<string>> _themeOptions = [];
    private IReadOnlyList<IconToggleOption<TimeFormat>> _timeFormatOptions = [];
    private IReadOnlyList<IconToggleOption<ResourceListDensity>> _densityOptions = [];
    private IReadOnlyList<IconToggleOption<GroupOverview>> _groupOverviewOptions = [];
    private IReadOnlyList<IconToggleOption<string>> _dashboardExperienceOptions = [];

    private IDisposable? _themeChangedSubscription;

    [CascadingParameter]
    public required IDialogInstance Dialog { get; set; }

    [Inject]
    public required ThemeManager ThemeManager { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required DashboardTelemetryService TelemetryService { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required ILocalStorage LocalStorage { get; init; }

    [Inject]
    public required DashboardViewPreferences ViewPreferences { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> ResourcesLoc { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    protected override void OnInitialized()
    {
        _languageOptions = GlobalizationHelpers.OrderedLocalizedCultures;

        _selectedUiCulture = GlobalizationHelpers.TryGetKnownParentCulture(CultureInfo.CurrentUICulture, out var matchedCulture)
            ? matchedCulture :
            // Otherwise, Blazor has fallen back to a supported language
            CultureInfo.CurrentUICulture;

        _timeFormat = TimeProvider.ConfiguredTimeFormat;
        _themeOptions =
        [
            new(ThemeManager.ThemeSettingSystem, new Icons.Regular.Size20.Desktop(), Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogSystemTheme)]),
            new(ThemeManager.ThemeSettingLight, new Icons.Regular.Size20.WeatherSunny(), Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogLightTheme)]),
            new(ThemeManager.ThemeSettingDark, new Icons.Regular.Size20.WeatherMoon(), Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogDarkTheme)]),
        ];
        _timeFormatOptions =
        [
            new(TimeFormat.System, new Icons.Regular.Size20.Desktop(), FormatTimeFormatOption(TimeFormat.System)),
            new(TimeFormat.TwelveHour, new Icons.Regular.Size20.Clock(), FormatTimeFormatOption(TimeFormat.TwelveHour), BadgeText: "12"),
            new(TimeFormat.TwentyFourHour, new Icons.Regular.Size20.Clock(), FormatTimeFormatOption(TimeFormat.TwentyFourHour), BadgeText: "24"),
        ];
        _densityOptions =
        [
            new(ResourceListDensity.Spacious, new Icons.Regular.Size20.TextBulletListLtr(), Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogResourceListDensitySpacious)]),
            new(ResourceListDensity.Comfortable, new Icons.Regular.Size20.AppsListDetail(), Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogResourceListDensityComfortable)]),
            new(ResourceListDensity.Compact, new Icons.Regular.Size20.AppsList(), Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogResourceListDensityCompact)]),
        ];
        _groupOverviewOptions = new IconToggleOption<GroupOverview>[]
        {
            new(GroupOverview.Cards, new Icons.Regular.Size20.Grid(), Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogGroupOverviewCards)]),
            new(GroupOverview.Summary, new Icons.Regular.Size20.Home(), Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogGroupOverviewSummary)]),
            new(GroupOverview.Graph, new Icons.Regular.Size20.ShareAndroid(), ResourcesLoc[nameof(Dashboard.Resources.Resources.GraphPageTitle)]),
        }
        .Where(option => option.Value != GroupOverview.Graph || ViewPreferences.IsGraphEnabled)
        .ToList();
        var dashboardExperienceOptions = new List<IconToggleOption<string>>
        {
            new(NewDashboardExperience, new Icons.Regular.Size24.Sparkle(), Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogDashboardExperienceNew)])
        };
        if (DashboardExperienceComponents.IsLegacyAvailable)
        {
            dashboardExperienceOptions.Add(
                new(PreviousDashboardExperience, new Icons.Regular.Size24.History(), Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogDashboardExperiencePrevious)]));
        }
        _dashboardExperienceOptions = dashboardExperienceOptions;
        _currentSetting = ThemeManager.SelectedTheme ?? ThemeManager.ThemeSettingSystem;

        // Handle value being changed in a different browser window.
        _themeChangedSubscription = ThemeManager.OnThemeChanged(async () =>
        {
            var newValue = ThemeManager.SelectedTheme!;
            if (_currentSetting != newValue)
            {
                _currentSetting = newValue;
                await InvokeAsync(StateHasChanged);
            }
        });
    }

    private async Task ThemeChangedAsync(string theme)
    {
        _currentSetting = theme;
        // The theme isn't changed here. Instead, the MainLayout subscribes to the change event
        // and applies the new theme to the browser window.
        await ThemeManager.RaiseThemeChangedAsync(theme);
    }

    private void OnLanguageChanged()
    {
        if (_selectedUiCulture is null || string.Equals(CultureInfo.CurrentUICulture.Name, _selectedUiCulture.Name, StringComparisons.CultureName))
        {
            return;
        }

        var uri = new Uri(NavigationManager.Uri)
            .GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);

        // A cookie (CookieRequestCultureProvider.DefaultCookieName) must be set and the page reloaded to use the new culture set by the localization middleware.
        NavigationManager.NavigateTo(
            DashboardUrls.SetLanguageUrl(_selectedUiCulture.Name, uri),
            forceLoad: true);
    }

    private Task LaunchManageDataAsync() => DialogService.ShowManageDataAsync();

    private Task CloseAsync() => Dialog.CloseAsync();

    private async Task OnDashboardExperienceChangedAsync(string experience)
    {
        if (experience == NewDashboardExperience)
        {
            await SwitchDashboardExperienceAsync("modern");
            return;
        }

        var parameters = new DialogParameters
        {
            Title = Loc[nameof(Dashboard.Resources.Dialogs.DashboardFeedbackDialogTitle)],
            PrimaryAction = string.Empty,
            SecondaryAction = string.Empty,
            Width = "min(640px, calc(100vw - 24px))"
        };
        var feedback = new DashboardFeedback
        {
            IsAppHost = DashboardClient.IsEnabled,
            SwitchToLegacyAsync = () => SwitchDashboardExperienceAsync("legacy")
        };
        await DialogService.ShowDialogAsync<DashboardFeedbackDialog>(feedback, parameters);
    }

    internal async Task SwitchDashboardExperienceAsync(string experience) =>
        await JS.InvokeVoidAsync(
            "setDashboardExperience",
            experience == PreviousDashboardExperience && DashboardExperienceComponents.IsLegacyAvailable ? PreviousDashboardExperience : NewDashboardExperience,
            experience == PreviousDashboardExperience && DashboardExperienceComponents.IsLegacyAvailable ? "/legacy" : "/");

    private async Task OnTimeFormatChanged(TimeFormat timeFormat)
    {
        _timeFormat = timeFormat;
        TimeProvider.SetConfiguredTimeFormat(_timeFormat);
        await LocalStorage.SetAsync(BrowserStorageKeys.TimeFormat, _timeFormat);

        // Reload the page to ensure all components pick up the new format
        var uri = new Uri(NavigationManager.Uri)
            .GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);

        NavigationManager.NavigateTo(uri, forceLoad: true);
    }

    protected override async Task OnInitializedAsync()
    {
        // The preferences are usually loaded by the views that apply them, but settings can open first.
        ViewPreferences.Changed += OnViewPreferencesChanged;
        await ViewPreferences.EnsureLoadedAsync();
    }

    private void OnViewPreferencesChanged() => InvokeAsync(StateHasChanged);

    private string FormatTimeFormatOption(TimeFormat format) => format switch
    {
        TimeFormat.System => Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogTimeFormatSystem)],
        TimeFormat.TwelveHour => Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogTimeFormatTwelveHour)],
        TimeFormat.TwentyFourHour => Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogTimeFormatTwentyFourHour)],
        _ => format.ToString()
    };

    public void Dispose()
    {
        _themeChangedSubscription?.Dispose();
        ViewPreferences.Changed -= OnViewPreferencesChanged;
    }
}
