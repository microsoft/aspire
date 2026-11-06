// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Internal use of launch-group membership annotations.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Orchestrator;

internal sealed class LaunchGroupService(
    IConfiguration configuration,
    DistributedApplicationExecutionContext executionContext,
    ILogger<LaunchGroupService> logger)
{
    internal const string ConfigurationSectionName = "Aspire:LaunchGroups";

    public void Configure(DistributedApplicationModel model)
    {
        if (executionContext.IsPublishMode)
        {
            return;
        }

        var declaredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredGroups = configuration.GetSection(ConfigurationSectionName);
        if (configuredGroups.Value is not null)
        {
            throw new DistributedApplicationException(
                $"{ConfigurationSectionName} must be an array of group names.");
        }

        foreach (var entry in configuredGroups.GetChildren())
        {
            if (entry.Value is not { } name)
            {
                throw new DistributedApplicationException(
                    $"{ConfigurationSectionName} configuration entry '{entry.Key}' must be a group name.");
            }

            declaredGroups.Add(LaunchGroupAnnotation.ValidateName(name, ConfigurationSectionName));
        }

        var memberships = model.Resources.SelectMany(resource => resource.Annotations.OfType<LaunchGroupAnnotation>()).ToArray();
        if (declaredGroups.Count > 0)
        {
            foreach (var resource in model.Resources)
            {
                foreach (var membership in resource.Annotations.OfType<LaunchGroupAnnotation>())
                {
                    if (!declaredGroups.Contains(membership.LaunchGroupName))
                    {
                        throw new DistributedApplicationException($"Resource '{resource.Name}' belongs to undefined launch group '{membership.LaunchGroupName}'.");
                    }
                }
            }
        }
        else
        {
            declaredGroups.UnionWith(memberships.Select(annotation => annotation.LaunchGroupName));
        }

        var selectedGroup = configuration["Aspire:LaunchGroup"];
        if (string.IsNullOrEmpty(selectedGroup))
        {
            return;
        }

        if (!declaredGroups.Contains(selectedGroup))
        {
            var validGroupsDescription = declaredGroups.Count == 0
                ? "No launch groups have been defined."
                : $"Select one of: {string.Join(", ", declaredGroups.Order(StringComparer.OrdinalIgnoreCase))}.";
            throw new DistributedApplicationException(
                $"Launch group '{selectedGroup}' is invalid. {validGroupsDescription}");
        }

        foreach (var resource in model.Resources)
        {
            if (!resource.HasAnnotationOfType<ExcludeFromLaunchGroupSelectionAnnotation>() &&
                !resource.HasAnnotationOfType<ExplicitStartupAnnotation>() &&
                !resource.Annotations.OfType<LaunchGroupAnnotation>()
                    .Any(annotation => StringComparer.OrdinalIgnoreCase.Equals(annotation.LaunchGroupName, selectedGroup)))
            {
                resource.Annotations.Add(new ExplicitStartupAnnotation());
            }
        }

        logger.LogInformation(
            "Launch group '{LaunchGroup}' is selected. Other resources are marked for explicit start unless excluded from selection.",
            selectedGroup);
    }
}
