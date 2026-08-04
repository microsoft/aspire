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
        List<(object Unprocessed, string Value, bool IsSensitive)> resolvedArguments = new(Arguments.Count);
        Dictionary<string, (object Unprocessed, string Value)> resolvedEnvironmentVariables = new(EnvironmentVariables.Count);
        List<Exception> exceptions = new();
        var entrypointArgumentsData = AdditionalConfigurationData.OfType<EntrypointArgumentsData>().FirstOrDefault();
        var resolvedEntrypointArgumentCount = 0;

        for (var argumentIndex = 0; argumentIndex < Arguments.Count; argumentIndex++)
        {
            var argument = Arguments[argumentIndex];

            try
            {
                var resolvedValue = await resource.ResolveValueAsync(executionContext, resourceLogger, argument, null, cancellationToken).ConfigureAwait(false);
                if (resolvedValue?.Value != null)
                {
                    resolvedArguments.Add((argument, resolvedValue.Value, resolvedValue.IsSensitive));
                    if (entrypointArgumentsData is not null && argumentIndex < entrypointArgumentsData.Count)
                    {
                        // Resolution drops null values. Count only entrypoint values that survived so a missing prefix
                        // value cannot make consumers treat the first ordinary argument as part of the prefix.
                        resolvedEntrypointArgumentCount++;
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

        foreach (var kvp in EnvironmentVariables)
        {
            try
            {
                var resolvedValue = await resource.ResolveValueAsync(executionContext, resourceLogger, kvp.Value, null, cancellationToken).ConfigureAwait(false);
                if (resolvedValue?.Value != null)
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
        if (entrypointArgumentsData is not null && entrypointArgumentsData.Count != resolvedEntrypointArgumentCount)
        {
            resolvedAdditionalConfigurationData = AdditionalConfigurationData
                .Where(data => data is not EntrypointArgumentsData)
                .ToHashSet();
            resolvedAdditionalConfigurationData.Add(new EntrypointArgumentsData(resolvedEntrypointArgumentCount));
        }

        return new ExecutionConfigurationResult
        {
            References = references,
            ArgumentsWithUnprocessed = resolvedArguments,
            EnvironmentVariablesWithUnprocessed = resolvedEnvironmentVariables,
            AdditionalConfigurationData = resolvedAdditionalConfigurationData,
            Exception = exceptions.Count == 0 ? null : new AggregateException("One or more errors occurred while resolving resource configuration.", exceptions)
        };
    }
}
