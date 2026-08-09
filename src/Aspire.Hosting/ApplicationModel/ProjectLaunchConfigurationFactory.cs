// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Builds the <see cref="ProjectLaunchConfiguration"/> that Aspire hands to the IDE for a .NET resource.
/// </summary>
internal static class ProjectLaunchConfigurationFactory
{
    public static ProjectLaunchConfiguration Create(IResource resource, string mode)
    {
        if (!resource.TryGetProjectMetadata(out var projectMetadata))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' cannot produce a \"{KnownLaunchConfigurationTypes.Project}\" launch configuration because it has no project metadata. " +
                $"The \"{KnownLaunchConfigurationTypes.Project}\" launch configuration type is reserved for .NET project resources; use a resource that carries {nameof(IProjectMetadata)} or a different launch configuration type.");
        }

        return Create(resource, projectMetadata, mode);
    }

    public static ProjectLaunchConfiguration Create(IResource resource, IProjectMetadata projectMetadata, string mode)
    {
        var projectLaunchConfiguration = new ProjectLaunchConfiguration
        {
            ProjectPath = projectMetadata.ProjectPath,
            Mode = mode,
            // The launch profile selection lives on the resource rather than on the project metadata, so it
            // can only be resolved when the configuration is produced, not when debug support is registered.
            DisableLaunchProfile = resource.TryGetLastAnnotation<ExcludeLaunchProfileAnnotation>(out _)
        };

        // Use the effective launch profile which has fallback logic
        if (!projectLaunchConfiguration.DisableLaunchProfile && resource.GetEffectiveLaunchProfile() is NamedLaunchProfile namedLaunchProfile)
        {
            projectLaunchConfiguration.LaunchProfile = namedLaunchProfile.Name;
        }

        return projectLaunchConfiguration;
    }
}
