// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

public partial class DetailPageCloseButton
{
    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Parameter, EditorRequired]
    public required string ParentUrl { get; set; }

    [Parameter]
    public EventCallback OnClose { get; set; }

    private async Task CloseAsync()
    {
        await OnClose.InvokeAsync();
        NavigationManager.NavigateTo(ParentUrl);
    }
}
