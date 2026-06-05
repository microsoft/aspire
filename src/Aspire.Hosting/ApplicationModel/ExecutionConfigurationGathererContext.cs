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

        foreach (var argument in Arguments)
        {
            try
            {
                var resolvedValue = await resource.ResolveValueAsync(executionContext, resourceLogger, argument, null, cancellationToken).ConfigureAwait(false);
                if (resolvedValue?.Value != null)
                {
                    resolvedArguments.Add((argument, resolvedValue.Value, resolvedValue.IsSensitive));
                    if (argument is IValueProvider or IManifestExpressionProvider)
                    {
                        references.Add(argument);
                    }
                }
            }
            catch (Exception ex)
            {
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
                exceptions.Add(ex);
            }
        }

        return new ExecutionConfigurationResult
        {
            References = references,
            ArgumentsWithUnprocessed = resolvedArguments,
            EnvironmentVariablesWithUnprocessed = resolvedEnvironmentVariables,
            AdditionalConfigurationData = AdditionalConfigurationData,
            Exception = exceptions.Count == 0 ? null : new AggregateException("One or more errors occurred while resolving resource configuration.", exceptions)
        };
    }
}
