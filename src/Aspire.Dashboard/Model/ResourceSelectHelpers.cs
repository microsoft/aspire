// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Extensions;

namespace Aspire.Dashboard.Model;

internal static class ResourceSelectHelpers
{
    internal static ImmutableList<SelectViewModel<ResourceTypeDetails>> CreateOptions(
        IDictionary<string, ResourceViewModel> resourcesByName, string unknownStateText, bool showHiddenResources)
    {
        var builder = ImmutableList.CreateBuilder<SelectViewModel<ResourceTypeDetails>>();
        foreach (var grouping in resourcesByName.Values
            .Where(r => !r.IsResourceHidden(showHiddenResources))
            .OrderBy(r => r, ResourceViewModelNameComparer.Instance)
            .GroupBy(r => r.DisplayName, StringComparers.ResourceName))
        {
            var isReplica = grouping.Count() > 1;
            if (isReplica)
            {
                builder.Add(new()
                {
                    Id = ResourceTypeDetails.CreateResourceGrouping(grouping.Key, true),
                    Name = grouping.Key
                });
            }

            foreach (var resource in grouping)
            {
                var name = ResourceViewModel.GetResourceName(resource, resourcesByName);
                builder.Add(new()
                {
                    Id = isReplica
                        ? ResourceTypeDetails.CreateReplicaInstance(resource.Name, grouping.Key)
                        : ResourceTypeDetails.CreateSingleton(resource.Name, grouping.Key),
                    Name = resource.HasNoState() ? $"{name} ({unknownStateText})"
                        : resource.IsRunningState() ? name
                        : $"{name} ({resource.State})"
                });
            }
        }

        return builder.ToImmutable();
    }

    internal static bool HasUsableTerminal(ResourceViewModel resource)
        => resource.HasTerminal();
}
