// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.GenAI;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

public partial class GenAIItemTitle
{
    [Parameter, EditorRequired]
    public required GenAIItemViewModel Item { get; set; }

    [Parameter, EditorRequired]
    public required string ResourceName { get; set; }

    [Parameter]
    public bool ShowIcon { get; set; } = true;

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    private BadgeDetail? _categoryBadge;
    private BadgeDetail? _titleBadge;

    protected override void OnParametersSet()
    {
        _categoryBadge = Item.GetCategoryBadge(Loc);
        _titleBadge = Item.GetTitleBadge(Loc);
    }
}
