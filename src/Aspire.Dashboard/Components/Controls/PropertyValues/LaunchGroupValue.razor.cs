// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.PropertyValues;

public partial class LaunchGroupValue
{
    [Parameter]
    public string Value { get; set; } = string.Empty;

    [Parameter]
    public string HighlightText { get; set; } = string.Empty;

    [Parameter]
    public IReadOnlyList<string> Groups { get; set; } = [];

    [Parameter]
    public bool ShowHiddenResources { get; set; }

    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Resources> Loc { get; init; }

    internal static void AddValueComponents(Dictionary<string, ComponentMetadata> components, ResourceLaunchGroupViewModel launchGroup, bool showHiddenResources)
    {
        components[LaunchGroupDisplayHelpers.SelectedGroupPropertyKey] = CreateMetadata(LaunchGroupDisplayHelpers.GetSelectedGroups(launchGroup), showHiddenResources);
        components[LaunchGroupDisplayHelpers.EffectiveMembershipPropertyKey] = CreateMetadata(launchGroup.Groups, showHiddenResources);
    }

    private static ComponentMetadata CreateMetadata(IReadOnlyList<string> groups, bool showHiddenResources)
    {
        return new ComponentMetadata
        {
            Type = typeof(LaunchGroupValue),
            Parameters = { ["Groups"] = groups, ["ShowHiddenResources"] = showHiddenResources }
        };
    }

    private string GetMembersText(LaunchGroupInformationViewModel information, string group)
    {
        IEnumerable<ResourceViewModel> resources = ResourcesLayout is { } layout
            ? layout.ResourceByName.Values
            : DashboardClient.GetResources();
        var members = information.GetMembers(group, resources, ResourcesLayout?.ShowHiddenResources ?? ShowHiddenResources).ToArray();
        return members.Length == 0 ? Loc[nameof(Resources.Resources.ResourceLaunchGroupsNone)] : string.Join(", ", members);
    }
}
