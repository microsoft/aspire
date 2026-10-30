// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Legacy.Dialogs;

public partial class LegacySettingsDialog : IDisposable
{
    private const string NewDashboardExperience = "modern";
    private const string PreviousDashboardExperience = "legacy";

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

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required ILocalStorage LocalStorage { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    protected override void OnInitialized()
    {
        _languageOptions = GlobalizationHelpers.OrderedLocalizedCultures;
        _selectedUiCulture = GlobalizationHelpers.TryGetKnownParentCulture(CultureInfo.CurrentUICulture, out var matchedCulture)
            ? matchedCulture
            : CultureInfo.CurrentUICulture;
        _timeFormat = TimeProvider.ConfiguredTimeFormat;
        _currentSetting = ThemeManager.SelectedTheme ?? ThemeManager.ThemeSettingSystem;

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
        if (_currentSetting is not null)
        {
            await ThemeManager.RaiseThemeChangedAsync(_currentSetting);
        }
    }

    private void OnLanguageChanged()
    {
        if (_selectedUiCulture is null ||
            string.Equals(CultureInfo.CurrentUICulture.Name, _selectedUiCulture.Name, StringComparisons.CultureName))
        {
            return;
        }

        var uri = new Uri(NavigationManager.Uri).GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);
        NavigationManager.NavigateTo(DashboardUrls.SetLanguageUrl(_selectedUiCulture.Name, uri), forceLoad: true);
    }

    private Task LaunchManageDataAsync() => DialogService.ShowManageDataAsync();

    private async Task OnDashboardExperienceChangedAsync(string experience)
    {
        if (experience != NewDashboardExperience)
        {
            return;
        }

        await JS.InvokeVoidAsync("setDashboardExperience", "modern", "/");
    }

    private async Task OnTimeFormatChanged()
    {
        TimeProvider.SetConfiguredTimeFormat(_timeFormat);
        await LocalStorage.SetAsync(BrowserStorageKeys.TimeFormat, _timeFormat);
        var uri = new Uri(NavigationManager.Uri).GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);
        NavigationManager.NavigateTo(uri, forceLoad: true);
    }

    private string FormatTimeFormatOption(TimeFormat format) => format switch
    {
        TimeFormat.System => Loc[nameof(Resources.Dialogs.SettingsDialogTimeFormatSystem)],
        TimeFormat.TwelveHour => Loc[nameof(Resources.Dialogs.SettingsDialogTimeFormatTwelveHour)],
        TimeFormat.TwentyFourHour => Loc[nameof(Resources.Dialogs.SettingsDialogTimeFormatTwentyFourHour)],
        _ => format.ToString()
    };

    public void Dispose()
    {
        _themeChangedSubscription?.Dispose();
    }
}
