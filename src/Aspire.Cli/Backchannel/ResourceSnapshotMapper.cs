// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Aspire.Shared;
using Aspire.Shared.Model;
using Aspire.Shared.Model.Serialization;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Maps <see cref="ResourceSnapshot"/> to <see cref="ResourceJson"/> for serialization.
/// </summary>
internal static class ResourceSnapshotMapper
{
    /// <summary>
    /// Filters resource snapshots by name, matching against both
    /// <see cref="ResourceSnapshot.Name"/> and <see cref="ResourceSnapshot.DisplayName"/>.
    /// </summary>
    internal static IEnumerable<ResourceSnapshot> WhereMatchesResourceName(IEnumerable<ResourceSnapshot> snapshots, string resourceName)
    {
        return snapshots.Where(s => string.Equals(s.Name, resourceName, StringComparisons.ResourceName)
                                 || string.Equals(s.DisplayName, resourceName, StringComparisons.ResourceName));
    }

    /// <summary>
    /// Maps a list of <see cref="ResourceSnapshot"/> to a list of <see cref="ResourceJson"/>.
    /// </summary>
    /// <param name="snapshots">The resource snapshots to map.</param>
    /// <param name="dashboardBaseUrl">Optional base URL of the Aspire Dashboard for generating resource URLs.</param>
    /// <param name="includeEnvironmentVariableValues">Whether to include environment variable values. Defaults to <c>true</c>. Set to <c>false</c> to exclude values for security reasons.</param>
    public static List<ResourceJson> MapToResourceJsonList(IEnumerable<ResourceSnapshot> snapshots, string? dashboardBaseUrl = null, bool includeEnvironmentVariableValues = true)
    {
        var snapshotList = snapshots.ToList();
        return snapshotList.Select(s => MapToResourceJson(s, snapshotList, dashboardBaseUrl, includeEnvironmentVariableValues)).ToList();
    }

