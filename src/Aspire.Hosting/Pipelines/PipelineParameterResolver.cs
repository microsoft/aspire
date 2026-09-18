// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

internal static class PipelineParameterResolver
{
    internal static async Task<IReadOnlyList<ParameterResource>> GetParameterResourcesAsync(
        DistributedApplicationModel model,
        DistributedApplicationExecutionContext executionContext,
        IReadOnlyCollection<IResource>? scopedResources,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, ParameterResource>(StringComparer.OrdinalIgnoreCase);
        var resources = scopedResources ?? model.Resources.ToArray();

        foreach (var parameter in resources.OfType<ParameterResource>())
        {
            parameters.TryAdd(parameter.Name, parameter);
        }

        foreach (var resource in resources)
        {
            if (resource.IsExcludedFromPublish())
            {
                continue;
            }

            var dependencies = await resource.GetResourceDependenciesAsync(executionContext, ResourceDependencyDiscoveryMode.Recursive, cancellationToken).ConfigureAwait(false);
            foreach (var parameter in dependencies.OfType<ParameterResource>())
            {
                parameters.TryAdd(parameter.Name, parameter);
            }
        }

        return [.. parameters.Values.OrderBy(static parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyCollection<IResource>? GetScopedResourcesForStep(
        string? stepName,
        IReadOnlyList<PipelineStep> resolvedSteps)
    {
        if (string.IsNullOrEmpty(stepName))
        {
            return null;
        }

        if (string.Equals(stepName, WellKnownPipelineSteps.ProcessParameters, StringComparison.Ordinal))
        {
            return null;
        }

        var stepsByName = resolvedSteps.ToDictionary(static step => step.Name, StringComparer.Ordinal);
        if (!stepsByName.TryGetValue(stepName, out var targetStep))
        {
            var availableSteps = string.Join(", ", resolvedSteps.Select(static step => $"'{step.Name}'"));
            throw new InvalidOperationException(
                $"Step '{stepName}' not found in pipeline. Available steps: {availableSteps}");
        }

        return DistributedApplicationPipeline.ComputeTransitiveDependencies(targetStep, stepsByName)
            .Select(static step => step.Resource)
            .Where(static resource => resource is not null)
            .Distinct()
            .ToArray()!;
    }
}
