// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.PropertyValues;

public partial class LaunchGroupValue
{
    private string _textPrefix = string.Empty;
    private string _textSuffix = string.Empty;

    [Parameter]
    public string Value { get; set; } = string.Empty;

    [Parameter]
    public string HighlightText { get; set; } = string.Empty;

    [Parameter]
    public string? TextFormat { get; set; }

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

    protected override void OnParametersSet()
    {
        _textPrefix = _textSuffix = string.Empty;
        if (TextFormat is { } format)
        {
            // Split localized text such as "Not in active launch group '{0}', deferred build"
            // around the placeholder so only the group name receives the member tooltip.
            var parts = format.Split("{0}", 2);
            if (parts.Length != 2)
            {
                throw new FormatException("The launch-group text format must contain a '{0}' placeholder.");
            }

            _textPrefix = parts[0];
            _textSuffix = parts[1];
        }
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
