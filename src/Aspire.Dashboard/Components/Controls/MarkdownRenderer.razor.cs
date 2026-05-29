// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Markdown;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls;

public partial class MarkdownRenderer : ComponentBase, IAsyncDisposable
{
    private IJSObjectReference? _jsModule;
    private ElementReference _containerElement;
    private bool _htmlChanged;
    private string? _markdown;
    private MarkupString _html;

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Parameter]
    public required string Markdown { get; set; }

    [Parameter]
    public required MarkdownProcessor MarkdownProcessor { get; set; }

    [Parameter]
    public required bool SuppressParagraphOnNewLines { get; set; }

    protected override void OnParametersSet()
    {
        if (Markdown != _markdown)
        {
            _markdown = Markdown;

            var suppressSurroundingParagraph = MarkdownHelpers.GetSuppressSurroundingParagraph(_markdown, SuppressParagraphOnNewLines);

            _html = (MarkupString)MarkdownProcessor.ToHtml(_markdown, inCompleteDocument: false, suppressSurroundingParagraph: suppressSurroundingParagraph);
            _htmlChanged = true;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "/Components/Controls/MarkdownRenderer.razor.js");
        }

        if (_jsModule is not null)
        {
            if (_htmlChanged && !string.IsNullOrWhiteSpace(_html.Value))
            {
                _htmlChanged = false;
                await _jsModule.InvokeVoidAsync("highlightCodeBlocks", _containerElement);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_jsModule != null)
        {
            try
            {
                await _jsModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Per https://learn.microsoft.com/aspnet/core/blazor/javascript-interoperability/?view=aspnetcore-7.0#javascript-interop-calls-without-a-circuit
                // this is one of the calls that will fail if the circuit is disconnected, and we just need to catch the exception so it doesn't pollute the logs
            }
        }
    }
}
