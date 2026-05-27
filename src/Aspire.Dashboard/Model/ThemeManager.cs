// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Model;

public sealed record ThemeSettings(string? SelectedTheme, string EffectiveTheme);

public interface IThemeResolver
{
    Task<ThemeSettings> GetThemeSettingsAsync(CancellationToken cancellationToken);
}

public sealed class BrowserThemeResolver(IJSRuntime jsRuntime, NavigationManager navigationManager) : IThemeResolver, IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime = jsRuntime;
    private readonly NavigationManager _navigationManager = navigationManager;
    private IJSObjectReference? _jsModule;

    public async Task<ThemeSettings> GetThemeSettingsAsync(CancellationToken cancellationToken)
    {
        if (_jsModule == null)
        {
            var baseUri = _navigationManager.BaseUri;
            _jsModule = await _jsRuntime.InvokeAsync<IJSObjectReference>("import", $"{baseUri}js/app-theme.js").ConfigureAwait(false);
        }

        var currentThemeTask = _jsModule.InvokeAsync<string>("getCurrentTheme", cancellationToken);
        var themeCookieValueTask = _jsModule.InvokeAsync<string?>("getThemeCookieValue", cancellationToken);
        var currentTheme = await currentThemeTask.ConfigureAwait(false);
        var themeCookieValue = await themeCookieValueTask.ConfigureAwait(false);

        return new ThemeSettings(themeCookieValue, currentTheme);
    }

    public async ValueTask DisposeAsync()
    {
        await JSInteropHelpers.SafeDisposeAsync(_jsModule).ConfigureAwait(false);
    }
}

public sealed class ThemeManager
{
    public const string ThemeSettingSystem = "System";
    public const string ThemeSettingDark = "Dark";
    public const string ThemeSettingLight = "Light";

    private readonly object _lock = new object();
    private readonly List<ModelSubscription> _subscriptions = new List<ModelSubscription>();
    private readonly IThemeResolver _themeResolver;
    private string? _effectiveTheme;
    private bool _hasInitialized;
    private string? _selectedTheme;

    public ThemeManager(IThemeResolver themeResolver)
    {
        _themeResolver = themeResolver;
    }

    /// <summary>
    /// The actual theme key (null, System, Dark, Light) set by the user.
    /// To ensure the theme is loaded from the browser, <see cref="EnsureInitializedAsync"/> must be called before accessing.
    /// </summary>
    public string? SelectedTheme
    {
        get
        {
            AssertInitialized();
            return _selectedTheme;
        }
        private set => _selectedTheme = value;
    }

    /// <summary>
    /// The effective theme, from app-theme.js, which is the theme that is actually applied to the browser window.
    /// To ensure the theme is loaded from the browser, <see cref="EnsureInitializedAsync"/> must be called before accessing.
    /// </summary>
    public string EffectiveTheme
    {
        get
        {
            AssertInitialized();
            return _effectiveTheme;
        }
        set => _effectiveTheme = value;
    }

    [MemberNotNull(nameof(_effectiveTheme))]
    private void AssertInitialized()
    {
        if (!_hasInitialized)
        {
            throw new InvalidOperationException("Theme manager not initialized.");
        }

        Debug.Assert(_effectiveTheme != null, "There should be an effective theme if theme manager has been initialized.");
    }

    public async Task EnsureInitializedAsync()
    {
        // There is some overhead is calling to the browser. Initializing can be delayed until it is needed, i.e. displaying settings dialog.
        if (!_hasInitialized)
        {
            var browserThemeSettings = await _themeResolver.GetThemeSettingsAsync(CancellationToken.None).ConfigureAwait(false);
            _effectiveTheme = browserThemeSettings.EffectiveTheme;
            SelectedTheme = !string.IsNullOrEmpty(browserThemeSettings.SelectedTheme) ? browserThemeSettings.SelectedTheme : null;

            _hasInitialized = true;
        }
    }

    public IDisposable OnThemeChanged(Func<Task> callback)
    {
        lock (_lock)
        {
            var subscription = new ModelSubscription(callback, RemoveSubscription);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    private void RemoveSubscription(ModelSubscription subscription)
    {
        lock (_lock)
        {
            _subscriptions.Remove(subscription);
        }
    }

    public async Task RaiseThemeChangedAsync(string theme)
    {
        AssertInitialized();

        SelectedTheme = theme;

        ModelSubscription[] subscriptions;
        lock (_lock)
        {
          if (_subscriptions.Count == 0)
            {
                return;
            }

          subscriptions = _subscriptions.ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.ExecuteAsync().ConfigureAwait(false);
        }
    }
}
