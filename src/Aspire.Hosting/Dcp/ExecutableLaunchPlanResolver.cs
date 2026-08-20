// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Dcp;

internal sealed class ExecutableLaunchPlanResolver(
    IConfiguration configuration,
    DistributedApplicationOptions distributedApplicationOptions,
    ExecutableLaunchPolicy launchPolicy)
{
    private readonly IConfiguration _configuration = configuration;
    private readonly DistributedApplicationOptions _distributedApplicationOptions = distributedApplicationOptions;
    private readonly ExecutableLaunchPolicy _launchPolicy = launchPolicy;

    public async Task<ExecutableLaunchPlan> ResolveAsync(
        IResource resource,
        IExecutionConfigurationResult executionConfiguration,
        CancellationToken cancellationToken)
    {
        var recipes = resource.Annotations.OfType<ExecutableLaunchRecipeAnnotation>().ToArray();
        if (recipes.Length != 1)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' must have exactly one executable launch recipe, but {recipes.Length} were found.");
        }

        var decision = _launchPolicy.Decide(resource);
        var context = new ExecutableLaunchContext(
            resource,
            _configuration,
            _distributedApplicationOptions,
            executionConfiguration,
            decision,
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
