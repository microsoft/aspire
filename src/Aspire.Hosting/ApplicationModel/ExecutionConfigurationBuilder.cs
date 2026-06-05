// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides a builder for constructing an <see cref="IExecutionConfigurationResult"/> for a specific resource in the distributed application model.
/// This resolves command line arguments and environment variables and potentially additional metadata through registered gatherers.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="ExecutionConfigurationBuilder"/> when you need to programmatically assemble configuration for a resource,
/// typically by aggregating multiple configuration sources using the gatherer pattern. This builder collects configuration
/// from registered <see cref="IExecutionConfigurationGatherer"/> instances, which encapsulate logic for gathering resource-specific
/// command line arguments, environment variables, and other metadata.
/// </para>
/// <para>
/// The gatherer pattern allows for modular and extensible configuration assembly, where each gatherer can contribute part of the
/// final configuration and allows for collecting only the relevant configuration supported in a given context (i.e. only applying certificate
/// configuration gatherers in supported environments).
/// </para>
/// <para>
/// Typical usage involves creating a builder for a resource, adding one or more configuration gatherers, and then building the
/// configuration asynchronously.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var resolvedConfiguration = await ExecutionConfigurationBuilder
///     .Create(myResource);
///     .WithArgumentsConfig()
///     .WithEnvironmentVariablesConfig()
///     .BuildAsync(executionContext).ConfigureAwait(false);
/// </code>
/// </example>
public sealed class ExecutionConfigurationBuilder : IExecutionConfigurationBuilder
{
    private readonly IResource _resource;
    private readonly List<IExecutionConfigurationGatherer> _gatherers = new();

    private ExecutionConfigurationBuilder(IResource resource)
    {
        _resource = resource;
    }

    /// <summary>
    /// Creates a new instance of <see cref="IExecutionConfigurationBuilder"/>.
    /// </summary>
    /// <param name="resource">The resource to build the configuration for.</param>
    /// <returns>A new <see cref="IExecutionConfigurationBuilder"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method is useful for building resource execution configurations (command line arguments and environment variables)
    /// in a fluent manner. Individual configuration sources can be added to the builder before finalizing the configuration to
    /// allow only supported configuration sources to be applied in a given execution context (run vs. publish, etc).
    /// </para>
    /// <para>
    /// In particular, this is used to allow certificate-related features to contribute to the final config, but only in execution
    /// contexts where they're supported.
    /// </para>
    /// <example>
    /// <code>
    /// var resolvedConfiguration = await ExecutionConfigurationBuilder
    ///     .Create(myResource)
    ///     .WithArgumentsConfig()
    ///     .WithEnvironmentVariablesConfig()
    ///     .BuildAsync(executionContext)
    ///     .ConfigureAwait(false);
    ///
    /// foreach (var argument in resolvedConfiguration.Arguments)
    /// {
    ///     Console.WriteLine($"Argument: {argument.Value}");
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    public static IExecutionConfigurationBuilder Create(IResource resource)
    {
        return new ExecutionConfigurationBuilder(resource);
    }

    /// <inheritdoc />
    public IExecutionConfigurationBuilder AddExecutionConfigurationGatherer(IExecutionConfigurationGatherer gatherer)
    {
        _gatherers.Add(gatherer);

        return this;
    }

    /// <inheritdoc />
    public async Task<IExecutionConfigurationResult> BuildAsync(DistributedApplicationExecutionContext executionContext, ILogger? resourceLogger = null, CancellationToken cancellationToken = default)
    {
        resourceLogger ??= _resource.GetLogger(executionContext.ServiceProvider);

        var context = new ExecutionConfigurationGathererContext();

        foreach (var gatherer in _gatherers)
        {
            await gatherer.GatherAsync(context, _resource, resourceLogger, executionContext, cancellationToken).ConfigureAwait(false);
        }

        return await context.ResolveAsync(_resource, resourceLogger, executionContext, cancellationToken).ConfigureAwait(false);
    }
}
