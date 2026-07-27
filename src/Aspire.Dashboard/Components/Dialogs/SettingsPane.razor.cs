// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

/// <summary>
/// Deck-style settings surface shown as a right-side pane (replaces the Fluent SettingsDialog panel).
/// Carries the same theme / language / time-format / manage-data settings; only the rendering surface
/// differs. Manage-data still opens the existing Fluent dialog for now.
/// </summary>
public partial class SettingsPane : IDisposable
{
    private string? _currentSetting;
    private List<CultureInfo> _languageOptions = null!;
    private CultureInfo? _selectedUiCulture;
    private TimeFormat _timeFormat;

    private IDisposable? _themeChangedSubscription;

    /// <summary>Invoked when the pane is dismissed.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>
    /// Invoked when the user activates "Manage data". When set, the host opens the Deck
    /// ManageDataPane; otherwise the pane falls back to the Fluent ManageDataDialog.
    /// </summary>
    [Parameter]
    public EventCallback OnManageData { get; set; }

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

    protected override void OnInitialized()
    {
        _languageOptions = GlobalizationHelpers.OrderedLocalizedCultures;

        _selectedUiCulture = GlobalizationHelpers.TryGetKnownParentCulture(CultureInfo.CurrentUICulture, out var matchedCulture)
            ? matchedCulture
            : CultureInfo.CurrentUICulture;

        _timeFormat = TimeProvider.ConfiguredTimeFormat;
        _currentSetting = ThemeManager.SelectedTheme ?? ThemeManager.ThemeSettingSystem;

        // Handle the value being changed in a different browser window.
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

    private async Task SetThemeAsync(string theme)
    {
        if (_currentSetting == theme)
        {
            return;
        }

        _currentSetting = theme;

        // The theme isn't applied here. MainLayout subscribes to the change event and applies the
        // new theme to the browser window.
        await ThemeManager.RaiseThemeChangedAsync(theme);
    }

    private void OnLanguageChanged(ChangeEventArgs e)
    {
        var name = e.Value?.ToString();
        if (string.IsNullOrEmpty(name) || string.Equals(CultureInfo.CurrentUICulture.Name, name, StringComparisons.CultureName))
        {
            return;
        }

        var uri = new Uri(NavigationManager.Uri)
            .GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);

        // A cookie (CookieRequestCultureProvider.DefaultCookieName) must be set and the page reloaded
        // for the localization middleware to apply the new culture.
        NavigationManager.NavigateTo(
            DashboardUrls.SetLanguageUrl(name, uri),
            forceLoad: true);
    }

    private async Task SetTimeFormatAsync(TimeFormat format)
    {
        if (_timeFormat == format)
        {
            return;
        }

        _timeFormat = format;
        TimeProvider.SetConfiguredTimeFormat(format);
        await LocalStorage.SetAsync(BrowserStorageKeys.TimeFormat, format);

        // Reload so every component picks up the new format.
        var uri = new Uri(NavigationManager.Uri)
            .GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);

        NavigationManager.NavigateTo(uri, forceLoad: true);
    }

    private async Task LaunchManageDataAsync()
    {
        // On desktop the settings pane delegates "Manage data" to the host (MainLayout), which closes
        // this pane and opens the Deck ManageDataPane. Fall back to the Fluent dialog if no host
        // handler is wired (keeps the surface usable in isolation).
        if (OnManageData.HasDelegate)
        {
            await OnManageData.InvokeAsync();
            return;
        }

        // Close the settings pane first to avoid concurrent focus traps.
        if (OnClose.HasDelegate)
        {
            await OnClose.InvokeAsync();
        }

        var parameters = new DialogParameters
        {
            Title = Loc[nameof(Dashboard.Resources.Dialogs.ManageDataDialogTitle)],
            PrimaryAction = Loc[nameof(Dashboard.Resources.Dialogs.DialogCloseButtonText)],
            SecondaryAction = string.Empty,
            Width = "800px",
            Height = "auto"
        };
        await DialogService.ShowDialogAsync<ManageDataDialog>(parameters);
    }

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
    }
}
