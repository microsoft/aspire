// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Compatibility shim for the removed FluentUI v4 ToastParameters.
/// In v5, the toast API has been simplified and these parameters are mapped to ToastOptions.
/// </summary>
public class ToastParameters<TContent>
{
    public string? Id { get; set; }
    public ToastIntent Intent { get; set; }
    public string? Title { get; set; }
    public TContent? Content { get; set; }
    public int? Timeout { get; set; }
    public string? PrimaryAction { get; set; }
    public string? SecondaryAction { get; set; }
    public EventCallback<ToastResult> OnPrimaryAction { get; set; }
    public EventCallback<ToastResult> OnSecondaryAction { get; set; }
    public (Icon Icon, Color Color)? Icon { get; set; }
}

/// <summary>
/// Compatibility shim for the removed FluentUI v4 CommunicationToastContent.
/// </summary>
public sealed class CommunicationToastContent
{
    public string? Subtitle { get; set; }
    public string? Details { get; set; }
}

/// <summary>
/// Compatibility shim for the removed FluentUI v4 ToastResult.
/// Represents the result of a toast action.
/// </summary>
public sealed class ToastResult
{
}

/// <summary>
/// Compatibility shim for the removed FluentUI v4 ActionButton.
/// Represents a button action in a message bar.
/// </summary>
public sealed class ActionButton<T>
{
    public string? Text { get; set; }
    public Func<T, Task>? OnClick { get; set; }
}
