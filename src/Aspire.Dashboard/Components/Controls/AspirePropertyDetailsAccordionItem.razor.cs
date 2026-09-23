// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

public partial class AspirePropertyDetailsAccordionItem
{
    private bool _expanded;
    private bool _lastExpandedParameter;
    private bool _expandedInitialized;

    /// <summary>
    /// Gets or sets the section header.
    /// </summary>
    [Parameter, EditorRequired]
    public required string Header { get; set; }

    /// <summary>
    /// Gets or sets the number of items in the section.
    /// </summary>
    [Parameter]
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the section is expanded.
    /// </summary>
    [Parameter]
    public bool Expanded { get; set; }

    /// <summary>
    /// Gets or sets the callback invoked when the expanded state changes.
    /// </summary>
    [Parameter]
    public EventCallback<bool> ExpandedChanged { get; set; }

    /// <summary>
    /// Gets or sets the section content.
    /// </summary>
    [Parameter, EditorRequired]
    public required RenderFragment ChildContent { get; set; }

    protected override void OnParametersSet()
    {
        if (!_expandedInitialized || Expanded != _lastExpandedParameter)
        {
            _expanded = Expanded;
            _lastExpandedParameter = Expanded;
            _expandedInitialized = true;
        }
    }

    private async Task HandleExpandedChangedAsync(bool expanded)
    {
        _expanded = expanded;
        await ExpandedChanged.InvokeAsync(expanded);
    }
}