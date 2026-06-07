// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Microsoft.Extensions.Localization;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model.ResourceGraph;

internal sealed class TelemetryGraphResources
{
    public static List<ResourceDto> CreateResourceDtos(
        IReadOnlyList<ResourceViewModel> activeResources,
        IReadOnlyList<TelemetryGraphEdge> edgeKeys,
        IDictionary<string, ResourceViewModel> resourceByName,
        IStringLocalizer<Columns> columnsLoc,
        string telemetryResourceType,
        bool showHiddenResources,
        IconResolver iconResolver)
    {
        var activeResourcesByName = activeResources.ToDictionary(r => r.Name, StringComparers.ResourceName);
        var resourceNameByResourceKey = new Dictionary<ResourceKey, string>(activeResources.Count * 2);
        foreach (var resource in activeResources)
        {
            resourceNameByResourceKey[ResourceKey.Create(resource.DisplayName, resource.Name)] = resource.Name;
            resourceNameByResourceKey[new ResourceKey(resource.DisplayName, resource.Name)] = resource.Name;
        }

        var referencedNames = new Dictionary<string, HashSet<string>>(StringComparers.ResourceName);
        var telemetryResourceKeysByName = new Dictionary<string, ResourceKey>(StringComparers.ResourceName);
        foreach (var edge in edgeKeys)
        {
            // Telemetry can arrive in the standalone dashboard without any AppHost resource model.
            // In that case the raw OTLP resource key becomes the node identity. If a dashboard
            // resource exists, use it instead so the node gets the richer icon/state/endpoint data.
            var sourceName = ResolveResourceName(edge.Source);
            var destinationName = ResolveResourceName(edge.Destination);
            if (string.Equals(sourceName, destinationName, StringComparisons.ResourceName))
            {
                continue;
            }

            ref var names = ref CollectionsMarshal.GetValueRefOrAddDefault(referencedNames, sourceName, out _);
            names ??= new HashSet<string>(StringComparers.ResourceName);
            names.Add(destinationName);
            telemetryResourceKeysByName.TryAdd(sourceName, edge.Source);
            telemetryResourceKeysByName.TryAdd(destinationName, edge.Destination);
        }

        var dtos = new List<ResourceDto>(telemetryResourceKeysByName.Count);
        foreach (var (resourceName, resourceKey) in telemetryResourceKeysByName.OrderBy(kvp => kvp.Key, StringComparers.ResourceName))
        {
            IEnumerable<string> references = referencedNames.TryGetValue(resourceName, out var telemetryReferencedNames)
                ? telemetryReferencedNames
                : Array.Empty<string>();

            if (activeResourcesByName.TryGetValue(resourceName, out var resource))
            {
                dtos.Add(ResourceGraphMapper.MapResource(resource, resourceByName, columnsLoc, showHiddenResources, iconResolver, references));
            }
            else
            {
                dtos.Add(CreateTelemetryResourceDto(resourceName, resourceKey, telemetryResourceType, references));
            }
        }

        return dtos;

        string ResolveResourceName(ResourceKey resourceKey)
        {
            if (resourceNameByResourceKey.TryGetValue(resourceKey, out var resourceName))
            {
                return resourceName;
            }

            // Raw OTLP can report service.name="api" and service.instance.id="api-123".
            // Normalize only that already-composite raw shape to the dashboard-style "api-123".
            // Edge keys produced by ResourceKey.Create have already stripped the service-name
            // prefix, so normalizing those again would lose the service name entirely.
            if (resourceKey.InstanceId is { } instanceId &&
                (string.Equals(resourceKey.Name, instanceId, StringComparisons.ResourceName) ||
                (instanceId.Length >= resourceKey.Name.Length + 2 &&
                instanceId.StartsWith(resourceKey.Name, StringComparisons.ResourceName) &&
                instanceId[resourceKey.Name.Length] == '-')))
            {
                return ResourceKey.Create(resourceKey.Name, instanceId).GetCompositeName();
            }

            return resourceKey.GetCompositeName();
        }
    }

    private static ResourceDto CreateTelemetryResourceDto(string name, ResourceKey resourceKey, string telemetryResourceType, IEnumerable<string> referencedNames)
    {
        var resourceIcon = new Icons.Regular.Size24.AppGeneric();
        return new ResourceDto
        {
            Name = name,
            ResourceType = telemetryResourceType,
            DisplayName = resourceKey.Name,
            Uid = name,
            ResourceIcon = new IconDto
            {
                Path = ResourceGraphMapper.GetIconPathData(resourceIcon),
                Color = ColorGenerator.Instance.GetColorVariableByKey(name),
                Tooltip = telemetryResourceType
            },
            StateIcon = null,
            ReferencedNames = referencedNames.Distinct(StringComparers.ResourceName).Order(StringComparers.ResourceName).ToImmutableArray(),
            EndpointUrl = null,
            EndpointText = null
        };
    }
}
