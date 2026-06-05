// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Resources;

namespace Aspire.Hosting;

internal static class LaunchProfileExtensions
{
    // Allow list of command names that are supported by Aspire
    private static readonly string[] s_allowedCommandNames = ["Project", "Executable"];

    internal static LaunchSettings? GetLaunchSettings(this ProjectResource projectResource)
    {
        if (!projectResource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
        {
            throw new DistributedApplicationException(LaunchProfileStrings.ProjectDoesNotContainMetadataExceptionMessage);
        }

        // ExcludeLaunchProfileAnnotation disables getting launch settings. This ensures consumers of launch settings
        // never get a copy and can't use values from it to configure the application.
        if (projectResource.TryGetLastAnnotation<ExcludeLaunchProfileAnnotation>(out _))
        {
            return null;
        }

        return projectMetadata.GetLaunchSettings(projectResource.Name);
    }

    internal static NamedLaunchProfile? GetEffectiveLaunchProfile(this ProjectResource projectResource, bool throwIfNotFound = false)
    {
        string? launchProfileName = projectResource.SelectLaunchProfileName();
        if (string.IsNullOrEmpty(launchProfileName))
        {
            return null;
        }

        var launchProfile = projectResource.GetLaunchProfile(launchProfileName, throwIfNotFound);
        if (launchProfile == null)
        {
            return null;
        }

        return new NamedLaunchProfile(launchProfileName, launchProfile);
    }

    internal static LaunchProfile? GetLaunchProfile(this ProjectResource projectResource, string launchProfileName, bool throwIfNotFound = false)
    {
        var profiles = projectResource.GetLaunchSettings()?.Profiles;
        if (profiles is null)
        {
            return null;
        }

        var found = profiles.TryGetValue(launchProfileName, out var launchProfile);
        if (!found && throwIfNotFound)
        {
            var message = string.Format(CultureInfo.InvariantCulture, LaunchProfileStrings.LaunchSettingsFileDoesNotContainProfileExceptionMessage, launchProfileName);
            throw new DistributedApplicationException(message);
        }

        return launchProfile;
    }

    private static LaunchSettings? GetLaunchSettings(this IProjectMetadata projectMetadata, string resourceName)
    {
        // For testing
        if (projectMetadata.LaunchSettings is { } launchSettings)
        {
            return launchSettings;
        }

        if (!File.Exists(projectMetadata.ProjectPath))
        {
            var message = string.Format(CultureInfo.InvariantCulture, LaunchProfileStrings.ProjectFileNotFoundExceptionMessage, projectMetadata.ProjectPath);
            throw new DistributedApplicationException(message);
        }

        var projectFileInfo = new FileInfo(projectMetadata.ProjectPath);
        var launchSettingsFilePath = projectFileInfo.DirectoryName switch
        {
            null => Path.Combine("Properties", "launchSettings.json"),
            _ => Path.Combine(projectFileInfo.DirectoryName, "Properties", "launchSettings.json")
        };

        // It isn't mandatory that the launchSettings.json file exists!
        if (!File.Exists(launchSettingsFilePath))
        {
            if (!projectMetadata.IsFileBasedApp)
            {
                return null;
            }
            else
            {
                // For file-based apps, also check for a .run.json file next to the .cs file
                var runSettingsFilePath = Path.ChangeExtension(projectMetadata.ProjectPath, ".run.json");
                if (File.Exists(runSettingsFilePath))
                {
                    launchSettingsFilePath = runSettingsFilePath;
                }
                else
                {
                    return null;
                }
            }
        }

        return LaunchSettingsReader.ReadLaunchSettingsFile(launchSettingsFilePath, $"project resource '{resourceName}'");
    }

    private static readonly LaunchProfileSelector[] s_launchProfileSelectors =
    [
        TrySelectLaunchProfileFromAnnotation,
        TrySelectLaunchProfileFromDefaultAnnotation,
        TrySelectLaunchProfileByOrder
    ];

    private static bool TrySelectLaunchProfileByOrder(ProjectResource projectResource, [NotNullWhen(true)] out string? launchProfileName)
    {
        var launchSettings = GetLaunchSettings(projectResource);

        if (launchSettings == null || launchSettings.Profiles.Count == 0)
        {
            launchProfileName = null;
            return false;
        }

        // Find the first profile with an allowed command name
        foreach (var (profileName, profile) in launchSettings.Profiles)
        {
            if (string.IsNullOrEmpty(profile.CommandName) || Array.Exists(s_allowedCommandNames, name => string.Equals(name, profile.CommandName, StringComparison.OrdinalIgnoreCase)))
            {
                launchProfileName = profileName;
                return true;
            }
        }

        launchProfileName = null;
        return false;
    }

    private static bool TrySelectLaunchProfileFromDefaultAnnotation(ProjectResource projectResource, [NotNullWhen(true)] out string? launchProfileName)
    {
        if (!projectResource.TryGetLastAnnotation<DefaultLaunchProfileAnnotation>(out var launchProfileAnnotation))
        {
            launchProfileName = null;
            return false;
        }

        var appHostDefaultLaunchProfileName = launchProfileAnnotation.LaunchProfileName;
        var launchSettings = GetLaunchSettings(projectResource);
        if (launchSettings == null)
        {
            launchProfileName = null;
            return false;
        }

        if (!launchSettings.Profiles.TryGetValue(appHostDefaultLaunchProfileName, out var launchProfile) || launchProfile is null)
        {
            launchProfileName = null;
            return false;
        }

        launchProfileName = appHostDefaultLaunchProfileName;
        return true;
    }

    private static bool TrySelectLaunchProfileFromAnnotation(ProjectResource projectResource, [NotNullWhen(true)] out string? launchProfileName)
    {
        if (projectResource.TryGetLastAnnotation<LaunchProfileAnnotation>(out var launchProfileAnnotation))
        {
            launchProfileName = launchProfileAnnotation.LaunchProfileName;
            return true;
        }
        else
        {
            launchProfileName = null;
            return false;
        }
    }

    internal static string? SelectLaunchProfileName(this ProjectResource projectResource)
    {
        // ExcludeLaunchProfileAnnotation takes precedence over all other launch profile selectors.
        if (projectResource.TryGetLastAnnotation<ExcludeLaunchProfileAnnotation>(out _))
        {
            return null;
        }

        foreach (var launchProfileSelector in s_launchProfileSelectors)
        {
            if (launchProfileSelector(projectResource, out var launchProfile))
            {
                return launchProfile;
            }
        }

        return null;
    }
}

internal sealed record class NamedLaunchProfile(string Name, LaunchProfile LaunchProfile);

internal delegate bool LaunchProfileSelector(ProjectResource project, out string? launchProfile);
