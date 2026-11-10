// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Internal use of launch-group membership annotations.

using System.Globalization;
using Aspire.Dashboard.Model;
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
            if (!int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new DistributedApplicationException(
                    $"{ConfigurationSectionName} must be an array of group names.");
            }

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

        var selection = configuration.GetSection("Aspire:LaunchGroup");
        // Arrays and objects such as "LaunchGroup": ["catalog", "basket"] have children
        // but no scalar value. Reject children even when another provider supplies a scalar.
        if (selection.GetChildren().Any())
        {
            throw new DistributedApplicationException("Aspire:LaunchGroup must be a single group name.");
        }

        var selectedGroup = selection.Value;
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

        ValidateSelectedGroupDependencies(model.Resources, selectedGroup);

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

    private static void ValidateSelectedGroupDependencies(IEnumerable<IResource> resources, string selectedGroup)
    {
        var resourceList = resources.ToArray();
        var automaticStartResources = new HashSet<IResource>(
            resourceList.Where(resource => !WillStartExplicitly(resource, selectedGroup)),
            ReferenceEqualityComparer.Instance);

        foreach (var resource in automaticStartResources)
        {
            if (!IsLocalExecutionResource(resource))
            {
                continue;
            }

            ValidateDependencies(
                resource,
                resource,
                selectedGroup,
                automaticStartResources,
                new HashSet<IResource>(ReferenceEqualityComparer.Instance));
        }
    }

    private static void ValidateDependencies(
        IResource root,
        IResource current,
        string selectedGroup,
        HashSet<IResource> automaticStartResources,
        HashSet<IResource> visited)
    {
        if (!visited.Add(current))
        {
            return;
        }

        var optionalReferences = current.Annotations
            .OfType<OptionalLaunchGroupDependencyAnnotation>()
            .Select(annotation => annotation.Resource)
            .ToHashSet<IResource>(ReferenceEqualityComparer.Instance);
        var dependencies = current.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Where(annotation => annotation.Type == KnownRelationshipTypes.Reference &&
                !optionalReferences.Contains(annotation.Resource))
            .Select(annotation => annotation.Resource)
            .Concat(current.Annotations.OfType<WaitAnnotation>().Select(annotation => annotation.Resource))
            .Concat(current.Annotations.OfType<ConnectionStringRedirectAnnotation>().Select(annotation => annotation.Resource))
            .Distinct<IResource>(ReferenceEqualityComparer.Instance);

        foreach (var dependency in dependencies)
        {
            if (IsLocalExecutionResource(dependency) &&
                !automaticStartResources.Contains(dependency))
            {
                var reason = dependency.HasAnnotationOfType<ExplicitStartupAnnotation>()
                    ? "is marked for explicit start"
                    : $"is not included in launch group '{selectedGroup}'";
                throw new DistributedApplicationException(
                    $"Resource '{root.Name}' requires local resource '{dependency.Name}', which {reason}. " +
                    $"Add the dependency to the selected launch group or mark it optional with " +
                    $"{nameof(LaunchGroupExtensions.WithOptionalLaunchGroupDependency)}.");
            }

            ValidateDependencies(root, dependency, selectedGroup, automaticStartResources, visited);
        }
    }

    private static bool IsLocalExecutionResource(IResource resource) =>
        resource is not IResourceWithoutLifetime and
            not ParameterResource and
            not ConnectionStringResource and
            not ExternalServiceResource;

    private static bool WillStartExplicitly(IResource resource, string selectedGroup) =>
        resource.HasAnnotationOfType<ExplicitStartupAnnotation>() ||
        (!resource.HasAnnotationOfType<ExcludeFromLaunchGroupSelectionAnnotation>() &&
            !resource.Annotations.OfType<LaunchGroupAnnotation>()
                .Any(annotation => StringComparer.OrdinalIgnoreCase.Equals(annotation.LaunchGroupName, selectedGroup)));
}
