// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class SettingsDialog : IDialogContentComponent, IDisposable
{
    private string? _currentSetting;
    private List<CultureInfo> _languageOptions = null!;
    private CultureInfo? _selectedUiCulture;
    private TimeFormat _timeFormat;

    private IDisposable? _themeChangedSubscription;

    [Inject]
    public required ThemeManager ThemeManager { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required DashboardTelemetryService TelemetryService { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [CascadingParameter]
    public FluentDialog Dialog { get; set; } = default!;

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required ILocalStorage LocalStorage { get; init; }

    protected override void OnInitialized()
    {
        _languageOptions = GlobalizationHelpers.OrderedLocalizedCultures;

        _selectedUiCulture = GlobalizationHelpers.TryGetKnownParentCulture(CultureInfo.CurrentUICulture, out var matchedCulture)
            ? matchedCulture :
            // Otherwise, Blazor has fallen back to a supported language
            CultureInfo.CurrentUICulture;

        _timeFormat = TimeProvider.ConfiguredTimeFormat;

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

    private async Task ThemeChangedAsync()
    {
        // The field is being transiently set to null when the value changes. Maybe a bug in FluentUI?
        // This should never be set to null by the dashboard so we can ignore null values.
        if (_currentSetting != null)
        {
            // The theme isn't changed here. Instead, the MainLayout subscribes to the change event
            // and applies the new theme to the browser window.
            await ThemeManager.RaiseThemeChangedAsync(_currentSetting);
        }
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

    private static void ValueChanged(string? value)
    {
        // Do nothing. Required for FluentUI Blazor to trigger SelectedOptionChanged.
    }

    private async Task LaunchManageDataAsync()
    {
        // Close the Settings dialog first to avoid concurrent focus traps causing a
        // "Maximum call stack size exceeded" error in the browser (see #14407).
        await Dialog.CloseAsync();

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

    private async Task OnTimeFormatChanged()
    {
        TimeProvider.SetConfiguredTimeFormat(_timeFormat);
        await LocalStorage.SetAsync(BrowserStorageKeys.TimeFormat, _timeFormat);

        // Reload the page to ensure all components pick up the new format
        var uri = new Uri(NavigationManager.Uri)
            .GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);

        NavigationManager.NavigateTo(uri, forceLoad: true);
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
