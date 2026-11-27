// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// The overview card that displays and edits a resource's tags.
/// </summary>
public sealed partial class OverviewTagsCard : ComponentBase
{
    [Parameter, EditorRequired]
    public required ResourceViewModel Resource { get; set; }

    /// <summary>
    /// Gets or sets the tags of all resources, which are suggested when adding a tag.
    /// </summary>
    [Parameter]
    public IReadOnlyList<string> AvailableTags { get; set; } = [];

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }
}
