// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.GenAI;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

public partial class TreeGenAISelector
{
    [Parameter, EditorRequired]
    public required Func<Task> HandleSelectedTreeItemChangedAsync { get; set; }

    [Parameter, EditorRequired]
    public required GenAIVisualizerDialogViewModel PageViewModel { get; set; }

    [Parameter, EditorRequired]
    public required GenAIItemViewModel? SelectedItem { get; set; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    private string GetSelectedTreeItemId()
    {
        return SelectedItem is { } item ? GetItemTreeItemId(item.Index) : "genai-span";
    }

    private static string GetItemTreeItemId(int index)
    {
        return $"genai-item-{index}";
    }
}
