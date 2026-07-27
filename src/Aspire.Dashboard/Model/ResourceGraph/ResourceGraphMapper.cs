// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model.ResourceGraph;

public static class ResourceGraphMapper
{
    public static ResourceDto MapResource(ResourceViewModel r, IDictionary<string, ResourceViewModel> resourcesByName, IStringLocalizer<Columns> columnsLoc, bool showHiddenResources, IconResolver iconResolver)
    {
        var resolvedNames = new List<string>();

        // Remove relationships back to the current resource. The graph doesn't display self referential relationships.
        var filteredRelationships = r.Relationships.Where(relationship => relationship.ResourceName != r.DisplayName);

        foreach (var resourceRelationships in filteredRelationships.GroupBy(r => r.ResourceName, StringComparers.ResourceName))
        {
            var matches = resourcesByName.Values
                .Where(r => string.Equals(r.DisplayName, resourceRelationships.Key, StringComparisons.ResourceName))
                .Where(r => !r.IsResourceHidden(showHiddenResources))
                .ToList();

            foreach (var match in matches)
            {
                resolvedNames.Add(match.Name);
            }
        }

        var endpoint = ResourceUrlHelpers.GetUrls(r, includeInternalUrls: false, includeNonEndpointUrls: false).FirstOrDefault()
            ?? ResourceUrlHelpers.GetUrls(r, includeInternalUrls: false, includeNonEndpointUrls: true).FirstOrDefault();
        var resolvedEndpointText = r.IsParameter ? null : ResolvedEndpointText(endpoint);
        var resourceName = ResourceViewModel.GetResourceName(r, resourcesByName);
        var color = ColorGenerator.Instance.GetColorVariableByKey(resourceName);

        var customIcon = ResourceIconHelpers.ResolveCustomIcon(iconResolver, r, IconSize.Size24);
        var icon = customIcon?.Content ?? DeckIconData.GetInnerMarkup(ResourceIconHelpers.GetDeckIconForResource(r));

        // The graph shows resource state as a tone-colored dot rather than a glyph: map the Deck state
        // tone (success/info/warning/error/neutral) to its CSS color variable.
        var stateTone = ResourceStateTone.Get(r);
        var stateColor = stateTone switch
        {
            "success" => "var(--success)",
            "info" => "var(--info)",
            "warning" => "var(--warning)",
            "error" => "var(--error)",
            _ => "var(--neutral)"
        };
        var stateText = ResourceStateViewModel.GetStateViewModel(r, columnsLoc).Text;

        var dto = new ResourceDto
        {
            Name = r.Name,
            ResourceType = r.ResourceType,
            DisplayName = ResourceViewModel.GetResourceName(r, resourcesByName),
            Uid = r.Uid,
            ResourceIcon = new IconDto
            {
                Svg = icon,
                UsesFill = customIcon is not null,
                Name = customIcon is not null ? r.IconName : null,
                Variant = customIcon is not null ? (r.IconVariant ?? IconVariant.Filled).ToString().ToLowerInvariant() : null,
                Color = color,
                Tooltip = r.ResourceType
            },
            StateIcon = new IconDto
            {
                Color = stateColor,
                Tooltip = stateText ?? r.State
            },
            ReferencedNames = resolvedNames.Distinct().OrderBy(n => n).ToImmutableArray(),
            EndpointUrl = r.IsParameter ? null : endpoint?.Url,
            EndpointText = resolvedEndpointText
        };

        return dto;
    }

    private static string ResolvedEndpointText(DisplayedUrl? endpoint)
    {
        var text = endpoint?.OriginalUrlString;
        if (string.IsNullOrEmpty(text))
        {
            return ControlsStrings.ResourceGraphNoEndpoints;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return $"{uri.Host}:{uri.Port}";
        }

        return text;
    }
}
