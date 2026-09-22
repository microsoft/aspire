// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class SettingsDialog : IAsyncDisposable
{
    private string? _currentSetting;
    private List<CultureInfo> _languageOptions = null!;
    private CultureInfo? _selectedUiCulture;
    private TimeFormat _timeFormat;
    private string? _terminalPalette = "dashboard";
    private string _savedTerminalPalette = "dashboard";
    private IJSObjectReference? _terminalModule;
    private bool _terminalPaletteSaveFailed;
    private bool _disposed;

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

    [Inject]
    public required ILogger<SettingsDialog> Logger { get; init; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var moduleUri = new Uri(new Uri(NavigationManager.BaseUri), "Components/Controls/TerminalView.razor.js");
            var module = await JS.InvokeAsync<IJSObjectReference>("import", moduleUri.PathAndQuery);
            var preference = await module.InvokeAsync<string>("getTerminalPalette");
            if (_disposed)
            {
                await JSInteropHelpers.SafeDisposeAsync(module);
                return;
            }
            _terminalModule = module;
            _savedTerminalPalette = preference;
            _terminalPalette = _savedTerminalPalette;
            if (!_disposed)
            {
                StateHasChanged();
            }
        }
    }

    private async Task TerminalPaletteChangedAsync()
    {
        // Fluent can transiently clear the binding while switching radio options.
        if (_terminalPalette is null || _terminalModule is null)
        {
            return;
        }
        var preference = _terminalPalette;
        try
        {
            await _terminalModule.InvokeVoidAsync("setTerminalPalette", preference);
            _savedTerminalPalette = preference;
            _terminalPaletteSaveFailed = false;
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Could not save terminal palette preference.");
            _terminalPalette = _savedTerminalPalette;
            _terminalPaletteSaveFailed = true;
        }
    }

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

    private async Task LaunchManageDataAsync()
    {
        var parameters = new DialogParameters
        {
            Title = Loc[nameof(Dashboard.Resources.Dialogs.ManageDataDialogTitle)],
            PrimaryAction = Loc[nameof(Dashboard.Resources.Dialogs.DialogCloseButtonText)],
            SecondaryAction = string.Empty,
            Width = "800px"
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

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _themeChangedSubscription?.Dispose();
        await JSInteropHelpers.SafeDisposeAsync(_terminalModule);
    }
}
