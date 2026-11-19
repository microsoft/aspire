// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Contains the launch-group metadata advertised by the connected AppHost.
/// </summary>
public sealed class LaunchGroupInformationViewModel
{
    private LaunchGroupInformationViewModel(
        string? selectedGroup,
        ImmutableArray<string> groups,
        ImmutableDictionary<string, ResourceLaunchGroupViewModel> resources)
    {
        SelectedGroup = selectedGroup;
        Groups = groups;
        Resources = resources;
    }

    internal string? SelectedGroup { get; }
    internal ImmutableArray<string> Groups { get; }
    internal ImmutableDictionary<string, ResourceLaunchGroupViewModel> Resources { get; }

    internal static LaunchGroupInformationViewModel Create(LaunchGroupInformation information)
    {
        var selectedGroup = information.HasSelectedGroup ? information.SelectedGroup : null;
        var resources = ImmutableDictionary.CreateBuilder<string, ResourceLaunchGroupViewModel>(StringComparers.ResourceName);

        foreach (var resource in information.Resources)
        {
            resources[resource.Name] = new ResourceLaunchGroupViewModel(
                selectedGroup,
                DistinctNames(resource.Groups),
                resource.HasInheritedFrom ? resource.InheritedFrom : null,
                resource.ExplicitStart,
                resource.ExclusionReason,
                resource.BuildType);
        }

        return new LaunchGroupInformationViewModel(
            selectedGroup,
            DistinctNames(information.Groups),
            resources.ToImmutable());
    }

    internal ResourceLaunchGroupViewModel? FindResource(string displayName)
    {
        return Resources.GetValueOrDefault(displayName);
    }

    internal IEnumerable<string> GetMembers(string group, IEnumerable<ResourceViewModel> resources, bool showHiddenResources)
    {
        var hiddenNames = resources
            .GroupBy(resource => resource.DisplayName, StringComparers.ResourceName)
            .Where(replicas => replicas.All(resource => resource.IsResourceHidden(showHiddenResources)))
            .Select(replicas => replicas.Key)
            .ToHashSet(StringComparers.ResourceName);

        return Resources
            .Where(resource => resource.Value.Groups.Contains(group, StringComparer.OrdinalIgnoreCase) && !hiddenNames.Contains(resource.Key))
            .Select(resource => resource.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal);
    }

    internal void ApplyToResource(ResourceViewModel resource)
    {
        resource.LaunchGroup = FindResource(resource.DisplayName);
    }

    internal ConcurrentDictionary<string, bool> CreateVisibility()
    {
        var visibility = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in Groups)
        {
            visibility.TryAdd(group, true);
        }

        foreach (var resource in Resources.Values)
        {
            if (resource.Groups.IsEmpty)
            {
                visibility.TryAdd(string.Empty, true);
            }
            else
            {
                foreach (var group in resource.Groups)
                {
                    visibility.TryAdd(group, true);
                }
            }
        }

        return visibility;
    }

    internal static void AddVisibility(LaunchGroupInformationViewModel? information, ConcurrentDictionary<string, bool> visibility)
    {
        if (information is null)
        {
            return;
        }

        foreach (var (group, visible) in information.CreateVisibility())
        {
            visibility.TryAdd(group, visible);
        }
    }

    internal static void SetHiddenGroups(ConcurrentDictionary<string, bool> visibility, IEnumerable<string> hiddenGroups)
    {
        foreach (var group in visibility.Keys)
        {
            visibility[group] = true;
        }

        // Ignore hidden groups from a stale URL or session that the current AppHost no longer advertises.
        foreach (var group in hiddenGroups)
        {
            if (visibility.ContainsKey(group))
            {
                visibility[group] = false;
            }
        }
    }

    internal static bool IsVisible(ResourceViewModel resource, IReadOnlyDictionary<string, bool> visibility)
    {
        if (resource.LaunchGroup is not { } launchGroup || visibility.Count == 0)
        {
            // Older AppHosts and resources omitted from the additive metadata retain the default visibility.
            return true;
        }

        if (launchGroup.Groups.IsEmpty)
        {
            return !visibility.TryGetValue(string.Empty, out var visible) || visible;
        }

        return launchGroup.Groups.Any(group => !visibility.TryGetValue(group, out var visible) || visible);
    }

    internal static IEnumerable<string> GetFilterKeys(ResourceViewModel resource)
    {
        return resource.LaunchGroup switch
        {
            null => [],
            { Groups.IsEmpty: true } => [string.Empty],
            { } launchGroup => launchGroup.Groups
        };
    }

    private static ImmutableArray<string> DistinctNames(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. names.Where(seen.Add)];
    }
}

internal sealed record ResourceLaunchGroupViewModel(
    string? SelectedGroup,
    ImmutableArray<string> Groups,
    string? InheritedFrom,
    bool ExplicitStart,
    LaunchGroupExclusionReason ExclusionReason,
    LaunchGroupBuildType BuildType);

internal static class LaunchGroupDisplayHelpers
{
    internal const string EffectiveMembershipPropertyKey = "resource-launch-group-effective-membership";

    internal static IEnumerable<ResourcePropertyViewModel> GetProperties(ResourceLaunchGroupViewModel? launchGroup, IStringLocalizer<Resources.Resources> loc)
    {
        if (launchGroup is null)
        {
            return [];
        }

        var sortOrder = int.MaxValue - 7;
        var properties = new List<ResourcePropertyViewModel>
        {
            CreateProperty(EffectiveMembershipPropertyKey, nameof(Resources.Resources.ResourceLaunchGroupsEffectiveMembership), GetGroupsText(launchGroup, loc), sortOrder++)
        };
        if (launchGroup.InheritedFrom is { Length: > 0 } inheritedFrom)
        {
            properties.Add(CreateProperty("resource-launch-group-inherited-from", nameof(Resources.Resources.ResourceLaunchGroupsInheritedFrom), inheritedFrom, sortOrder++));
        }
        if (launchGroup.ExplicitStart)
        {
            properties.Add(CreateProperty("resource-launch-group-explicit-start", nameof(Resources.Resources.ResourceLaunchGroupsExplicitStart), loc[nameof(Resources.Resources.ResourceLaunchGroupsYes)], sortOrder++));
        }
        return properties;
    }

    private static ResourcePropertyViewModel CreateProperty(string key, string displayNameResourceName, string value, int sortOrder)
    {
        return new ResourcePropertyViewModel(
            name: key,
            value: Value.ForString(value),
            isValueSensitive: false,
            knownProperty: new KnownProperty(key, localizer => localizer[displayNameResourceName]),
            sortOrder: sortOrder,
            displayName: null,
            isHighlighted: false);
    }

    internal static string GetGroupsText(ResourceLaunchGroupViewModel launchGroup, IStringLocalizer<Resources.Resources> loc)
    {
        return launchGroup.Groups.IsEmpty
            ? loc[nameof(Resources.Resources.ResourceLaunchGroupsUngrouped)]
            : string.Join(", ", launchGroup.Groups);
    }
}
