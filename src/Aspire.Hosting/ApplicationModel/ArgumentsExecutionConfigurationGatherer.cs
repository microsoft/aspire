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

        await GatherLaunchToolArgumentsAsync(context, resource, resourceLogger, executionContext, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates the resource's launch tool arguments (the tool-invocation prefix, e.g. <c>run ./cmd/api</c>) and
    /// inserts them ahead of every other argument.
    /// </summary>
    /// <remarks>
    /// The callback is deliberately evaluated <em>after</em> the ordinary argument callbacks but its result is
    /// inserted <em>before</em> them. That is what makes the prefix order-independent: no <c>WithArgs</c> callback
    /// can observe it, mutate it, or clear it, so it does not matter whether the tool invocation was declared before
    /// or after the calls that add the program's own arguments.
    /// </remarks>
    private static async ValueTask GatherLaunchToolArgumentsAsync(IExecutionConfigurationGathererContext context, IResource resource, ILogger resourceLogger, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        // Launch tool arguments describe how a local tool (the resource's executable command) invokes the program.
        // A container invokes the program through the image's ENTRYPOINT instead, so the prefix must not be repeated
        // in its arguments. This matters for executables published as a Dockerfile (Go, Python, JavaScript), where
        // PublishAsDockerFile() reuses the executable's annotations for the generated container resource. Note that
        // the container's own `WithArgs(c => c.Args.Clear())` cannot undo this, because the prefix is inserted after
        // all the ordinary argument callbacks have run.
        if (resource is ContainerResource)
        {
            return;
        }

        // Only the last annotation applies, mirroring how the active SupportsDebuggingAnnotation is resolved:
        // a resource can be handed launch tool arguments more than once and the most recent declaration wins.
        if (!resource.TryGetLastAnnotation<LaunchToolArgsCallbackAnnotation>(out var launchToolAnnotation))
        {
            return;
        }

        var launchToolContext = new CommandLineArgsCallbackContext([], resource, cancellationToken)
        {
            Logger = resourceLogger,
            ExecutionContext = executionContext
        };

        var launchToolArgs = await launchToolAnnotation.AsCallbackAnnotation().EvaluateOnceAsync(launchToolContext).ConfigureAwait(false);
        if (launchToolArgs.Count == 0)
        {
            return;
        }

        context.Arguments.InsertRange(0, launchToolArgs);
        context.AddAdditionalData(new LaunchToolArgumentsData(launchToolArgs.Count, launchToolAnnotation.ShowInCommandLine));
    }
}
