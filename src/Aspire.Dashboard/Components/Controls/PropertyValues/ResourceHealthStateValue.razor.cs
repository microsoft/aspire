// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls.PropertyValues;

public partial class ResourceHealthStateValue
{
    private DeckIconName _iconName;
    private string _toneClass = string.Empty;

    [Parameter, EditorRequired]
    public required string Value { get; set; }

    [Parameter, EditorRequired]
    public required string HighlightText { get; set; }

    [Parameter, EditorRequired]
    public required ResourceViewModel Resource { get; set; }

    protected override void OnParametersSet()
    {
        (_iconName, _toneClass) = ResourceIconHelpers.GetHealthStatusDeckIcon(Resource.HealthStatus);
    }
}
