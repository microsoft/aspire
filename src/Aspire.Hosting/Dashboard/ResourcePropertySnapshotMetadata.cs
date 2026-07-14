// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using static Aspire.Hosting.Resources.MessageStrings;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Provides dashboard display metadata for resource-specific properties emitted by Aspire.Hosting.
/// </summary>
/// <remarks>
/// This keeps resource-specific labels, default visibility, and relative ordering with the resource
/// producer instead of requiring the dashboard to hard-code those property names. Generic
/// dashboard-owned properties, such as state and health, are still handled by the dashboard.
/// </remarks>
internal static class ResourcePropertySnapshotMetadata
{
    internal static ResourcePropertySnapshot Create(string resourceType, string name, object? value, bool isSensitive = false)
    {
        var (displayName, isHighlighted, sortOrder) = Get(resourceType, name);

        return new(name, value)
        {
            IsSensitive = isSensitive,
            DisplayName = displayName,
            IsHighlighted = isHighlighted,
            SortOrder = sortOrder
        };
    }

    // Some producers update one property on an existing snapshot rather than replacing a full
    // property set. Keep the metadata lookup separate from Create so those update paths can
    // preserve the same label, visibility, and ordering without constructing a throwaway snapshot.
    internal static (string? DisplayName, bool IsHighlighted, int? SortOrder) Get(string resourceType, string name)
    {
        return (resourceType, name) switch
        {
            (KnownResourceTypes.Container, KnownProperties.Container.Image) => (ResourcePropertyContainerImageDisplayName, true, 0),
            (KnownResourceTypes.Container, KnownProperties.Container.Id) => (ResourcePropertyContainerIdDisplayName, true, 1),
            (KnownResourceTypes.Container, KnownProperties.Container.Command) => (ResourcePropertyContainerCommandDisplayName, true, 2),
            (KnownResourceTypes.Container, KnownProperties.Container.Args) => (ResourcePropertyContainerArgumentsDisplayName, true, 3),
            (KnownResourceTypes.Container, KnownProperties.Container.Ports) => (ResourcePropertyContainerPortsDisplayName, true, 4),
            (KnownResourceTypes.Container, KnownProperties.Container.Lifetime) => (ResourcePropertyContainerLifetimeDisplayName, true, 5),
            (KnownResourceTypes.Executable, KnownProperties.Executable.Path) => (ResourcePropertyExecutablePathDisplayName, true, 0),
            (KnownResourceTypes.Executable, KnownProperties.Executable.WorkDir) => (ResourcePropertyExecutableWorkingDirectoryDisplayName, true, 1),
            (KnownResourceTypes.Executable, KnownProperties.Executable.Args) => (ResourcePropertyExecutableArgumentsDisplayName, true, 2),
            (KnownResourceTypes.Executable, KnownProperties.Executable.Pid) => (ResourcePropertyExecutableProcessIdDisplayName, true, 3),
            (KnownResourceTypes.Project, KnownProperties.Project.Path) => (ResourcePropertyProjectPathDisplayName, true, 0),
            (KnownResourceTypes.Project, KnownProperties.Project.LaunchProfile) => (ResourcePropertyProjectLaunchProfileDisplayName, true, 1),
            (KnownResourceTypes.Project, KnownProperties.Executable.Pid) => (ResourcePropertyExecutableProcessIdDisplayName, true, 2),
            _ => (null, false, null)
        };
    }
}
