// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using static Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Provides temporary display metadata for resource-specific properties emitted by older resource servers.
/// </summary>
internal static class LegacyResourcePropertyMetadata
{
    /// <summary>
    /// Gets legacy metadata for resource-specific properties that predate producer-supplied display metadata.
    /// </summary>
    internal static (int SortOrder, KnownProperty KnownProperty)? Get(string resourceType, string propertyName)
    {
        var metadata = (resourceType, propertyName) switch
        {
            (KnownResourceTypes.Container, KnownProperties.Container.Image) => Create(KnownProperties.Container.Image, nameof(ResourcesDetailsContainerImageProperty), KnownResourcePropertySortOrder.ProducerDefinedStart),
            (KnownResourceTypes.Container, KnownProperties.Container.Id) => Create(KnownProperties.Container.Id, nameof(ResourcesDetailsContainerIdProperty), KnownResourcePropertySortOrder.ProducerDefinedStart + 1),
            (KnownResourceTypes.Container, KnownProperties.Container.Command) => Create(KnownProperties.Container.Command, nameof(ResourcesDetailsContainerCommandProperty), KnownResourcePropertySortOrder.ProducerDefinedStart + 2),
            (KnownResourceTypes.Container, KnownProperties.Container.Args) => Create(KnownProperties.Container.Args, nameof(ResourcesDetailsContainerArgumentsProperty), KnownResourcePropertySortOrder.ProducerDefinedStart + 3),
            (KnownResourceTypes.Container, KnownProperties.Container.Ports) => Create(KnownProperties.Container.Ports, nameof(ResourcesDetailsContainerPortsProperty), KnownResourcePropertySortOrder.ProducerDefinedStart + 4),
            (KnownResourceTypes.Container, KnownProperties.Container.Lifetime) => Create(KnownProperties.Container.Lifetime, nameof(ResourcesDetailsContainerLifetimeProperty), KnownResourcePropertySortOrder.ProducerDefinedStart + 5),
            (KnownResourceTypes.Executable, KnownProperties.Executable.Path) => Create(KnownProperties.Executable.Path, nameof(ResourcesDetailsExecutablePathProperty), KnownResourcePropertySortOrder.ProducerDefinedStart),
            (KnownResourceTypes.Executable, KnownProperties.Executable.WorkDir) => Create(KnownProperties.Executable.WorkDir, nameof(ResourcesDetailsExecutableWorkingDirectoryProperty), KnownResourcePropertySortOrder.ProducerDefinedStart + 1),
            (KnownResourceTypes.Executable, KnownProperties.Executable.Args) => Create(KnownProperties.Executable.Args, nameof(ResourcesDetailsExecutableArgumentsProperty), KnownResourcePropertySortOrder.ProducerDefinedStart + 2),
            (KnownResourceTypes.Executable, KnownProperties.Executable.Pid) => Create(KnownProperties.Executable.Pid, nameof(ResourcesDetailsExecutableProcessIdProperty), KnownResourcePropertySortOrder.ProducerDefinedStart + 3),
            (KnownResourceTypes.Project, KnownProperties.Project.Path) => Create(KnownProperties.Project.Path, nameof(ResourcesDetailsProjectPathProperty), KnownResourcePropertySortOrder.ProducerDefinedStart),
            (KnownResourceTypes.Project, KnownProperties.Project.LaunchProfile) => Create(KnownProperties.Project.LaunchProfile, nameof(ResourcesDetailsProjectLaunchProfileProperty), KnownResourcePropertySortOrder.ProducerDefinedStart + 1),
            (KnownResourceTypes.Project, KnownProperties.Executable.Pid) => Create(KnownProperties.Executable.Pid, nameof(ResourcesDetailsExecutableProcessIdProperty), KnownResourcePropertySortOrder.ProducerDefinedStart + 2),
            (KnownResourceTypes.Parameter, KnownProperties.Parameter.Value) => Create(KnownProperties.Parameter.Value, nameof(ResourcesDetailsParameterValueProperty), KnownResourcePropertySortOrder.ProducerDefinedStart),
            _ => ((int SortOrder, KnownProperty KnownProperty)?)null
        };

        return metadata;
    }

    private static (int SortOrder, KnownProperty KnownProperty) Create(string propertyName, string displayNameResourceName, int sortOrder)
    {
        // This fallback exists only for dashboards connected to pre-metadata resource servers.
        // New resource-specific properties should be emitted with producer metadata instead.
        return (sortOrder, new(propertyName, loc => loc[displayNameResourceName]));
    }
}
