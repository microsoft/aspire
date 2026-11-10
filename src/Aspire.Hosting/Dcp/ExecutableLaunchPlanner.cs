// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Resolves an app-model launch recipe before it is rendered into a DCP execution specification.
/// </summary>
internal static class ExecutableLaunchPlanner
{
    internal static async Task<ExecutableLaunchPlan> ResolveAsync(
        IResource resource,
        IExecutionConfigurationResult executionConfiguration,
        IConfiguration configuration,
        DistributedApplicationOptions distributedApplicationOptions,
        ExecutableLaunchDecision decision,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        var recipes = resource.Annotations.OfType<ExecutableLaunchRecipeAnnotation>().ToArray();
        if (recipes.Length != 1)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' must have exactly one executable launch recipe, but {recipes.Length} were found.");
        }

        var context = new ExecutableLaunchContext(
            resource,
            configuration,
            distributedApplicationOptions,
            executionConfiguration,
            decision,
            resourceLogger,
            cancellationToken);
        var plan = await recipes[0].Recipe.CreateLaunchPlanAsync(context).ConfigureAwait(false);

        if (plan.Mechanism != decision.Mechanism)
        {
            throw new InvalidOperationException(
                $"The executable launch recipe for resource '{resource.Name}' returned a {plan.Mechanism} plan after {decision.Mechanism} was selected.");
        }

        if (plan.Mechanism == ExecutableLaunchMechanism.Ide && plan.LaunchConfigurations.Count == 0)
        {
            throw new InvalidOperationException(
                $"The executable launch recipe for resource '{resource.Name}' selected IDE execution without producing a launch configuration.");
        }

        return plan;
    }
}
