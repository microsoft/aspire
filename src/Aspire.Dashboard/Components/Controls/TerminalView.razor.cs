// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Terminal;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Renders a GPU terminal through the dashboard's HWT1 presentation endpoint.
/// </summary>
public sealed partial class TerminalView : ComponentBase, IAsyncDisposable
{
    private ElementReference _terminalElement;
    private ElementReference _selectionTemplateElement;
    private ElementReference _footerElement;
    private IJSObjectReference? _jsModule;
    private DotNetObjectReference<TerminalView>? _selfRef;
    private int _terminalId;
    private int _connectedGeneration = -1;
    private string? _connectedEndpoint;
    private bool _appliedReadOnly;
    private bool _appliedAutoFit;
    private bool _initializationFailed;
    private string? _failedEndpoint;
    private bool _disposed;
    private bool _reconciling;
    private Task? _initializationTask;
    private string? _terminalError;
    private TerminalToolbarState _state = new();
    private IReadOnlyList<TerminalSizePreset> _sizePresets = [];
    private TerminalViewSession? _viewSession;
    private string? _sessionEndpoint;

    /// <summary>Gets or sets the display name of the resource that owns the terminal.</summary>
    [Parameter]
    public string? ResourceName { get; set; }

    /// <summary>Gets or sets the zero-based resource replica index.</summary>
    [Parameter]
    public int ReplicaIndex { get; set; }

    /// <summary>Gets or sets the accessible label for decreasing the font size.</summary>
    [Parameter]
    public string? DecreaseFontSizeLabel { get; set; }

    /// <summary>Gets or sets the accessible label for increasing the font size.</summary>
    [Parameter]
    public string? IncreaseFontSizeLabel { get; set; }

    /// <summary>Gets or sets the accessible label for the terminal dimensions selector.</summary>
    [Parameter]
    public string? TerminalDimensionsLabel { get; set; }

    /// <summary>Gets or sets the label for fitting the terminal to the available space.</summary>
    [Parameter]
    public string? FitLabel { get; set; }

    /// <summary>Gets or sets the hint describing focus navigation to the terminal controls.</summary>
    [Parameter]
    public string? FocusControlsHintLabel { get; set; }

    /// <summary>
    /// Gets or sets an explicit endpoint path and query, overriding the resource and replica.
    /// </summary>
    [Parameter]
    public string? EndpointPathAndQuery { get; set; }

    /// <summary>Gets or sets whether user input is blocked while terminal output continues.</summary>
    /// <remarks>Changing this value does not reconnect or change the lifetime of the process.</remarks>
    [Parameter]
    public bool ReadOnly { get; set; }

    /// <summary>Gets or sets whether the terminal omits its border, titlebar and internal padding.</summary>
    /// <remarks>The dock, interaction dialog and detached windows provide their own surrounding chrome.</remarks>
    [Parameter]
    public bool Chromeless { get; set; }

    /// <summary>Gets or sets the per-surface key for page-lifetime font-size persistence.</summary>
    /// <remarks>Detached windows seed their font from the opener without sharing live font preferences.</remarks>
    [Parameter]
    public string? SizeMemoryKey { get; set; }

    /// <summary>Gets or sets the initial font size in CSS pixels when this surface has no remembered preference.</summary>
    /// <remarks>Null uses the terminal's default. Changing this value does not override a mounted view's font.</remarks>
    [Parameter]
    public int? InitialFontSize { get; set; }

    /// <summary>Gets the selected font size, or the initial preference before the first state notification.</summary>
    public int? FontSize => _state.FontPx > 0 ? _state.FontPx : InitialFontSize;

    /// <summary>Gets or sets whether the footer offers fixed-resolution presets. Defaults to true.</summary>
    /// <remarks>The font stepper remains available on surfaces sized by a splitter or dialog.</remarks>
    [Parameter]
    public bool ShowDimensionsPicker { get; set; } = true;

    /// <summary>Gets or sets whether opening this surface fits its grid to the container while preserving font size.</summary>
    /// <remarks>Set this only for the active dock pane. Read-only views do not take resize control.</remarks>
    [Parameter]
    public bool AutoFit { get; set; }

