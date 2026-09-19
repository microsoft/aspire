// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>Displays the workload title, directory and progress without changing terminal state.</summary>
public partial class TerminalTitle : IAsyncDisposable
{
    private readonly string _directoryButtonId = $"terminal-directory-{Guid.NewGuid():N}";
    private ElementReference _metadataElement;
    private IJSObjectReference? _jsModule;
    private Task? _initializationTask;
    private bool _disposed;

    /// <summary>Gets or sets the terminal's latest presentation state.</summary>
    [Parameter]
    public TerminalToolbarState? State { get; set; }

    /// <summary>Gets or sets the title used before the workload reports one or after it clears it.</summary>
    [Parameter]
    public string FallbackTitle { get; set; } = string.Empty;

    [Inject]
    public required IStringLocalizer<Resources.TerminalStrings> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.ControlsStrings> ControlsLoc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _initializationTask = InitializeAsync();
        }

        return _initializationTask ?? Task.CompletedTask;
    }

    private async Task InitializeAsync()
    {
        _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Controls/TerminalTitle.razor.js");
        if (!_disposed)
        {
            await _jsModule.InvokeVoidAsync("observePath", _metadataElement);
        }
    }

    /// <summary>Releases the path measurement observers.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (_initializationTask is not null)
            {
                await _initializationTask;
            }
            if (_jsModule is not null)
            {
                await _jsModule.InvokeVoidAsync("disconnectPath", _metadataElement);
            }
        }
        catch (JSDisconnectedException)
        {
            // The browser may already be gone when the component is disposed.
        }
        catch (OperationCanceledException)
        {
            // The browser may already be gone when the component is disposed.
        }
        finally
        {
            if (_jsModule is not null)
            {
                await JSInteropHelpers.SafeDisposeAsync(_jsModule);
            }
        }
    }

    private Dictionary<string, object> DirectoryCopyAttributes => FluentUIExtensions.GetClipboardCopyAdditionalAttributes(
        State?.WorkingDirectory,
        ControlsLoc[nameof(Resources.ControlsStrings.GridValueCopyToClipboard)],
        ControlsLoc[nameof(Resources.ControlsStrings.GridValueCopied)],
        ("aria-label", Loc[nameof(Resources.TerminalStrings.TerminalCopyWorkingDirectory), State?.WorkingDirectory ?? string.Empty].Value));

    private string DisplayTitle => State is { Title.Length: > 0 } ? State.Title : FallbackTitle;

    private double? ProgressValue => State?.ProgressState is "indeterminate" ? null : State?.ProgressPercentage;

    private string ProgressLabel => Loc[State?.ProgressState switch
    {
        "error" => nameof(Resources.TerminalStrings.TerminalProgressError),
        "warning" => nameof(Resources.TerminalStrings.TerminalProgressWarning),
        _ => nameof(Resources.TerminalStrings.TerminalProgress)
    }];
}
