// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components;

public partial class UrlsColumnDisplay : IAsyncDisposable
{
    // Safety cap on how many URL elements are ever rendered inline. Rendering hundreds of URL DOM
    // elements triggers a forced synchronous reflow long enough to drop the SignalR connection, so
    // anything past the cap is only ever shown in the overflow popover.
    private const int MaxRenderedUrls = 20;

    internal static string GetTooltipText(DisplayedUrl displayedUrl)
    {
        return displayedUrl.Url ?? displayedUrl.OriginalUrlString;
    }

    [Parameter, EditorRequired]
    public required ResourceViewModel Resource { get; set; }

    [Parameter, EditorRequired]
    public required bool HasMultipleReplicas { get; set; }

    [Parameter, EditorRequired]
    public required IList<DisplayedUrl> DisplayedUrls { get; set; }

    [Parameter]
    public string? AdditionalMessage { get; set; }

    [Inject]
    public required IStringLocalizer<Columns> Loc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    private bool _popoverVisible;

    // Stable id for the "+N" overflow button so the popover can anchor to it.
    private readonly string _moreButtonId = $"urls-more-{Guid.NewGuid():N}";

    private ElementReference _container;
    private IJSObjectReference? _module;
    private DotNetObjectReference<UrlsColumnDisplay>? _selfReference;

    // Number of the (up to MaxRenderedUrls) rendered items shown inline. Until the JS measurer runs
    // we optimistically assume everything fits; the measurer collapses overflow into the popover.
    private int _visibleCount = MaxRenderedUrls;

    // Signature of the currently rendered URL set, used to re-measure only when the data changed
    // (a ResizeObserver in the module already handles width changes on its own).
    private string? _measuredSignature;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Only the overflow layout needs measuring; a single (or no) URL is laid out by the browser.
        if (DisplayedUrls.Count <= 1)
        {
            return;
        }

        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/ResourcesGridColumns/UrlsColumnDisplay.razor.js");
            _selfReference = DotNetObjectReference.Create(this);
            _measuredSignature = ComputeSignature();
            await _module.InvokeVoidAsync("initialize", _container, _selfReference);
        }
        else if (_module is not null)
        {
            // Re-measure when the rendered URLs changed. The ResizeObserver won't fire for a
            // content-only change that leaves the column the same width.
            var signature = ComputeSignature();
            if (signature != _measuredSignature)
            {
                _measuredSignature = signature;
                await _module.InvokeVoidAsync("measure", _container);
            }
        }
    }

    /// <summary>
    /// Called by the resize measurer with the number of rendered items that fit inline. The first
    /// item is always kept, and the count is clamped to what we actually rendered.
    /// </summary>
    [JSInvokable]
    public Task SetVisibleCountAsync(int visibleCount)
    {
        var renderedCount = Math.Min(DisplayedUrls.Count, MaxRenderedUrls);
        var clamped = Math.Clamp(visibleCount, 1, Math.Max(1, renderedCount));
        if (clamped != _visibleCount)
        {
            _visibleCount = clamped;
            StateHasChanged();
        }

        return Task.CompletedTask;
    }

    // Cheap identity of the rendered subset: count plus each rendered item's original URL. Small
    // because it only ever covers up to MaxRenderedUrls items.
    private string ComputeSignature()
    {
        return $"{DisplayedUrls.Count}\u0001{string.Join('\u0001', DisplayedUrls.Take(MaxRenderedUrls).Select(u => u.OriginalUrlString))}";
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("dispose", _container);
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; nothing to clean up.
        }

        _selfReference?.Dispose();
    }
}