    /// <summary>Raised when the terminal's role, dimensions, font or connection state changes.</summary>
    [Parameter]
    public EventCallback<TerminalToolbarState> OnToolbarStateChanged { get; set; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.ConsoleLogs> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.ControlsStrings> ControlsLoc { get; init; }

    [Inject]
    public required TerminalViewSessionRegistry ViewSessions { get; init; }

    protected override void OnParametersSet()
    {
        // Update the authoritative input gate immediately, including while initialization
        // or an earlier JS policy update is awaiting its Blazor interop round trip.
        _viewSession?.ReadOnly = ReadOnly ||
            !string.Equals(_sessionEndpoint, ResolveEndpoint(), StringComparison.Ordinal);
    }

    protected override Task OnAfterRenderAsync(bool firstRender) => ReconcileAsync();

    private async Task ReconcileAsync()
    {
        if (_disposed || _reconciling ||
            (_initializationFailed && string.Equals(_failedEndpoint, ResolveEndpoint(), StringComparison.Ordinal)))
        {
            return;
        }
        _initializationFailed = false;

        // Blazor can render again while interop awaits. One reconciler owns initialization, endpoint changes
        // (including removal), and input policy changes; it rereads parameters after every interop round trip.
        _reconciling = true;
        try
        {
            while (!_disposed)
            {
                var endpoint = ResolveEndpoint();
                if (!string.Equals(endpoint, _connectedEndpoint, StringComparison.Ordinal))
                {
                    await ReconnectAsync(endpoint);
                    if (_disposed || _initializationFailed)
                    {
                        return;
                    }
                    continue;
                }

                if (_terminalId != 0 && _appliedReadOnly != ReadOnly)
                {
                    var readOnly = ReadOnly;
                    _viewSession!.ReadOnly = readOnly;
                    await _jsModule!.InvokeVoidAsync("setReadOnly", _terminalId, readOnly);
                    _appliedReadOnly = readOnly;
                    continue;
                }
                if (_terminalId != 0 && _appliedAutoFit != AutoFit)
                {
                    var autoFit = AutoFit;
                    await _jsModule!.InvokeVoidAsync("setAutoFit", _terminalId, autoFit);
                    _appliedAutoFit = autoFit;
                    continue;
                }
                break;
            }
        }
        catch (JSDisconnectedException)
        {
            // The browser disconnected during the interop round trip.
        }
        catch (Exception)
        {
            // Keep failures local to this view without silently swallowing rendering or input-policy errors.
            ShowInitializationError();
        }
        finally
        {
            _reconciling = false;
        }
    }

    private string? ResolveEndpoint()
    {
        if (!string.IsNullOrEmpty(EndpointPathAndQuery))
        {
            return new Uri(new Uri(NavigationManager.BaseUri), EndpointPathAndQuery).PathAndQuery;
        }

        if (string.IsNullOrEmpty(ResourceName))
        {
            return null;
        }
        return new Uri(new Uri(NavigationManager.BaseUri),
            $"api/terminal?resource={Uri.EscapeDataString(ResourceName)}&replica={ReplicaIndex}").PathAndQuery;
    }

    private Task InitializeTerminalAsync(string endpoint)
    {
        return _initializationTask = InitializeTerminalCoreAsync(endpoint);
    }

    private async Task InitializeTerminalCoreAsync(string endpoint)
    {
        var moduleUri = new Uri(new Uri(NavigationManager.BaseUri), "Components/Controls/TerminalView.razor.js");
        _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", moduleUri.PathAndQuery);
        if (_disposed)
        {
            return;
        }

        // Internal chrome/error updates need the callback even when the host has no subscriber.
        _selfRef ??= DotNetObjectReference.Create(this);
        _connectedGeneration = -1;
        var readOnly = ReadOnly;
        var autoFit = AutoFit;
        _terminalId = await _jsModule.InvokeAsync<int>(
            "initTerminal", _terminalElement, BuildWebSocketUrl(endpoint), _selfRef,
            new TerminalViewOptions
            {
                ViewId = _viewSession!.Id,
                ReadOnly = readOnly,
                Chromeless = Chromeless,
                ShowDimensions = ShowDimensionsPicker,
                AutoFit = autoFit,
                SizeMemoryKey = SizeMemoryKey,
                InitialFontSize = InitialFontSize,
                Label = Loc[nameof(Resources.ConsoleLogs.TerminalInputLabel)],
                DecreaseFontSize = DecreaseFontSizeLabel ?? Loc[nameof(Resources.ConsoleLogs.TerminalToolbarDecreaseFontSize)],
                IncreaseFontSize = IncreaseFontSizeLabel ?? Loc[nameof(Resources.ConsoleLogs.TerminalToolbarIncreaseFontSize)],
                TerminalDimensions = TerminalDimensionsLabel ?? Loc[nameof(Resources.ConsoleLogs.TerminalToolbarGridSize)],
                Fit = FitLabel ?? Loc[nameof(Resources.ConsoleLogs.TerminalToolbarGridSizeAuto)],
                FocusControlsHint = FocusControlsHintLabel ?? Loc[nameof(Resources.ConsoleLogs.TerminalFocusControlsHint)],
            }, _selectionTemplateElement, _footerElement);
        _appliedReadOnly = readOnly;
        _appliedAutoFit = autoFit;
        if (!_disposed)
        {
            _sizePresets = await GetSizePresetsAsync();
            if (!_disposed)
            {
                StateHasChanged();
            }
        }
    }

    /// <summary>Rebinds the view to an endpoint, or explicitly retries the current endpoint.</summary>
    /// <param name="newEndpoint">The endpoint path and query, or null to detach the view.</param>
    public async Task ReconnectAsync(string? newEndpoint)
    {
        if (_disposed)
        {
            return;
        }

        if (string.IsNullOrEmpty(newEndpoint))
        {
            ReleaseViewSession();
            if (_jsModule is not null && _terminalId != 0)
            {
                var id = _terminalId;
                _terminalId = 0;
                _connectedGeneration = -1;
                await _jsModule.InvokeVoidAsync("disposeTerminal", id);
            }
            _state = new();
            _terminalError = null;
            if (!_disposed)
            {
                StateHasChanged();
            }
        }
        else
        {
            // Registry identity must match Request.PathBase + Request.Path + Request.QueryString,
            // including when a caller supplied a relative explicit endpoint.
            newEndpoint = new Uri(new Uri(NavigationManager.BaseUri), newEndpoint).PathAndQuery;
            EnsureViewSession(newEndpoint);
            if (_terminalId == 0)
            {
                await InitializeTerminalAsync(newEndpoint);
            }
            else
            {
                var generation = await _jsModule!.InvokeAsync<int>(
                    "reconnectTerminal", _terminalId, BuildWebSocketUrl(newEndpoint));
                _connectedGeneration = Math.Max(_connectedGeneration, generation);
            }
        }

        _connectedEndpoint = newEndpoint;
    }

    private void EnsureViewSession(string endpoint)
    {
        if (_viewSession is not null && string.Equals(_sessionEndpoint, endpoint, StringComparison.Ordinal))
        {
            return;
        }
        ReleaseViewSession();
        _sessionEndpoint = endpoint;
        _viewSession = ViewSessions.Create(endpoint, ReadOnly);
    }

    private void ReleaseViewSession()
    {
        _viewSession?.Dispose();
        _viewSession = null;
        _sessionEndpoint = null;
    }

    /// <summary>Updates this view's chrome and forwards the current terminal state to its host.</summary>
    /// <param name="state">The generation-tagged state supplied by the JS adapter.</param>
    [JSInvokable]
    public async Task OnTerminalStateChanged(TerminalToolbarState state)
    {
        if (_disposed || (_terminalId != 0 && state.TerminalId != _terminalId) ||
            (_terminalId == 0 && _initializationTask is not { IsCompleted: false }) ||
            state.Generation < _connectedGeneration)
        {
            return;
        }

        _connectedGeneration = state.Generation;
        if (_state != state)
        {
            _state = state;
            if (!_initializationFailed)
            {
                _terminalError = state.Error;
            }
            StateHasChanged();
        }
        await OnToolbarStateChanged.InvokeAsync(state);
    }

    /// <summary>Sets the font size in automatic sizing mode, clamped to the package's public bounds.</summary>
    /// <param name="fontPx">The desired font size in CSS pixels.</param>
    public Task SetFontSizeAsync(int fontPx) => InvokeTerminalAsync("setFontSizeFromHost", fontPx);

    /// <summary>Selects automatic sizing or one of the terminal's fixed grid presets.</summary>
    /// <param name="sizeKey">The preset key, or <c>auto</c>.</param>
    public Task SetSizeModeAsync(string sizeKey) => InvokeTerminalAsync("setSizeModeFromHost", sizeKey);

    /// <summary>Fits the terminal grid to its container without changing the selected font size.</summary>
    public Task FitToContainerAsync() => InvokeTerminalAsync("fitToContainer");

    private IReadOnlyList<TerminalSizePreset> DisplayedSizePresets => _state.Cols > 0 && _state.Rows > 0 &&
        !_sizePresets.Any(p => p.Value == _state.SizeKey)
        ? [new(_state.SizeKey, $"{_state.Cols}\u00d7{_state.Rows}", _state.Cols, _state.Rows), .. _sizePresets]
        : _sizePresets;

    /// <summary>Gets the supported grid presets from the JS adapter.</summary>
    /// <returns>The available preset values and dimensions.</returns>
    public async Task<IReadOnlyList<TerminalSizePreset>> GetSizePresetsAsync()
    {
        if (_jsModule is null)
        {
            return [];
        }
        try
        {
            return await _jsModule.InvokeAsync<TerminalSizePreset[]>("getSizePresets");
        }
        catch (JSDisconnectedException)
        {
            return [];
        }
    }

    /// <summary>Requests a fresh state notification even if the state has not changed.</summary>
    public Task RefreshToolbarStateAsync() => InvokeTerminalAsync("refreshToolbarState");

    /// <summary>Starts a deferred mount or refreshes a view that became visible without reconnecting.</summary>
    public Task RefreshLayoutAsync() => InvokeTerminalAsync("refreshLayout");

    private async Task InvokeTerminalAsync(string method, params object?[] arguments)
    {
        if (_disposed || _jsModule is null || _terminalId == 0)
        {
            return;
        }
        try
        {
            await _jsModule.InvokeVoidAsync(method, [_terminalId, .. arguments]);
        }
        catch (JSDisconnectedException)
        {
            // Expected when the browser leaves this page.
        }
    }

    private string BuildWebSocketUrl(string pathAndQuery)
    {
        var endpoint = new Uri(new Uri(NavigationManager.BaseUri), pathAndQuery);
        var scheme = endpoint.Scheme == "https" ? "wss" : "ws";
        var boundPathAndQuery = QueryHelpers.AddQueryString(endpoint.PathAndQuery, "viewId", _viewSession!.Id);
        return $"{scheme}://{endpoint.Authority}{boundPathAndQuery}";
    }

    private string GetErrorMessage() => Loc[_terminalError switch
    {
        "disconnected" => nameof(Resources.ConsoleLogs.TerminalDisconnected),
        "input-failed" => nameof(Resources.ConsoleLogs.TerminalInputFailed),
        "sizing-failed" => nameof(Resources.ConsoleLogs.TerminalSizingFailed),
        _ => nameof(Resources.ConsoleLogs.TerminalMountFailed)
    }];

    private async Task RetryAsync()
    {
        if (_reconciling || _disposed)
        {
            return;
        }
        _initializationFailed = false;
        _terminalError = null;
        _reconciling = true;
        try
        {
            await ReconnectAsync(ResolveEndpoint());
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception)
        {
            ShowInitializationError();
        }
        finally
        {
            _reconciling = false;
        }
        await ReconcileAsync();
    }

    private void ShowInitializationError()
    {
        _initializationFailed = true;
        _failedEndpoint = ResolveEndpoint();
        _terminalError = "mount-failed";
        if (!_disposed)
        {
            StateHasChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ReleaseViewSession();
        // Wait for the interop result before disposing the module so a worker created during disposal isn't orphaned.
        if (_initializationTask is not null)
        {
            try
            {
                await _initializationTask;
            }
            catch (Exception)
            {
                // Initialization already surfaces its failure while the component is alive.
            }
        }
        if (_jsModule is not null)
        {
            if (_terminalId != 0)
            {
                try
                {
                    await _jsModule.InvokeVoidAsync("disposeTerminal", _terminalId);
                }
                catch (JSDisconnectedException)
                {
                }
            }
            await JSInteropHelpers.SafeDisposeAsync(_jsModule);
            _jsModule = null;
        }
        _terminalId = 0;
        _selfRef?.Dispose();
        _selfRef = null;
    }
}

/// <summary>Localized options serialized to the JS adapter using camelCase property names.</summary>
public sealed record TerminalViewOptions
{
    /// <summary>The opaque registry identity for this view's input policy.</summary>
    public required string ViewId { get; init; }
    /// <summary>Whether application input is blocked.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Whether the host supplies its own surrounding chrome.</summary>
    public bool Chromeless { get; init; }
    /// <summary>Whether fixed-resolution presets are offered.</summary>
    public bool ShowDimensions { get; init; } = true;
    /// <summary>Whether opening the active surface requests automatic grid sizing at the current font size.</summary>
    public bool AutoFit { get; init; }
    /// <summary>The per-surface key for remembering the font size.</summary>
    public string? SizeMemoryKey { get; init; }
    /// <summary>The initial font size when no per-surface preference has been remembered.</summary>
    public int? InitialFontSize { get; init; }
    /// <summary>The accessible label for the terminal's keyboard input.</summary>
    public required string Label { get; init; }
    /// <summary>The accessible decrease-font-size label.</summary>
    public required string DecreaseFontSize { get; init; }
    /// <summary>The accessible increase-font-size label.</summary>
    public required string IncreaseFontSize { get; init; }
    /// <summary>The accessible grid-size label.</summary>
    public required string TerminalDimensions { get; init; }
    /// <summary>The localized automatic-sizing option.</summary>
    public required string Fit { get; init; }
    /// <summary>The localized focus-navigation hint.</summary>
    public required string FocusControlsHint { get; init; }
}

/// <summary>A generation-tagged snapshot of terminal role, sizing and connection state.</summary>
public sealed record TerminalToolbarState
{
    /// <summary>The unique JS-side view identifier.</summary>
    public int TerminalId { get; init; }
    /// <summary>The connection generation.</summary>
    public int Generation { get; init; }
    /// <summary>One of connecting, primary, viewer or no-primary.</summary>
    public string Status { get; init; } = "connecting";
    /// <summary>Whether a connected frame has been presented.</summary>
    public bool Connected { get; init; }
    /// <summary>Whether this view owns resize authority.</summary>
    public bool IsPrimary { get; init; }
    /// <summary>Whether requesting resize authority is available.</summary>
    public bool CanTakeControl { get; init; }
    /// <summary>The current sizing mode: font or fixed.</summary>
    public string SizeMode { get; init; } = "font";
    /// <summary>The selected preset key, or auto.</summary>
    public string SizeKey { get; init; } = "auto";
    /// <summary>The font size in CSS pixels.</summary>
    public int FontPx { get; init; }
    /// <summary>Whether font controls are available.</summary>
    public bool FontControlsEnabled { get; init; }
    /// <summary>Whether decreasing the font respects the public package bounds.</summary>
    public bool CanDecreaseFontSize { get; init; }
    /// <summary>Whether increasing the font respects the public package bounds.</summary>
    public bool CanIncreaseFontSize { get; init; }
    /// <summary>Whether grid presets are available.</summary>
    public bool SizeSelectEnabled { get; init; }
    /// <summary>Whether fitting is available and the view is not already the auto-sized primary.</summary>
    public bool FitEnabled { get; init; }
    /// <summary>The server-authoritative grid width.</summary>
    public int Cols { get; init; }
    /// <summary>The server-authoritative grid height.</summary>
    public int Rows { get; init; }
    /// <summary>The localized error category, or null when healthy.</summary>
    public string? Error { get; init; }
}

/// <summary>A named grid preset exposed by the JS terminal.</summary>
public sealed record TerminalSizePreset(string Value, string Label, int Cols, int Rows);
