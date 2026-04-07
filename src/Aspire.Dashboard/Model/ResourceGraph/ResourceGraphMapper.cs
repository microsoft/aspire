// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Xml.Linq;
using Aspire.Dashboard.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Extensions;

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
        var resolvedEndpointText = ResolvedEndpointText(endpoint);
        var resourceName = ResourceViewModel.GetResourceName(r, resourcesByName);
        var color = ColorGenerator.Instance.GetColorVariableByKey(resourceName);

        var icon = GetIconPathData(ResourceIconHelpers.GetIconForResource(iconResolver, r, IconSize.Size24));

        var stateIcon = ResourceStateViewModel.GetStateViewModel(r, columnsLoc);

        var dto = new ResourceDto
        {
            Name = r.Name,
            ResourceType = r.ResourceType,
            DisplayName = ResourceViewModel.GetResourceName(r, resourcesByName),
            Uid = r.Uid,
            ResourceIcon = new IconDto
            {
                Path = icon,
                Color = color,
                Tooltip = r.ResourceType
            },
            StateIcon = new IconDto
            {
                Path = GetIconPathData(stateIcon.Icon),
                Color = stateIcon.Color.ToAttributeValue()!,
                Tooltip = stateIcon.Text ?? r.State
            },
            ReferencedNames = resolvedNames.Distinct().OrderBy(n => n).ToImmutableArray(),
            EndpointUrl = endpoint?.Url,
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

    public static string GetIconPathData(Icon icon)
    {
        var p = icon.Content;
        var e = XElement.Parse(p);
        return e.Attribute("d")!.Value;
    }
}
