// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Utilities;

namespace Aspire.Dashboard.Components;

public partial class AspireMenu : FluentComponentBase
{
    private FluentMenu? _menu;

    [Parameter]
    public string? Anchor { get; set; }

    [Parameter]
    public bool Open { get; set; }

    [Parameter]
    public bool Anchored { get; set; } = true;

    [Parameter]
    public int VerticalThreshold { get; set; } = 200;

    /// <summary>
    /// Raised when the <see cref="Open"/> property changed.
    /// </summary>
    [Parameter]
    public EventCallback<bool> OpenChanged { get; set; }

    [Parameter]
    public required IReadOnlyList<MenuButtonItem> Items { get; set; }

    public async Task CloseAsync()
    {
        if (_menu is { } menu)
        {
            await menu.CloseAsync();
        }
    }

    public async Task OpenAsync(int screenWidth, int screenHeight, int clientX, int clientY)
    {
        if (_menu is { } menu)
        {
            // Calculate the position to display the context menu using the cursor position (clientX, clientY)
            // together with the screen width and height.
            // The menu may need to be displayed above or left of the cursor to fit in the screen.
            var left = 0;
            var right = 0;
            var top = 0;
            var bottom = 0;

            if (clientX + menu.HorizontalThreshold > screenWidth)
            {
                right = screenWidth - clientX;
            }
            else
            {
                left = clientX;
            }

            if (clientY + menu.VerticalThreshold > screenHeight)
            {
                bottom = screenHeight - clientY;
            }
            else
            {
                top = clientY;
            }

            // Overwrite the style. We don't want to add new position values each time the menu is opened.
            Style = new StyleBuilder()
                .AddStyle("left", $"{left}px", left != 0)
                .AddStyle("right", $"{right}px", right != 0)
                .AddStyle("top", $"{top}px", top != 0)
                .AddStyle("bottom", $"{bottom}px", bottom != 0)
                // Width values come from fluentui-blazor stylesheet; max-width uses an app CSS variable so nested submenus stay in sync.
                // Explicitly set to override min-width: fit-content applied by library to some menus.
                .AddStyle("max-width", "var(--aspire-menu-max-width)")
                .AddStyle("min-width", "64px")
                .Build();

            Open = true;
            if (OpenChanged.HasDelegate)
            {
                await OpenChanged.InvokeAsync(Open);
            }

            StateHasChanged();
        }
    }

    private async Task HandleItemClicked(MenuButtonItem item)
    {
        if (item.OnClick is {} onClick)
        {
            await onClick();
        }
        Open = false;
    }

    private Task OnOpenChanged(bool open)
    {
        Open = open;

        return OpenChanged.HasDelegate
            ? OpenChanged.InvokeAsync(open)
            : Task.CompletedTask;
    }
}
