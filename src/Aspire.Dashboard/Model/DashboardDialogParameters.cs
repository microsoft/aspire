// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

public sealed class DialogParameters
{
    public string? Id { get; set; }

    public string? Title { get; set; }

    public string? PrimaryAction { get; set; }

    public bool PrimaryActionEnabled { get; set; } = true;

    public string? SecondaryAction { get; set; }

    public bool ShowDismiss { get; set; } = true;

    public bool UseCustomFooter { get; set; }

    public string? DismissTitle { get; set; }

    public bool PreventDismissOnOverlayClick { get; set; }

    public bool TrapFocus { get; set; }

    public bool PreventScroll { get; set; }

    public bool Modal { get; set; } = true;

    public HorizontalAlignment Alignment { get; set; } = HorizontalAlignment.Center;

    public string? Width { get; set; }

    public string? Height { get; set; }

    public string? AriaLabel { get; set; }

    public EventCallback<DialogResult> OnDialogResult { get; set; }

    public EventCallback<IDialogInstance> OnDialogClosing { get; set; }
}

public sealed class DashboardDialogReference(string? id, Task<DialogResult> result)
{
    private IDialogInstance? _instance;

    public string? Id => id;

    public Task<DialogResult> Result => result;

    internal IDialogInstance? Instance => _instance;

    internal void SetInstance(IDialogInstance instance)
    {
        _instance = instance;
    }

    public Task CloseAsync()
    {
        return _instance?.CloseAsync() ?? Task.CompletedTask;
    }

    public Task CloseAsync(DialogResult result)
    {
        return _instance?.CloseAsync(result) ?? Task.CompletedTask;
    }
}
