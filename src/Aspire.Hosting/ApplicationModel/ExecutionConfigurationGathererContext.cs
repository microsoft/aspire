// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

internal class ExecutionConfigurationGathererContext : IExecutionConfigurationGathererContext
{
    /// <inheritdoc/>
    public List<object> Arguments { get; } = new();

    /// <inheritdoc/>
    public Dictionary<string, object> EnvironmentVariables { get; } = new();

    /// <summary>
    /// Additional configuration data collected during gathering.
    /// </summary>
    internal HashSet<IExecutionConfigurationData> AdditionalConfigurationData { get; } = new();

    /// <inheritdoc/>
    public void AddAdditionalData(IExecutionConfigurationData metadata)
    {
        AdditionalConfigurationData.Add(metadata);
    }

    /// <summary>
    /// Resolves the actual <see cref="IExecutionConfigurationResult"/> from the gatherer context.
    /// </summary>
    /// <param name="resource">The resource for which the configuration is being resolved.</param>
    /// <param name="resourceLogger">The logger associated with the resource.</param>
    /// <param name="executionContext">The execution context of the distributed application.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the resolved resource configuration.
    /// </returns>
    internal async Task<IExecutionConfigurationResult> ResolveAsync(
        IResource resource,
        ILogger resourceLogger,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        HashSet<object> references = new();
        var launchToolArgumentsData = AdditionalConfigurationData.OfType<UnresolvedLaunchToolArgumentsData>().FirstOrDefault();
        var argumentCapacity = Arguments.Count + (launchToolArgumentsData?.Arguments.Length ?? 0);
        List<(object Unprocessed, string Value, bool IsSensitive)> resolvedArguments = new(argumentCapacity);
        Dictionary<string, (object Unprocessed, string Value)> resolvedEnvironmentVariables = new(EnvironmentVariables.Count);
        List<Exception> exceptions = new();
        var resolvedLaunchToolArgumentCount = 0;

        if (launchToolArgumentsData is not null)
        {
            await ResolveArgumentsAsync(launchToolArgumentsData.Arguments, areLaunchToolArguments: true).ConfigureAwait(false);
        }

        await ResolveArgumentsAsync(Arguments, areLaunchToolArguments: false).ConfigureAwait(false);

        foreach (var kvp in EnvironmentVariables)
        {
            try
            {
                var resolvedValue = await resource.ResolveValueAsync(executionContext, resourceLogger, kvp.Value, null, cancellationToken).ConfigureAwait(false);
                if (resolvedValue?.Value is not null)
                {
                    resolvedEnvironmentVariables[kvp.Key] = (kvp.Value, resolvedValue.Value);
                    if (kvp.Value is IValueProvider or IManifestExpressionProvider)
                    {
                        references.Add(kvp.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                resourceLogger.LogError(ex, "Failed to resolve environment variable '{EnvironmentVariable}' for resource '{ResourceName}'. A dependency may have failed to start.", kvp.Key, resource.Name);
                exceptions.Add(ex);
            }
        }

        var resolvedAdditionalConfigurationData = AdditionalConfigurationData;
        if (launchToolArgumentsData is not null)
        {
            resolvedAdditionalConfigurationData = AdditionalConfigurationData
                .Where(data => data is not UnresolvedLaunchToolArgumentsData && data is not LaunchToolArgumentsData)
                .ToHashSet();

            resolvedAdditionalConfigurationData.Add(new LaunchToolArgumentsData(resolvedLaunchToolArgumentCount, launchToolArgumentsData.ShowInCommandLine));
        }

        return new ExecutionConfigurationResult
        {
            References = references,
            ArgumentsWithUnprocessed = resolvedArguments,
            EnvironmentVariablesWithUnprocessed = resolvedEnvironmentVariables,
            AdditionalConfigurationData = resolvedAdditionalConfigurationData,
            Exception = exceptions.Count == 0 ? null : new AggregateException("One or more errors occurred while resolving resource configuration.", exceptions)
        };

        async Task ResolveArgumentsAsync(IEnumerable<object> arguments, bool areLaunchToolArguments)
        {
            foreach (var argument in arguments)
            {
                try
                {
                    var resolvedValue = await resource.ResolveValueAsync(executionContext, resourceLogger, argument, null, cancellationToken).ConfigureAwait(false);
                    if (resolvedValue?.Value is not null)
                    {
                        resolvedArguments.Add((argument, resolvedValue.Value, resolvedValue.IsSensitive));
                        if (areLaunchToolArguments)
                        {
                            // Resolution drops null values. Count only launch tool values that survived so a missing
                            // prefix value cannot make consumers treat the first ordinary argument as part of the prefix.
                            resolvedLaunchToolArgumentCount++;
                        }

                        if (argument is IValueProvider or IManifestExpressionProvider)
                        {
                            references.Add(argument);
                        }
                    }
                }
                catch (Exception ex)
                {
                    resourceLogger.LogError(ex, "Failed to resolve argument for resource '{ResourceName}'. A dependency may have failed to start.", resource.Name);
                    exceptions.Add(ex);
                }
            }
        }
    }
}
