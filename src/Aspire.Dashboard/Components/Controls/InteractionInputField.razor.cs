// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Model.Markdown;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class InteractionInputField(LibraryConfiguration configuration) : FluentComponentBase(configuration)
{
    [Parameter]
    public required string? ForId { get; set; }

    [Parameter]
    public required InputViewModel InputViewModel { get; set; }

    [Parameter]
    public required RenderFragment<InteractionInputFieldContext> ChildContent { get; set; }

    [Parameter]
    public required MarkdownProcessor MarkdownProcessor { get; set; }
}

public sealed class InteractionInputFieldContext
{
    public required string? DescriptionId { get; init; }
    public required InputViewModel ViewModel { get; init; }
}