    /// <summary>
    /// Maps a <see cref="ResourceSnapshot"/> to <see cref="ResourceJson"/>.
    /// </summary>
    /// <param name="snapshot">The resource snapshot to map.</param>
    /// <param name="allSnapshots">All resource snapshots for resolving relationships.</param>
    /// <param name="dashboardBaseUrl">Optional base URL of the Aspire Dashboard for generating resource URLs.</param>
    /// <param name="includeEnvironmentVariableValues">Whether to include environment variable values. Defaults to <c>true</c>. Set to <c>false</c> to exclude values for security reasons.</param>
    public static ResourceJson MapToResourceJson(ResourceSnapshot snapshot, IReadOnlyList<ResourceSnapshot> allSnapshots, string? dashboardBaseUrl = null, bool includeEnvironmentVariableValues = true)
    {
        var urls = snapshot.Urls
            .Select(u => new ResourceUrlJson
            {
                Name = u.Name,
                DisplayName = u.DisplayProperties?.DisplayName,
                Url = u.Url,
                IsInternal = u.IsInternal
            })
            .ToArray();

        var volumes = snapshot.Volumes
            .Select(v => new ResourceVolumeJson
            {
                Source = v.Source,
                Target = v.Target,
                MountType = v.MountType,
                IsReadOnly = v.IsReadOnly
            })
            .ToArray();

        var healthReports = snapshot.HealthReports.OrderBy(h => h.Name).ToDistinctDictionary(
            h => h.Name,
            h => new ResourceHealthReportJson
            {
                Status = h.Status,
                Description = h.Description,
                ExceptionMessage = h.ExceptionText
            });

        var environment = snapshot.EnvironmentVariables
            .Where(e => e.IsFromSpec)
            .OrderBy(e => e.Name)
            .ToDistinctDictionary(
                e => e.Name,
                e => includeEnvironmentVariableValues ? e.Value : null);

        var properties = snapshot.Properties.OrderBy(p => p.Key).ToDistinctDictionary(
            p => p.Key,
            p => p.Value);

        // Build relationships by matching DisplayName
        var relationships = new List<ResourceRelationshipJson>();
        foreach (var relationship in snapshot.Relationships)
        {
            var matches = allSnapshots
                .Where(r => string.Equals(r.DisplayName, relationship.ResourceName, StringComparisons.ResourceName))
                .ToList();

            foreach (var match in matches)
            {
                relationships.Add(new ResourceRelationshipJson
                {
                    Type = relationship.Type,
                    ResourceName = match.Name
                });
            }
        }

        // Only include enabled commands
        var commands = snapshot.Commands
            .Where(c => string.Equals(c.State, "Enabled", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name)
            .ToDistinctDictionary(
                c => c.Name,
                c => new ResourceCommandJson
                {
                    Description = c.Description,
                    ConfirmationMessage = c.ConfirmationMessage
                });

        // Get source information using the shared ResourceSourceViewModel
        var sourceViewModel = ResourceSource.GetSourceModel(snapshot.ResourceType, snapshot.Properties);

        // Generate dashboard URL for this resource if a base URL is provided
        string? dashboardUrl = null;
        if (!string.IsNullOrEmpty(dashboardBaseUrl))
        {
            var resourcePath = DashboardUrls.ResourcesUrl(snapshot.Name);
            dashboardUrl = DashboardUrls.CombineUrl(dashboardBaseUrl, resourcePath);
        }

        return new ResourceJson
        {
            Name = snapshot.Name,
            DisplayName = snapshot.DisplayName,
            ResourceType = snapshot.ResourceType,
            State = snapshot.State,
            StateStyle = snapshot.StateStyle,
            HealthStatus = snapshot.HealthStatus,
            Source = sourceViewModel?.Value,
            ExitCode = snapshot.ExitCode,
            CreationTimestamp = snapshot.CreatedAt,
            StartTimestamp = snapshot.StartedAt,
            StopTimestamp = snapshot.StoppedAt,
            DashboardUrl = dashboardUrl,
            Urls = urls,
            Volumes = volumes,
            Environment = environment,
            HealthReports = healthReports,
            Properties = properties,
            Relationships = relationships.ToArray(),
            Commands = commands
        };
    }

    /// <summary>
    /// Resolves a user-provided resource name to matching snapshots.
    /// First tries an exact match on <see cref="ResourceSnapshot.Name"/>, then falls back
    /// to matching by <see cref="ResourceSnapshot.DisplayName"/> only when the display name is
    /// unique (i.e., not a replica set).
    /// </summary>
    /// <param name="resourceName">The user-provided resource name to resolve.</param>
    /// <param name="snapshots">All available resource snapshots.</param>
    /// <returns>The matching snapshots.</returns>
    public static IReadOnlyList<ResourceSnapshot> ResolveResources(string resourceName, IReadOnlyList<ResourceSnapshot> snapshots)
    {
        // First try exact match on the unique resource Name.
        var exactMatches = snapshots.Where(s => string.Equals(s.Name, resourceName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactMatches.Count > 0)
        {
            return exactMatches;
        }

        // Fall back to matching by DisplayName, but only when there is exactly one match
        // (no replicas). When there are replicas the user must specify the full suffixed name.
        var displayNameMatches = snapshots.Where(s => string.Equals(s.DisplayName, resourceName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (displayNameMatches.Count == 1)
        {
            return displayNameMatches;
        }

        return [];
    }

    /// <summary>
    /// Gets the display name for a resource, returning the unique name if there are multiple resources
    /// with the same display name (replicas).
    /// </summary>
    /// <param name="resource">The resource to get the name for.</param>
    /// <param name="allResources">All resources to check for duplicates.</param>
    /// <returns>The display name if unique, otherwise the unique resource name.</returns>
    public static string GetResourceName(ResourceSnapshot resource, IDictionary<string, ResourceSnapshot> allResources)
    {
        return GetResourceName(resource, allResources.Values);
    }

    /// <summary>
    /// Gets the display name for a resource, returning the unique name if there are multiple resources
    /// with the same display name (replicas).
    /// </summary>
    /// <param name="resource">The resource to get the name for.</param>
    /// <param name="allResources">All resources to check for duplicates.</param>
    /// <returns>The display name if unique, otherwise the unique resource name.</returns>
    public static string GetResourceName(ResourceSnapshot resource, IEnumerable<ResourceSnapshot> allResources)
    {
        var count = 0;
        foreach (var item in allResources)
        {
            // Skip hidden resources
            if (string.Equals(item.State, "Hidden", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(item.DisplayName, resource.DisplayName, StringComparisons.ResourceName))
            {
                count++;
                if (count >= 2)
                {
                    // There are multiple resources with the same display name so they're part of a replica set.
                    // Need to use the name which has a unique ID to tell them apart.
                    return resource.Name;
                }
            }
        }

        return resource.DisplayName ?? resource.Name;
    }

    /// <summary>
    /// Determines whether a resource snapshot represents a hidden resource.
    /// A resource is hidden if its <see cref="ResourceSnapshot.IsHidden"/> flag is set
    /// or its <see cref="ResourceSnapshot.State"/> is "Hidden".
    /// </summary>
    internal static bool IsHiddenResource(ResourceSnapshot snapshot)
    {
        return snapshot.IsHidden || string.Equals(snapshot.State, "Hidden", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Filters a list of all resource snapshots based on hidden-resource visibility,
    /// returning the visible snapshot list and a set of hidden resource names for log filtering.
    /// When <paramref name="includeHidden"/> is <see langword="true"/> or <paramref name="resourceName"/>
    /// is specified, all resources are included and the hidden set is empty.
    /// </summary>
    /// <param name="allSnapshots">All resource snapshots (including hidden).</param>
    /// <param name="includeHidden">Whether the user explicitly requested hidden resources.</param>
    /// <param name="resourceName">The specific resource name requested, or <see langword="null"/> for all.</param>
    /// <returns>The effective include-hidden flag, the filtered snapshot list, and the set of hidden resource names.</returns>
    internal static (bool EffectiveIncludeHidden, List<ResourceSnapshot> Snapshots, HashSet<string> HiddenResourceNames) FilterHiddenResources(
        IReadOnlyList<ResourceSnapshot> allSnapshots,
        bool includeHidden,
        string? resourceName)
    {
        var effectiveIncludeHidden = includeHidden || resourceName is not null;

        var hiddenResourceNames = effectiveIncludeHidden
            ? new HashSet<string>(StringComparers.ResourceName)
            : new HashSet<string>(allSnapshots.Where(IsHiddenResource).Select(s => s.Name), StringComparers.ResourceName);

        var snapshots = effectiveIncludeHidden
            ? allSnapshots.ToList()
            : allSnapshots.Where(s => !IsHiddenResource(s)).ToList();

        return (effectiveIncludeHidden, snapshots, hiddenResourceNames);
    }
}
