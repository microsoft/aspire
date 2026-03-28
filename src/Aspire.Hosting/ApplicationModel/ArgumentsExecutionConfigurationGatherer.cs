// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Gathers command line arguments for resources.
/// </summary>
internal class ArgumentsExecutionConfigurationGatherer : IExecutionConfigurationGatherer
{
    /// <inheritdoc/>
    public async ValueTask GatherAsync(IExecutionConfigurationGathererContext context, IResource resource, ILogger resourceLogger, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken = default)
    {
        if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argumentAnnotations))
        {
            IList<object> args = [.. context.Arguments];
            var callbackContext = new CommandLineArgsCallbackContext(args, resource, cancellationToken)
            {
                Logger = resourceLogger,
                ExecutionContext = executionContext
            };

            foreach (var ann in argumentAnnotations)
            {
                // Each annotation operates on a shared context.
                args = await ann.AsCallbackAnnotation().EvaluateOnceAsync(callbackContext).ConfigureAwait(false);
            }

            // Take the final result and apply to the gatherer context.
            context.Arguments.Clear();
            context.Arguments.AddRange(args);
        }
    }
}