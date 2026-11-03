// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls.PropertyValues;

public partial class TraceIdButtonValue
{
    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    [Parameter, EditorRequired]
    public required string Value { get; set; }

    [Parameter, EditorRequired]
    public required string HighlightText { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    private async Task OnClickAsync()
    {
        if (OnClick.HasDelegate)
        {
            await OnClick.InvokeAsync();
        }
        else
        {
            NavigationManager.NavigateTo(ResourcesLayout?.AddPaneToUrl(DashboardUrls.TraceDetailUrl(Value)) ?? DashboardUrls.TraceDetailUrl(Value));
        }
    }
}
