// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREPROJECTS001

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
        resource.TryGetLastAnnotation<ProjectLaunchDefaultsAnnotation>(out var launchDefaults);
        var projectLaunchConfiguration = new ProjectLaunchConfiguration
        {
            Type = GetLaunchConfigurationType(resource, projectMetadata),
            ProjectPath = projectMetadata.ProjectPath,
            Mode = mode,
            BuildConfiguration = launchDefaults?.BuildConfiguration,
            BuildEnvironment = projectMetadata.BuildEnvironment.Count == 0
                ? null
                : projectMetadata.BuildEnvironment.ToDictionary(),
            BuildWorkingDirectory = projectMetadata.BuildWorkingDirectory,
            SuppressBuild = ShouldSuppressIdeBuild(resource, projectMetadata),
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

    internal static string GetLaunchConfigurationType(IResource resource, IProjectMetadata projectMetadata) =>
        ShouldSuppressIdeBuild(resource, projectMetadata)
            ? KnownLaunchConfigurationTypes.ProjectWithExternalBuild
            : KnownLaunchConfigurationTypes.Project;

    private static bool ShouldSuppressIdeBuild(IResource resource, IProjectMetadata projectMetadata)
    {
        // Generated metadata for classic ProjectResource instances has long used SuppressBuild=true to keep
        // the DCP process path from rebuilding outputs already produced with the AppHost. The IDE contract
        // historically still rebuilt missing output and honored force-build requests for those resources.
        // Preserve that behavior while allowing Project v2 executable resources to delegate build ownership
        // to their coordinated build.
        return resource is not ProjectResource && projectMetadata.SuppressBuild;
    }
}
