// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Compatibility shim for the removed FluentUI v4 DialogParameters.
/// Maps to the v5 DialogOptions API.
/// </summary>
public class DialogParameters
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public bool Modal { get; set; } = true;
    public bool ShowDismiss { get; set; } = true;
    public string? PrimaryAction { get; set; }
    public string? SecondaryAction { get; set; }
    public bool PrimaryActionEnabled { get; set; } = true;
    public bool PreventDismissOnOverlayClick { get; set; }
    public bool TrapFocus { get; set; } = true;
    public bool PreventScroll { get; set; } = true;
    public HorizontalAlignment Alignment { get; set; }
    public string? Width { get; set; }
    public string? Height { get; set; }
    public string? AriaLabel { get; set; }
    public string? DismissTitle { get; set; }
    public EventCallback<DialogResult>? OnDialogResult { get; set; }
    public Func<IDialogInstance, Task>? OnDialogClosing { get; set; }

    /// <summary>
    /// Converts these parameters to a v5 DialogOptions.
    /// </summary>
    public virtual DialogOptions ToDialogOptions()
    {
        return new DialogOptions(options =>
        {
            options.Header.Title = Title;
            options.Modal = PreventDismissOnOverlayClick;
            options.Width = Width;
            options.Height = Height;

            if (!string.IsNullOrEmpty(PrimaryAction))
            {
                options.Footer.PrimaryAction.Label = PrimaryAction;
            }
            if (!string.IsNullOrEmpty(SecondaryAction))
            {
                options.Footer.SecondaryAction.Label = SecondaryAction;
            }
        });
    }
}

/// <summary>
/// Compatibility shim for the removed FluentUI v4 DialogParameters&lt;T&gt;.
/// </summary>
public sealed class DialogParameters<TContent> : DialogParameters
{
    public TContent? Content { get; set; }
    public DialogType DialogType { get; set; }
    public new HorizontalAlignment Alignment { get; set; }

    public override DialogOptions ToDialogOptions()
    {
        var options = base.ToDialogOptions();
        options.Data = Content;
        return options;
    }
}

/// <summary>
/// Compatibility enum for v4 dialog types.
/// </summary>
public enum DialogType
{
    Dialog,
    Panel,
    MessageBox
}
