// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>A terminal launch button whose native listener opens the window before notifying Blazor.</summary>
public partial class TerminalWindowButton : ComponentBase, IAsyncDisposable
{
    private readonly string _buttonId = $"terminal-window-button-{Guid.NewGuid():N}";
    private TerminalWindowLauncher? _launcher;
    private bool _ready;
    private bool _disposed;

    /// <summary>Gets or sets the terminal's stable window key.</summary>
    [Parameter]
    public string? TerminalKey { get; set; }

    /// <summary>Gets or sets the dashboard-relative URL of the independent terminal viewer.</summary>
    [Parameter]
    public string? Url { get; set; }

    /// <summary>Gets or sets the originating font preference. Null keeps the button disabled until it is known.</summary>
    [Parameter]
    public int? FontSize { get; set; }

    /// <summary>Gets or sets the localized accessible label and tooltip.</summary>
    [Parameter, EditorRequired]
    public required string Label { get; set; }

    /// <summary>Gets or sets additional button classes.</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>Gets or sets whether this surface currently permits launching a window.</summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Raised with the clicked key and outcome, even if the selected terminal has since changed.</summary>
    [Parameter]
    public EventCallback<(string Key, TerminalWindowOpenResult Result)> OnWindowOpened { get; set; }

    /// <summary>Raised with the key of a tracked window that the user closed.</summary>
    [Parameter]
    public EventCallback<string> OnWindowClosed { get; set; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required ILogger<TerminalWindowButton> Logger { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.TerminalStrings> Loc { get; init; }

    [Inject]
    public required Microsoft.FluentUI.AspNetCore.Components.INotificationService ToastService { get; init; }

    // Transfer only the font preference. The independent viewer fits its own viewport instead of inheriting
    // the opener's grid dimensions. Render this complete URL so the click needs no interop or terminal lookup.
    private string? LaunchUrl => !string.IsNullOrEmpty(Url) && FontSize is > 0
        ? QueryHelpers.AddQueryString(NavigationManager.ToAbsoluteUri(Url).AbsoluteUri, "fontSize",
            FontSize.Value.ToString(CultureInfo.InvariantCulture))
        : null;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed)
        {
            return;
        }

        _launcher = new TerminalWindowLauncher(JS, NavigationManager, OnOpenedAsync,
            key => InvokeAsync(() => _disposed ? Task.CompletedTask : OnWindowClosed.InvokeAsync(key)));
        try
        {
            await _launcher.RegisterAsync(_buttonId);
            if (!_disposed)
            {
                _ready = true;
                StateHasChanged();
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit has gone away, so there is no live UI to notify.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to register the terminal window button.");
            if (!_disposed)
            {
                await ToastService.ShowErrorToastAsync(Loc[nameof(Resources.TerminalStrings.TerminalToolbarOpenInWindowFailed)]);
            }
        }
    }

    private Task OnOpenedAsync(string key, TerminalWindowOpenResult result) => InvokeAsync(async () =>
    {
        if (_disposed)
        {
            return;
        }

        if (result == TerminalWindowOpenResult.Failed)
        {
            Logger.LogWarning("The browser failed to open or focus a terminal window.");
            await ToastService.ShowErrorToastAsync(Loc[nameof(Resources.TerminalStrings.TerminalToolbarOpenInWindowFailed)]);
        }
        else if (result == TerminalWindowOpenResult.Blocked && !OnWindowOpened.HasDelegate)
        {
            await ToastService.ShowErrorToastAsync(Loc[nameof(Resources.TerminalStrings.TerminalToolbarOpenInWindowBlocked)]);
        }

        if (!_disposed)
        {
            await OnWindowOpened.InvokeAsync((key, result));
        }
    });

    /// <summary>Focuses an already detached window, or returns false if it has closed.</summary>
    /// <param name="key">The terminal window key.</param>
    /// <returns>Whether the window is still open.</returns>
    public Task<bool> FocusAsync(string key) => _launcher?.FocusAsync(key) ?? Task.FromResult(false);

    /// <summary>Closes an independent viewer without stopping its terminal producer.</summary>
    /// <param name="key">The terminal window key.</param>
    /// <returns>A task that completes when the browser has closed the window.</returns>
    public Task CloseAsync(string key) => _launcher?.CloseAsync(key) ?? Task.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_launcher is { } launcher)
        {
            await launcher.DisposeAsync();
        }
    }
}
