// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREPIPELINES003

namespace Aspire.Hosting.Utils;

/// <summary>
/// Resolves standalone image defaults after compute environments finish preparing the model.
/// </summary>
internal static class ContainerImageRegistryResolver
{
    internal static void Resolve(DistributedApplicationModel model)
    {
        var images = model.Resources.OfType<ContainerImageResource>().ToArray();
        foreach (var image in images)
        {
            // Inferred associations must not become explicit lockouts on a retry or retain
            // a registry removed by subsequent environment configuration.
            foreach (var association in image.Annotations.OfType<ContainerImageRegistryAssociationAnnotation>()
                .Where(a => a.DefaultEnvironment is not null).ToArray())
            {
                image.Annotations.Remove(association);
            }
        }

        var unassociatedImages = images
            .Where(image => !image.IsExcludedFromPublish() && !image.HasExplicitRegistryAssociation)
            .ToArray();
        if (unassociatedImages.Length == 0)
        {
            return;
        }

        // Enumerate advertisements, not all registries: a registry facade and its generated
        // registry are not two defaults. Multiple environments can advertise the same resource.
        var candidates = new Dictionary<string, (IResource Registry, IComputeEnvironmentResource Environment)>(StringComparers.ResourceName);
        foreach (var environment in model.Resources.OfType<IComputeEnvironmentResource>()
            .Where(environment => !environment.IsExcludedFromPublish())
            .OrderBy(environment => environment.Name, StringComparers.ResourceName))
        {
            if (!environment.TryGetLastAnnotation<ContainerImageRegistryTargetAnnotation>(out var target))
            {
                continue;
            }

            var defaultRegistry = target.Registry;
            if (defaultRegistry is null)
            {
                continue;
            }
            if (defaultRegistry is not IResource advertisedRegistry)
            {
                throw new DistributedApplicationException(
                    $"Compute environment '{environment.Name}' must advertise an image registry resource present in the application model.");
            }

            var registry = model.Resources.FirstOrDefault(resource => StringComparers.ResourceName.Equals(resource.Name, advertisedRegistry.Name));
            if (registry is not IContainerRegistry || registry.IsExcludedFromPublish())
            {
                throw new DistributedApplicationException(
                    $"Compute environment '{environment.Name}' advertises image registry '{advertisedRegistry.Name}', " +
                    "which is not a publishable container registry resource in the application model.");
            }

            candidates.TryAdd(registry.Name, (registry, environment));
        }

        if (candidates.Count > 1)
        {
            var imageNames = string.Join("', '", unassociatedImages.Select(image => image.Name).Order(StringComparers.ResourceName));
            var registryNames = string.Join("', '", candidates.Keys.Order(StringComparers.ResourceName));
            throw new DistributedApplicationException(
                $"Image artifact(s) '{imageNames}' have multiple default container registries available ('{registryNames}'). " +
                "Associate each image explicitly using 'registry.WithPushedImage(image)' or 'image.WithContainerRegistry(registry)'.");
        }

        if (candidates.Count == 1)
        {
            var (registry, environment) = candidates.Values.Single();
            foreach (var image in unassociatedImages)
            {
                image.Annotations.Add(new ContainerImagePublicationAnnotation(registry, null, image.DefaultTag, environment));
            }
        }
    }
}
