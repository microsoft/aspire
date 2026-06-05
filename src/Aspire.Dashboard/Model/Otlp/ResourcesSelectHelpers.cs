// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Model.Otlp;

public static class ResourcesSelectHelpers
{
    public static SelectViewModel<ResourceTypeDetails> GetResource(this ICollection<SelectViewModel<ResourceTypeDetails>> resources, ILogger logger, string? name, bool canSelectGrouping, SelectViewModel<ResourceTypeDetails> fallbackViewModel)
    {
        if (name is null)
        {
            return SingleMatch(resources, logger, name: "(null)", fallbackViewModel, fallback: true);
        }

        var allowedMatches = resources.Where(e => SupportType(e.Id?.Type, canSelectGrouping)).ToList();

        // First attempt an exact match on the instance id.
        var instanceIdMatches = allowedMatches.Where(e => string.Equals(name, e.Id?.InstanceId, StringComparisons.ResourceName)).ToList();
        if (instanceIdMatches.Count == 1)
        {
            return SingleMatch(resources, logger, name, instanceIdMatches[0]);
        }
        else if (instanceIdMatches.Count == 0)
        {
            // Fallback to matching on resource name. This is commonly used when there is only one instance of the resource.
            var replicaSetMatches = allowedMatches.Where(e => e.Id?.Type != OtlpResourceType.Instance && string.Equals(name, e.Id?.ReplicaSetName, StringComparisons.ResourceName)).ToList();

            if (replicaSetMatches.Count == 1)
            {
                return SingleMatch(resources, logger, name, replicaSetMatches[0]);
            }
            else if (replicaSetMatches.Count == 0)
            {
                // No matches found so return the passed in fallback.
                return SingleMatch(resources, logger, name, fallbackViewModel, fallback: true);
            }
            else
            {
                return MultipleMatches(allowedMatches, logger, name, replicaSetMatches);
            }
        }
        else
        {
            return MultipleMatches(allowedMatches, logger, name, instanceIdMatches);
        }

        static SelectViewModel<ResourceTypeDetails> SingleMatch(ICollection<SelectViewModel<ResourceTypeDetails>> resources, ILogger logger, string name, SelectViewModel<ResourceTypeDetails> match, bool fallback = false)
        {
            // There is a single match. Log as much information as possible about resources.
            logger.LogDebug(
                """
                Single match found when getting resource '{Name}'. Fallback used: {Fallback}
                Available resources:
                {AvailableResources}
                Matched resource:
                {MatchedResource}
                """, name, fallback, string.Join(Environment.NewLine, resources), match);

            return match;
        }

        static SelectViewModel<ResourceTypeDetails> MultipleMatches(ICollection<SelectViewModel<ResourceTypeDetails>> resources, ILogger logger, string name, List<SelectViewModel<ResourceTypeDetails>> matches)
        {
            // There are multiple matches. Log as much information as possible about resources.
            logger.LogWarning(
                """
                Multiple matches found when getting resource '{Name}'.
                Available resources:
                {AvailableResources}
                Matched resources:
                {MatchedResources}
                """, name, string.Join(Environment.NewLine, resources), string.Join(Environment.NewLine, matches));

            // Return first match to not break app. Make the UI resilient to unexpectedly bad data.
            return matches[0];
        }
    }

    public static List<SelectViewModel<ResourceTypeDetails>> CreateResources(List<OtlpResource> resources)
    {
        var replicasByResourceName = OtlpResource.GetReplicasByResourceName(resources);

        var selectViewModels = new List<SelectViewModel<ResourceTypeDetails>>();

        foreach (var (resourceName, replicas) in replicasByResourceName)
        {
            if (replicas.Count == 1)
            {
                // not replicated
                var resource = replicas.Single();
                selectViewModels.Add(new SelectViewModel<ResourceTypeDetails>
                {
                    Id = ResourceTypeDetails.CreateSingleton(resource.ResourceKey.ToString(), resourceName),
                    Name = resourceName
                });

                continue;
            }

            // add a disabled "Resource" as a header
            selectViewModels.Add(new SelectViewModel<ResourceTypeDetails>
            {
                Id = ResourceTypeDetails.CreateResourceGrouping(resourceName, isReplicaSet: true),
                Name = resourceName
            });

            // add each individual replica
            selectViewModels.AddRange(replicas.Select(replica =>
                new SelectViewModel<ResourceTypeDetails>
                {
                    Id = ResourceTypeDetails.CreateReplicaInstance(replica.ResourceKey.ToString(), resourceName),
                    Name = OtlpHelpers.GetResourceName(replica, resources)
                }));
        }

        var sortedVMs = selectViewModels.OrderBy(vm => vm.Name, StringComparers.ResourceName).ToList();
        return sortedVMs;
    }

    private static bool SupportType(OtlpResourceType? type, bool canSelectGrouping)
    {
        if (type is OtlpResourceType.Instance or OtlpResourceType.Singleton)
        {
            return true;
        }

        if (canSelectGrouping && type is OtlpResourceType.ResourceGrouping)
        {
            return true;
        }

        return false;
    }
}
