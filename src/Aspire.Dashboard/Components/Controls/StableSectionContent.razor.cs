// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Provides content to a <see cref="Microsoft.AspNetCore.Components.Sections.SectionOutlet"/> without recreating it each
/// time the page that owns the content renders.
/// </summary>
/// <remarks>
/// A <see cref="Microsoft.AspNetCore.Components.Sections.SectionOutlet"/> keys its content by the
/// <see cref="Microsoft.AspNetCore.Components.Sections.SectionContent.ChildContent"/> delegate. Inline child content is a
/// new delegate on every render of the owning component, so the outlet would destroy and recreate everything in it each
/// time. That resets the controls in the section, for example closing a dropdown the user has just opened on a page that
/// re-renders as data arrives. This component hands the outlet one delegate for its whole lifetime, and that delegate
/// renders the most recent child content.
/// </remarks>
public partial class StableSectionContent : ComponentBase
{
    private readonly RenderFragment _content;

    public StableSectionContent()
    {
        _content = builder => builder.AddContent(0, ChildContent);
    }

    [Parameter, EditorRequired]
    public required string SectionName { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }
}
