// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

public partial class PauseIncomingDataSwitch : ComponentBase
{
    [Parameter]
    public bool IsPaused { get; set; }

    [Parameter]
    public EventCallback<bool> IsPausedChanged { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    private async Task OnTogglePauseCoreAsync()
    {
        if (Disabled)
        {
            return;
        }

        IsPaused = !IsPaused;
        await IsPausedChanged.InvokeAsync(IsPaused);
    }
}

