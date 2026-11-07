// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Gathers environment variables for resources.
/// </summary>
internal class EnvironmentVariablesExecutionConfigurationGatherer : IExecutionConfigurationGatherer
{
    /// <inheritdoc/>
    public async ValueTask GatherAsync(IExecutionConfigurationGathererContext context, IResource resource, ILogger resourceLogger, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken = default)
    {
        if (resource.TryGetEnvironmentVariables(out var envVarAnnotations))
        {
            var envVars = new Dictionary<string, object>(context.EnvironmentVariables);
            foreach (var ann in envVarAnnotations)
            {
                // Cached callbacks return an earlier snapshot. Later callbacks must
                // receive that dictionary, not the initial, potentially empty context.
                var callbackContext = new EnvironmentCallbackContext(executionContext, resource, envVars, cancellationToken: cancellationToken)
                {
                    Logger = resourceLogger,
                };
                envVars = await ann.AsCallbackAnnotation().EvaluateOnceAsync(callbackContext).ConfigureAwait(false);
            }

            // Take the final result and apply to the gatherer context.
            foreach (var kvp in envVars)
            {
                context.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }
    }
}