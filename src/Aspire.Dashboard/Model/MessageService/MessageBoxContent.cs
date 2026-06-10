// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Compatibility shim for the removed FluentUI v4 MessageBoxContent.
/// </summary>
public sealed class MessageBoxContent
{
    public string? Title { get; set; }
    public MarkupString? MarkupMessage { get; set; }
    public Icon? Icon { get; set; }
    public Color? IconColor { get; set; }
    public MessageBarIntent? Intent { get; set; }
}
