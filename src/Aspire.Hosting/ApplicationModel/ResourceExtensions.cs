// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIRECOMPUTE003

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for the <see cref="IResource"/> interface.
/// </summary>
public static class ResourceExtensions
{
    /// <summary>
    /// Attempts to get the last annotation of the specified type from the resource.
    /// </summary>
    /// <typeparam name="T">The type of the annotation to get.</typeparam>
    /// <param name="resource">The resource to get the annotation from.</param>
    /// <param name="annotation">When this method returns, contains the last annotation of the specified type from the resource, if found; otherwise, the default value for <typeparamref name="T"/>.</param>
    /// <returns><see langword="true"/> if the last annotation of the specified type was found in the resource; otherwise, <see langword="false"/>.</returns>
    public static bool TryGetLastAnnotation<T>(this IResource resource, [NotNullWhen(true)] out T? annotation) where T : IResourceAnnotation
    {
        if (resource.Annotations.OfType<T>().LastOrDefault() is { } lastAnnotation)
        {
            annotation = lastAnnotation;
            return true;
        }
        else
        {
            annotation = default;
            return false;
        }
    }

    /// <summary>
    /// Attempts to retrieve all annotations of the specified type from the given resource.
    /// </summary>
    /// <typeparam name="T">The type of annotation to retrieve.</typeparam>
    /// <param name="resource">The resource to retrieve annotations from.</param>
    /// <param name="result">When this method returns, contains the annotations of the specified type, if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if annotations of the specified type were found; otherwise, <see langword="false"/>.</returns>
    public static bool TryGetAnnotationsOfType<T>(this IResource resource, [NotNullWhen(true)] out IEnumerable<T>? result) where T : IResourceAnnotation
    {
        var matchingTypeAnnotations = resource.Annotations.OfType<T>();

        if (matchingTypeAnnotations.Any())
        {
            result = matchingTypeAnnotations.ToArray();
            return true;
        }
        else
        {
            result = null;
            return false;
        }
    }

    /// <summary>
    /// Gets whether <paramref name="resource"/> has an annotation of type <typeparamref name="T"/>
    /// </summary>
    /// <typeparam name="T">The type of annotation to retrieve.</typeparam>
    /// <param name="resource">The resource to retrieve annotations from.</param>
    /// <returns><see langword="true"/> if an annotation of the specified type was found; otherwise, <see langword="false"/>.</returns>
    public static bool HasAnnotationOfType<T>(this IResource resource) where T : IResourceAnnotation
    {
        return resource.Annotations.Any(a => a is T);
    }

    /// <summary>
    /// Attempts to retrieve all annotations of the specified type from the given resource including from parents.
    /// </summary>
    /// <typeparam name="T">The type of annotation to retrieve.</typeparam>
    /// <param name="resource">The resource to retrieve annotations from.</param>
    /// <param name="result">When this method returns, contains the annotations of the specified type, if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if annotations of the specified type were found; otherwise, <see langword="false"/>.</returns>
    public static bool TryGetAnnotationsIncludingAncestorsOfType<T>(this IResource resource, [NotNullWhen(true)] out IEnumerable<T>? result) where T : IResourceAnnotation
    {
        if (resource is IResourceWithParent)
        {
            List<T>? annotations = null;

            while (true)
            {
                foreach (var annotation in resource.Annotations.OfType<T>())
                {
                    annotations ??= [];
                    annotations.Add(annotation);
                }

                if (resource is IResourceWithParent child)
                {
                    resource = child.Parent;
                }
                else
                {
                    break;
                }
            }

            result = annotations;
            return annotations is not null;
        }

        return TryGetAnnotationsOfType(resource, out result);
    }

    /// <summary>
    /// Gets whether <paramref name="resource"/> or its ancestors have an annotation of type <typeparamref name="T"/>
    /// </summary>
    /// <typeparam name="T">The type of annotation to retrieve.</typeparam>
    /// <param name="resource">The resource to retrieve annotations from.</param>
    /// <returns><see langword="true"/> if an annotation of the specified type was found; otherwise, <see langword="false"/>.</returns>
    public static bool HasAnnotationIncludingAncestorsOfType<T>(this IResource resource) where T : IResourceAnnotation
    {
        if (resource is IResourceWithParent)
        {
            while (true)
            {
                if (HasAnnotationOfType<T>(resource))
                {
                    return true;
                }

                if (resource is IResourceWithParent child)
                {
                    resource = child.Parent;
                }
                else
                {
                    break;
                }
            }

            return false;
        }

        return HasAnnotationOfType<T>(resource);
    }

    /// <summary>
    /// Attempts to get the environment variables from the given resource.
    /// </summary>
    /// <param name="resource">The resource to get the environment variables from.</param>
    /// <param name="environmentVariables">The environment variables retrieved from the resource, if any.</param>
    /// <returns>True if the environment variables were successfully retrieved, false otherwise.</returns>
    public static bool TryGetEnvironmentVariables(this IResource resource, [NotNullWhen(true)] out IEnumerable<EnvironmentCallbackAnnotation>? environmentVariables)
    {
        return TryGetAnnotationsOfType(resource, out environmentVariables);
    }

    /// <summary>
    /// Get the environment variables from the given resource.
    /// </summary>
    /// <param name="resource">The resource to get the environment variables from.</param>
    /// <param name="applicationOperation">The context in which the AppHost is being executed.</param>
    /// <returns>The environment variables retrieved from the resource.</returns>
    /// <remarks>
    /// This method is useful when you want to make sure the environment variables are added properly to resources, mostly in test situations.
    /// This method has asynchronous behavior when <paramref name = "applicationOperation" /> is <see cref="DistributedApplicationOperation.Run"/>
    /// and environment variables were provided from <see cref="IValueProvider"/> otherwise it will be synchronous.
    /// <example>
    /// Using <see cref="GetEnvironmentVariableValuesAsync(IResourceWithEnvironment, DistributedApplicationOperation)"/> inside
    /// a unit test to validate environment variable values.
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder();
    /// var container = builder.AddContainer("elasticsearch", "library/elasticsearch", "8.14.0")
    ///  .WithEnvironment("discovery.type", "single-node")
    ///  .WithEnvironment("xpack.security.enabled", "true");
    ///
    /// var env = await container.Resource.GetEnvironmentVariableValuesAsync();
    ///
    /// Assert.Collection(env,
    ///     env =>
    ///         {
    ///             Assert.Equal("discovery.type", env.Key);
    ///             Assert.Equal("single-node", env.Value);
    ///         },
    ///         env =>
    ///         {
    ///             Assert.Equal("xpack.security.enabled", env.Key);
    ///             Assert.Equal("true", env.Value);
    ///         });
    /// </code>
    /// </example>
    /// </remarks>
    [Obsolete($"Use {nameof(ExecutionConfigurationBuilder)} instead.")]
    public static async ValueTask<Dictionary<string, string>> GetEnvironmentVariableValuesAsync(this IResourceWithEnvironment resource,
            DistributedApplicationOperation applicationOperation = DistributedApplicationOperation.Run)
    {
        var executionConfiguration = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new(applicationOperation), NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        return executionConfiguration.EnvironmentVariables.ToDictionary();
    }

    /// <summary>
    /// Get the arguments from the given resource.
    /// </summary>
    /// <param name="resource">The resource to get the arguments from.</param>
    /// <param name="applicationOperation">The context in which the AppHost is being executed.</param>
    /// <returns>The arguments retrieved from the resource.</returns>
    /// <remarks>
    /// This method is useful when you want to make sure the arguments are added properly to resources, mostly in test situations.
    /// This method has asynchronous behavior when <paramref name = "applicationOperation" /> is <see cref="DistributedApplicationOperation.Run"/>
    /// and arguments were provided from <see cref="IValueProvider"/> otherwise it will be synchronous.
    /// <example>
    /// Using <see cref="GetArgumentValuesAsync(IResourceWithArgs, DistributedApplicationOperation)"/> inside
    /// a unit test to validate argument values.
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder();
    /// var container = builder.AddContainer("elasticsearch", "library/elasticsearch", "8.14.0")
    ///  .WithArgs("--discovery.type", "single-node")
    ///  .WithArgs("--xpack.security.enabled", "true");
    ///
    /// var args = await container.Resource.GetArgumentsAsync();
    ///
    /// Assert.Collection(args,
    ///     arg =>
    ///         {
    ///             Assert.Equal("--discovery.type", arg);
    ///         },
    ///         arg =>
    ///         {
    ///             Assert.Equal("--xpack.security.enabled", arg);
    ///         });
    /// </code>
    /// </example>
    /// </remarks>
    [Obsolete($"Use {nameof(ExecutionConfigurationBuilder)} instead.")]
    public static async ValueTask<string[]> GetArgumentValuesAsync(this IResourceWithArgs resource,
        DistributedApplicationOperation applicationOperation = DistributedApplicationOperation.Run)
    {
        var argumentConfiguration = await ExecutionConfigurationBuilder.Create(resource)
            .WithArgumentsConfig()
            .BuildAsync(new(applicationOperation), NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        return argumentConfiguration.Arguments.Select(a => a.Value).ToArray();
    }

    /// <summary>
    /// Gather argument values, but do not resolve them. Used to allow multiple callbacks to constructively contribute to
    /// the argument list before resolving.
    /// </summary>
    /// <param name="resource">The resource to retrieve argument values for.</param>
    /// <param name="executionContext">The execution context used during the retrieval of argument values.</param>
    /// <param name="logger">The logger used for logging information or errors during the retrieval of argument values.</param>
    /// <param name="cancellationToken">A token for cancelling the operation, if needed.</param>
    /// <returns>A list of unprocessed argument values.</returns>
    [Obsolete("Use ExecutionConfigurationBuilder instead.")]
    internal static async ValueTask<List<object>> GatherArgumentValuesAsync(
        this IResource resource,
        DistributedApplicationExecutionContext executionContext,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var args = new List<object>();
        if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var callbacks))
        {
            var context = new CommandLineArgsCallbackContext(args, resource, cancellationToken)
            {
                Logger = logger,
                ExecutionContext = executionContext
            };

            foreach (var callback in callbacks)
            {
                await callback.Callback(context).ConfigureAwait(false);
            }
        }

        return args;
    }

    /// <summary>
    /// Processes pre-gathered command-line argument values for the specified resource in the given execution context.
    /// </summary>
    /// <param name="resource">The resource for which the argument values are being processed.</param>
    /// <param name="executionContext">The execution context used during the processing of argument values.</param>
    /// <param name="arguments">The list of pre-gathered argument values to process.</param>
    /// <param name="processValue">A callback invoked for each argument value, providing the unprocessed value, processed string representation, any exception, and a sensitivity flag.</param>
    /// <param name="logger">The logger used for logging information or errors during the argument processing.</param>
    /// <param name="cancellationToken">A token for cancelling the operation, if needed.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [Obsolete("Use ExecutionConfigurationBuilder instead.")]
    internal static async ValueTask ProcessGatheredArgumentValuesAsync(
        this IResource resource,
        DistributedApplicationExecutionContext executionContext,
        List<object> arguments,
        // (unprocessed, processed, exception, isSensitive)
        Action<object?, string?, Exception?, bool> processValue,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        foreach (var a in arguments)
        {
            try
            {
                var resolvedValue = await resource.ResolveValueAsync(executionContext, logger, a, null, cancellationToken).ConfigureAwait(false);

                if (resolvedValue?.Value != null)
                {
                    processValue(a, resolvedValue.Value, null, resolvedValue.IsSensitive);
                }
            }
            catch (Exception ex)
            {
                processValue(a, a.ToString(), ex, false);
            }
        }
    }

    /// <summary>
    /// Processes argument values for the specified resource in the given execution context.
    /// </summary>
    /// <param name="resource">The resource containing the argument values to process.</param>
    /// <param name="executionContext">The execution context used during the processing of argument values.</param>
    /// <param name="processValue">
    /// A callback invoked for each argument value. This action provides the unprocessed value, processed string representation,
    /// an exception if one occurs, and a boolean indicating the success of processing.
    /// </param>
    /// <param name="logger">The logger used for logging information or errors during the argument processing.</param>
    /// <param name="cancellationToken">A token for cancelling the operation, if needed.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [Obsolete("Use ExecutionConfigurationBuilder instead.")]
    public static async ValueTask ProcessArgumentValuesAsync(
        this IResource resource,
        DistributedApplicationExecutionContext executionContext,
        // (unprocessed, processed, exception, isSensitive)
        Action<object?, string?, Exception?, bool> processValue,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var args = await GatherArgumentValuesAsync(resource, executionContext, logger, cancellationToken).ConfigureAwait(false);

        await ProcessGatheredArgumentValuesAsync(resource, executionContext, args, processValue, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gather environment variable values, but do not resolve them. Used to allow multiple callbacks to
    /// contribute to the environment variable list before resolving.
    /// </summary>
    /// <param name="resource">The resource containing the environment variables to gather.</param>
    /// <param name="executionContext">The execution context used during the gathering of environment variables.</param>
    /// <param name="logger">The logger used for logging information or errors during the gathering process.</param>
    /// <param name="cancellationToken">A token for cancelling the operation, if needed.</param>
    /// <returns>A dictionary of unprocessed environment variable values.</returns>
    [Obsolete("Use ExecutionConfigurationBuilder instead.")]
    internal static async ValueTask<Dictionary<string, object>> GatherEnvironmentVariableValuesAsync(
        this IResource resource,
        DistributedApplicationExecutionContext executionContext,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var config = new Dictionary<string, object>();
        if (resource.TryGetEnvironmentVariables(out var callbacks))
        {
            var context = new EnvironmentCallbackContext(executionContext, resource, config, cancellationToken)
            {
                Logger = logger
            };

            foreach (var callback in callbacks)
            {
                await callback.Callback(context).ConfigureAwait(false);
            }
        }

        return config;
    }

    /// <summary>
    /// Processes pre-gathered environment variable values for the specified resource within the given execution context.
    /// </summary>
    /// <param name="resource">The resource for which the environment variables are being processed.</param>
    /// <param name="executionContext">The execution context used during the processing of environment variables.</param>
    /// <param name="environmentVariables">The pre-gathered environment variable values to be processed.</param>
    /// <param name="processValue">An action delegate invoked for each environment variable, providing the key, the unprocessed value, the processed value (if available), and any exception encountered during processing.</param>
    /// <param name="logger">The logger used to log any information or errors during the environment variables processing.</param>
    /// <param name="cancellationToken">A cancellation token to observe during the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Obsolete("Use ExecutionConfigurationBuilder instead.")]
    internal static async ValueTask ProcessGatheredEnvironmentVariableValuesAsync(
        this IResource resource,
        DistributedApplicationExecutionContext executionContext,
        Dictionary<string, object> environmentVariables,
        Action<string, object?, string?, Exception?> processValue,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        foreach (var (key, expr) in environmentVariables)
        {
            try
            {
                var resolvedValue = await resource.ResolveValueAsync(executionContext, logger, expr, key, cancellationToken).ConfigureAwait(false);

                if (resolvedValue?.Value is not null)
                {
                    processValue(key, expr, resolvedValue.Value, null);
                }
            }
            catch (Exception ex)
            {
                processValue(key, expr, expr?.ToString(), ex);
            }
        }
    }

    /// <summary>
    /// Processes environment variable values for the specified resource within the given execution context.
    /// </summary>
    /// <param name="resource">The resource from which the environment variables are retrieved and processed.</param>
    /// <param name="executionContext">The execution context to be used for processing the environment variables.</param>
    /// <param name="processValue">An action delegate invoked for each environment variable, providing the key, the unprocessed value, the processed value (if available), and any exception encountered during processing.</param>
    /// <param name="logger">The logger used to log any information or errors during the environment variables processing.</param>
    /// <param name="cancellationToken">A cancellation token to observe during the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    [Obsolete("Use ExecutionConfigurationBuilder instead.")]
    public static async ValueTask ProcessEnvironmentVariableValuesAsync(
        this IResource resource,
        DistributedApplicationExecutionContext executionContext,
        Action<string, object?, string?, Exception?> processValue,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var config = await GatherEnvironmentVariableValuesAsync(resource, executionContext, logger, cancellationToken).ConfigureAwait(false);

        await ProcessGatheredEnvironmentVariableValuesAsync(resource, executionContext, config, processValue, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Processes all container build options callback annotations on a resource by invoking them in order.
    /// </summary>
    /// <param name="resource">The resource to process container build options for.</param>
    /// <param name="serviceProvider">The service provider for dependency injection.</param>
    /// <param name="logger">The logger used to log any information or errors during processing.</param>
    /// <param name="executionContext">The optional execution context.</param>
    /// <param name="cancellationToken">A cancellation token to observe during the asynchronous operation.</param>
    /// <returns>A context object containing the accumulated container build options from all callbacks.</returns>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static async ValueTask<ContainerBuildOptionsCallbackContext> ProcessContainerBuildOptionsCallbackAsync(
        this IResource resource,
        IServiceProvider serviceProvider,
        ILogger logger,
        DistributedApplicationExecutionContext? executionContext = null,
        CancellationToken cancellationToken = default)
    {
        var context = new ContainerBuildOptionsCallbackContext(
            resource,
            serviceProvider,
            logger,
            cancellationToken,
            executionContext);

        if (resource.TryGetAnnotationsOfType<ContainerBuildOptionsCallbackAnnotation>(out var annotations))
        {
            foreach (var annotation in annotations)
            {
                await annotation.Callback(context).ConfigureAwait(false);
            }
        }

        return context;
    }

    /// <summary>
    /// Configures container build options for a compute resource using a callback.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to configure container build options.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithContainerBuildOptions<T>(
        this IResourceBuilder<T> builder,
        Action<ContainerBuildOptionsCallbackContext> callback)
        where T : IResource, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new ContainerBuildOptionsCallbackAnnotation(callback), ResourceAnnotationMutationBehavior.Append);
    }

    /// <summary>
    /// Configures container build options for a compute resource using an async callback.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">An async callback to configure container build options.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithContainerBuildOptions<T>(
        this IResourceBuilder<T> builder,
        Func<ContainerBuildOptionsCallbackContext, Task> callback)
        where T : IResource, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new ContainerBuildOptionsCallbackAnnotation(callback), ResourceAnnotationMutationBehavior.Append);
    }

    internal static NetworkIdentifier GetDefaultResourceNetwork(this IResource resource)
    {
        return resource.IsContainer() ? KnownNetworkIdentifiers.DefaultAspireContainerNetwork : KnownNetworkIdentifiers.LocalhostNetwork;
    }

    internal static IEnumerable<NetworkIdentifier> GetSupportedNetworks(this IResource resource)
    {
        return resource.IsContainer() ? [KnownNetworkIdentifiers.DefaultAspireContainerNetwork, KnownNetworkIdentifiers.LocalhostNetwork] : [KnownNetworkIdentifiers.LocalhostNetwork];
    }

    internal static async ValueTask<ResolvedValue?> ResolveValueAsync(
        this IResource resource,
        DistributedApplicationExecutionContext executionContext,
        ILogger logger,
        object? value,
        string? key = null,
        CancellationToken cancellationToken = default)
    {
        return (executionContext.Operation, value) switch
        {
            (_, string s) => new(s, false),
            (DistributedApplicationOperation.Run, IValueProvider provider) => await resource.GetValue(executionContext, key, provider, logger, cancellationToken).ConfigureAwait(false),
            (DistributedApplicationOperation.Run, IResourceBuilder<IResource> rb) when rb.Resource is IValueProvider provider => await resource.GetValue(executionContext, key, provider, logger, cancellationToken).ConfigureAwait(false),
            (DistributedApplicationOperation.Publish, IManifestExpressionProvider provider) => new(provider.ValueExpression, false),
            (DistributedApplicationOperation.Publish, IResourceBuilder<IResource> rb) when rb.Resource is IManifestExpressionProvider provider => new(provider.ValueExpression, false),
            (_, { } o) => new(o.ToString(), false),
            (_, null) => new(null, false),
        };
    }

    /// <summary>
    /// Gets a value indicating whether the resource is excluded from being published.
    /// </summary>
    /// <param name="resource">The resource to determine if it should be excluded from being published.</param>
    public static bool IsExcludedFromPublish(this IResource resource) =>
        resource.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var lastAnnotation) && lastAnnotation == ManifestPublishingCallbackAnnotation.Ignore;

    internal static async ValueTask ProcessContainerRuntimeArgValues(
        this IResource resource,
        DistributedApplicationExecutionContext executionContext,
        Action<string?, Exception?> processValue,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Apply optional extra arguments to the container run command.
        if (resource.TryGetAnnotationsOfType<ContainerRuntimeArgsCallbackAnnotation>(out var runArgsCallback))
        {
            var args = new List<object>();

            var containerRunArgsContext = new ContainerRuntimeArgsCallbackContext(args, cancellationToken);

            foreach (var callback in runArgsCallback)
            {
                await callback.Callback(containerRunArgsContext).ConfigureAwait(false);
            }

            foreach (var arg in args)
            {
                try
                {
                    var value = arg switch
                    {
                        string s => s,
                        IValueProvider valueProvider => (await resource.GetValue(executionContext, key: null, valueProvider, logger, cancellationToken).ConfigureAwait(false))?.Value,
                        { } obj => obj.ToString(),
                        null => null
                    };

                    if (value is not null)
                    {
                        processValue(value, null);
                    }
                }
                catch (Exception ex)
                {
                    processValue(arg.ToString(), ex);
                }
            }
        }
    }

    private static async Task<ResolvedValue?> GetValue(this IResource resource, DistributedApplicationExecutionContext executionContext, string? key, IValueProvider valueProvider, ILogger logger, CancellationToken cancellationToken)
    {
        var task = ExpressionResolver.ResolveAsync(valueProvider, new ValueProviderContext() { ExecutionContext = executionContext, Caller = resource }, cancellationToken);

        if (!task.IsCompleted)
        {
            if (valueProvider is IResource providerResource)
            {
                if (key is null)
                {
                    logger.LogInformation("Waiting for value from resource '{ResourceName}'", providerResource.Name);
                }
                else
                {
                    logger.LogInformation("Waiting for value for environment variable value '{Name}' from resource '{ResourceName}'", key, providerResource.Name);
                }
            }
            else if (valueProvider is ConnectionStringReference { Resource: var cs })
            {
                logger.LogInformation("Waiting for value for connection string from resource '{ResourceName}'", cs.Name);
            }
            else
            {
                if (key is null)
                {
                    logger.LogInformation("Waiting for value from {ValueProvider}.", valueProvider.ToString());
                }
                else
                {
                    logger.LogInformation("Waiting for value for environment variable value '{Name}' from {ValueProvider}.", key, valueProvider.ToString());
                }
            }
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to get the container mounts for the specified resource.
    /// </summary>
    /// <param name="resource">The resource to get the volume mounts for.</param>
    /// <param name="volumeMounts">When this method returns, contains the volume mounts for the specified resource, if found; otherwise, <c>null</c>.</param>
    /// <returns><c>true</c> if the volume mounts were successfully retrieved; otherwise, <c>false</c>.</returns>
    public static bool TryGetContainerMounts(this IResource resource, [NotNullWhen(true)] out IEnumerable<ContainerMountAnnotation>? volumeMounts)
    {
        return TryGetAnnotationsOfType<ContainerMountAnnotation>(resource, out volumeMounts);
    }

    /// <summary>
    /// Attempts to retrieve the endpoints for the given resource.
    /// </summary>
    /// <param name="resource">The resource to retrieve the endpoints for.</param>
    /// <param name="endpoints">The endpoints for the given resource, if found.</param>
    /// <returns>True if the endpoints were found, false otherwise.</returns>
    public static bool TryGetEndpoints(this IResource resource, [NotNullWhen(true)] out IEnumerable<EndpointAnnotation>? endpoints)
    {
        return TryGetAnnotationsOfType(resource, out endpoints);
    }

    /// <summary>
    /// Attempts to retrieve the URLs for the given resource.
    /// </summary>
    /// <param name="resource">The resource to retrieve the URLs for.</param>
    /// <param name="urls">The URLs for the given resource, if found.</param>
    /// <returns>True if the URLs were found, false otherwise.</returns>
    public static bool TryGetUrls(this IResource resource, [NotNullWhen(true)] out IEnumerable<ResourceUrlAnnotation>? urls)
    {
        return TryGetAnnotationsOfType(resource, out urls);
    }

    /// <summary>
    /// Gets references to all endpoints for the specified resource.
    /// </summary>
    /// <param name="resource">The <see cref="IResourceWithEndpoints"/> which contains <see cref="EndpointAnnotation"/> annotations.</param>
    /// <returns>An enumeration of <see cref="EndpointReference"/> based on the <see cref="EndpointAnnotation"/> annotations from the resources' <see cref="IResource.Annotations"/> collection.</returns>
    public static IEnumerable<EndpointReference> GetEndpoints(this IResourceWithEndpoints resource)
    {
        if (TryGetAnnotationsOfType<EndpointAnnotation>(resource, out var endpoints))
        {
            return endpoints.Select(e => new EndpointReference(resource, e));
        }

        return [];
    }

    /// <summary>
    /// Gets references to all endpoints for the specified resource.
    /// </summary>
    /// <param name="resource">The <see cref="IResourceWithEndpoints"/> which contains <see cref="EndpointAnnotation"/> annotations.</param>
    /// <param name="contextNetworkID">The ID of the network that serves as the context context for the endpoint references.</param>
    /// <returns>An enumeration of <see cref="EndpointReference"/> based on the <see cref="EndpointAnnotation"/> annotations from the resources' <see cref="IResource.Annotations"/> collection.</returns>
    public static IEnumerable<EndpointReference> GetEndpoints(this IResourceWithEndpoints resource, NetworkIdentifier contextNetworkID)
    {
        if (TryGetAnnotationsOfType<EndpointAnnotation>(resource, out var endpoints))
        {
            return endpoints.Select(e => new EndpointReference(resource, e, contextNetworkID));
        }

        return [];
    }

    /// <summary>
    /// Gets an endpoint reference for the specified endpoint name.
    /// </summary>
    /// <param name="resource">The <see cref="IResourceWithEndpoints"/> which contains <see cref="EndpointAnnotation"/> annotations.</param>
    /// <param name="endpointName">The name of the endpoint.</param>
    /// <returns>An <see cref="EndpointReference"/>object providing resolvable reference for the specified endpoint.</returns>
    public static EndpointReference GetEndpoint(this IResourceWithEndpoints resource, string endpointName)
    {
        var endpoint = resource.TryGetEndpoints(out var endpoints) ?
            endpoints.FirstOrDefault(e => StringComparers.EndpointAnnotationName.Equals(e.Name, endpointName)) :
            null;
        if (endpoint is null)
        {
            return new EndpointReference(resource, endpointName);
        }
        else
        {
            return new EndpointReference(resource, endpoint);
        }
    }

    /// <summary>
    /// Gets an endpoint reference for the specified endpoint name.
    /// </summary>
    /// <param name="resource">The <see cref="IResourceWithEndpoints"/> which contains <see cref="EndpointAnnotation"/> annotations.</param>
    /// <param name="endpointName">The name of the endpoint.</param>
    /// <param name="contextNetworkID">The network ID of the network that provides the context for the returned <see cref="EndpointReference"/></param>
    /// <returns>An <see cref="EndpointReference"/>object providing resolvable reference for the specified endpoint.</returns>
    public static EndpointReference GetEndpoint(this IResourceWithEndpoints resource, string endpointName, NetworkIdentifier contextNetworkID)
    {

        var endpoint = resource.TryGetEndpoints(out var endpoints) ?
            endpoints.FirstOrDefault(e => StringComparers.EndpointAnnotationName.Equals(e.Name, endpointName)) :
            null;
        if (endpoint is null)
        {
            return new EndpointReference(resource, endpointName, contextNetworkID);
        }
        else
        {
            return new EndpointReference(resource, endpoint, contextNetworkID);
        }
    }

    /// <summary>
    /// Resolves endpoint port configuration for the specified resource.
    /// Computes target ports and exposed ports based on resource type, endpoint configuration,
    /// and whether the endpoint is considered a default HTTP endpoint.
    /// </summary>
    /// <param name="resource">The resource containing endpoints to resolve.</param>
    /// <param name="portAllocator">Optional port allocator. If null, uses default allocation starting from port 8000.</param>
    /// <returns>A read-only list of resolved endpoints with computed port values.</returns>
    public static IReadOnlyList<ResolvedEndpoint> ResolveEndpoints(this IResource resource, IPortAllocator? portAllocator = null)
    {
        if (!resource.TryGetEndpoints(out var endpoints))
        {
            return [];
        }

        portAllocator ??= new PortAllocator();
        var httpSchemesEncountered = new HashSet<string>();
        var result = new List<ResolvedEndpoint>();

        foreach (var endpoint in endpoints)
        {
            // Compute target port based on resource type and endpoint configuration
            ResolvedPort targetPort = (resource, endpoint.UriScheme, endpoint.TargetPort, endpoint.Port) switch
            {
                // The port was explicitly specified so use it
                (_, _, int target, _) => ResolvedPort.Explicit(target),

                // Container resources get their default listening port from the exposed port (implicit)
                (ContainerResource, _, null, int port) => ResolvedPort.Implicit(port),

                // Check whether the project views this endpoint as Default (for its scheme).
                // If so, we don't specify the target port, as it will get one from the deployment tool.
                (ProjectResource, string uriScheme, null, _) when IsHttpScheme(uriScheme) && !httpSchemesEncountered.Contains(uriScheme) => ResolvedPort.None(),

                // Allocate a dynamic port
                _ => ResolvedPort.Allocated(portAllocator.AllocatePort())
            };

            // Track HTTP schemes encountered for ProjectResources
            if (resource is ProjectResource && IsHttpScheme(endpoint.UriScheme))
            {
                httpSchemesEncountered.Add(endpoint.UriScheme);
            }

            // Compute exposed port (host port)
            ResolvedPort exposedPort = (endpoint.UriScheme, endpoint.Port, targetPort.Value) switch
            {
                // Port set explicitly, use it
                (_, int port, _) => ResolvedPort.Explicit(port),

                // We have a target port, infer the exposedPort from it
                (_, null, int targetPortValue) => ResolvedPort.Implicit(targetPortValue),

                // Let the tool infer the default http and https ports
                ("http", null, null) => ResolvedPort.None(),
                ("https", null, null) => ResolvedPort.None(),

                // Other schemes just allocate a port
                _ => ResolvedPort.Allocated(portAllocator.AllocatePort())
            };

            // Track used ports to avoid collisions when allocating
            if (exposedPort.Value is int ep)
            {
                portAllocator.AddUsedPort(ep);
            }

            if (targetPort.Value is int tp)
            {
                portAllocator.AddUsedPort(tp);
            }

            result.Add(new ResolvedEndpoint
            {
                Endpoint = endpoint,
                TargetPort = targetPort,
                ExposedPort = exposedPort
            });
        }

        return result;

        static bool IsHttpScheme(string scheme) => scheme is "http" or "https";
    }

    /// <summary>
    /// Attempts to get the container image name from the given resource.
    /// </summary>
    /// <param name="resource">The resource to get the container image name from.</param>
    /// <param name="imageName">The container image name if found, otherwise null.</param>
    /// <returns>True if the container image name was found, otherwise false.</returns>
    public static bool TryGetContainerImageName(this IResource resource, [NotNullWhen(true)] out string? imageName)
    {
        return TryGetContainerImageName(resource, useBuiltImage: true, out imageName);
    }

    /// <summary>
    /// Attempts to get the container image name from the given resource.
    /// </summary>
    /// <param name="resource">The resource to get the container image name from.</param>
    /// <param name="useBuiltImage">When true, uses the image name from DockerfileBuildAnnotation if present. When false, uses only ContainerImageAnnotation.</param>
    /// <param name="imageName">The container image name if found, otherwise null.</param>
    /// <returns>True if the container image name was found, otherwise false.</returns>
    public static bool TryGetContainerImageName(this IResource resource, bool useBuiltImage, [NotNullWhen(true)] out string? imageName)
    {
        // First check if there's a DockerfileBuildAnnotation with an image name/tag
        // This takes precedence over the ContainerImageAnnotation when building from a Dockerfile
        if (useBuiltImage &&
            resource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault() is { } buildAnnotation &&
            !string.IsNullOrEmpty(buildAnnotation.ImageName))
        {
            var tagSuffix = string.IsNullOrEmpty(buildAnnotation.ImageTag) ? string.Empty : $":{buildAnnotation.ImageTag}";
            imageName = $"{buildAnnotation.ImageName}{tagSuffix}";
            return true;
        }

        if (resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault() is { } imageAnnotation)
        {
            var registryPrefix = string.IsNullOrEmpty(imageAnnotation.Registry) ? string.Empty : $"{imageAnnotation.Registry}/";

            if (string.IsNullOrEmpty(imageAnnotation.SHA256))
            {
                var tagSuffix = string.IsNullOrEmpty(imageAnnotation.Tag) ? string.Empty : $":{imageAnnotation.Tag}";
                imageName = $"{registryPrefix}{imageAnnotation.Image}{tagSuffix}";
            }
            else
            {
                var shaSuffix = $"@sha256:{imageAnnotation.SHA256}";
                imageName = $"{registryPrefix}{imageAnnotation.Image}{shaSuffix}";
            }

            return true;
        }

        imageName = null;
        return false;
    }

    /// <summary>
    /// Gets the number of replicas for the specified resource. Defaults to <c>1</c> if no
    /// <see cref="ReplicaAnnotation" /> is found.
    /// </summary>
    /// <param name="resource">The resource to get the replica count for.</param>
    /// <returns>The number of replicas for the specified resource.</returns>
    public static int GetReplicaCount(this IResource resource)
    {
        if (resource.TryGetLastAnnotation<ReplicaAnnotation>(out var replicaAnnotation))
        {
            return replicaAnnotation.Replicas;
        }
        else
        {
            return 1;
        }
    }

    /// <summary>
    /// Determines whether the specified resource requires image building.
    /// </summary>
    /// <remarks>
    /// Resources require an image build if they provide their own Dockerfile or are a project.
    /// Resources that are excluded from publishing are not considered to require image building.
    /// </remarks>
    /// <param name="resource">The resource to evaluate for image build requirements.</param>
    /// <returns>True if the resource requires image building; otherwise, false.</returns>
    public static bool RequiresImageBuild(this IResource resource)
    {
        if (resource.IsExcludedFromPublish())
        {
            return false;
        }

        return resource is ProjectResource || resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _);
    }

    /// <summary>
    /// Determines whether the specified resource requires image building and pushing.
    /// </summary>
    /// <remarks>
    /// Resources require an image build and a push to a container registry if they provide
    /// their own Dockerfile or are a project.
    /// Resources that are excluded from publishing are not considered to require image building and pushing.
    /// </remarks>
    /// <param name="resource">The resource to evaluate for image push requirements.</param>
    /// <returns>True if the resource requires image building and pushing; otherwise, false.</returns>
    public static bool RequiresImageBuildAndPush(this IResource resource)
    {
        return resource.RequiresImageBuild() && !resource.IsBuildOnlyContainer();
    }

    internal static bool IsBuildOnlyContainer(this IResource resource)
    {
        return resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuild) &&
            !dockerfileBuild.HasEntrypoint;
    }

    /// <summary>
    /// Gets the deployment target for the specified resource, if any. Throws an exception if
    /// there are multiple compute environments and a compute environment is not explicitly specified.
    /// </summary>
    public static DeploymentTargetAnnotation? GetDeploymentTargetAnnotation(this IResource resource, IComputeEnvironmentResource? targetComputeEnvironment = null)
    {
        IComputeEnvironmentResource? selectedComputeEnvironment = null;
        if (resource.TryGetLastAnnotation<ComputeEnvironmentAnnotation>(out var computeEnvironmentAnnotation))
        {
            // If you have a ComputeEnvironmentAnnotation, it means the resource is bound to a specific compute environment.
            // Skip the annotation if it doesn't match the specified computeEnvironmentResource.
            if (targetComputeEnvironment is not null && targetComputeEnvironment != computeEnvironmentAnnotation.ComputeEnvironment)
            {
                return null;
            }

            // If the resource is bound to a specific compute environment, use that one.
            selectedComputeEnvironment = computeEnvironmentAnnotation.ComputeEnvironment;
        }

        if (resource.TryGetAnnotationsOfType<DeploymentTargetAnnotation>(out var deploymentTargetAnnotations))
        {
            var annotations = deploymentTargetAnnotations.ToArray();

            if (selectedComputeEnvironment is not null)
            {
                return annotations.SingleOrDefault(a => a.ComputeEnvironment == selectedComputeEnvironment);
            }

            if (annotations.Length > 1)
            {
                var computeEnvironmentNames = string.Join(", ", annotations.Select(a => a.ComputeEnvironment?.Name));
                throw new InvalidOperationException($"Resource '{resource.Name}' has multiple compute environments - '{computeEnvironmentNames}'. Please specify a single compute environment using 'WithComputeEnvironment'.");
            }

            return annotations[0];
        }
        return null;
    }

    /// <summary>
    /// Gets the lifetime type of the container for the specified resource.
    /// Defaults to <see cref="ContainerLifetime.Session"/> if no <see cref="ContainerLifetimeAnnotation"/> is found.
    /// </summary>
    /// <param name="resource">The resource to get the ContainerLifetimeType for.</param>
    /// <returns>
    /// The <see cref="ContainerLifetime"/> from the <see cref="ContainerLifetimeAnnotation"/> for the resource (if the annotation exists).
    /// Defaults to <see cref="ContainerLifetime.Session"/> if the annotation is not set.
    /// </returns>
    internal static ContainerLifetime GetContainerLifetimeType(this IResource resource)
    {
        if (resource.TryGetLastAnnotation<ContainerLifetimeAnnotation>(out var lifetimeAnnotation))
        {
            return lifetimeAnnotation.Lifetime;
        }

        return ContainerLifetime.Session;
    }

    /// <summary>
    /// Determines whether the specified resource has a pull policy annotation and retrieves the value if it does.
    /// </summary>
    /// <param name="resource">The resource to check for a ContainerPullPolicy annotation</param>
    /// <param name="pullPolicy">The <see cref="ImagePullPolicy"/> for the annotation</param>
    /// <returns>True if an annotation exists, false otherwise</returns>
    internal static bool TryGetContainerImagePullPolicy(this IResource resource, [NotNullWhen(true)] out ImagePullPolicy? pullPolicy)
    {
        if (resource.TryGetLastAnnotation<ContainerImagePullPolicyAnnotation>(out var pullPolicyAnnotation))
        {
            pullPolicy = pullPolicyAnnotation.ImagePullPolicy;
            return true;
        }

        pullPolicy = null;
        return false;
    }

    /// <summary>
    /// Determines whether a resource has proxy support enabled or not. Container resources may have a <see cref="ProxySupportAnnotation"/> setting that disables proxying for their
    /// endpoints regardless of the endpoint proxy configuration.
    /// </summary>
    /// <param name="resource">The resource to get proxy support for.</param>
    /// <returns>True if the resource supports proxied endpoints/services, false otherwise.</returns>
    internal static bool SupportsProxy(this IResource resource)
    {
        // If the resource doesn't have a ProxySupportAnnotation or the ProxyEnabled property on the annotation is true, then the resource supports proxying.
        return !resource.TryGetLastAnnotation<ProxySupportAnnotation>(out var proxySupportAnnotation) || proxySupportAnnotation.ProxyEnabled;
    }

    /// <summary>
    /// Get the top resource in the resource hierarchy.
    /// e.g. for a AzureBlobStorageResource, the top resource is the AzureStorageResource.
    /// </summary>
    internal static IResource GetRootResource(this IResource resource) =>
        resource switch
        {
            IResourceWithParent resWithParent => resWithParent.Parent.GetRootResource(),
            _ => resource
        };

    /// <summary>
    /// Returns a single DCP resource name for the specified resource.
    /// Throws <see cref="InvalidOperationException"/> if the resource has no resolved names or multiple resolved names.
    /// </summary>
    internal static string GetResolvedResourceName(this IResource resource)
    {
        var names = resource.GetResolvedResourceNames();
        if (names.Length == 0)
        {
            throw new InvalidOperationException($"Resource '{resource.Name}' has no resolved names.");
        }
        if (names.Length > 1)
        {
            throw new InvalidOperationException($"Resource '{resource.Name}' has multiple resolved names: {string.Join(", ", names)}.");
        }

        return names[0];
    }

    /// <summary>
    /// Gets resolved names for the specified resource.
    /// DCP resources are given a unique suffix as part of the complete name. We want to use that value.
    /// Also, a DCP resource could have multiple instances. All instance names are returned for a resource.
    /// </summary>
    internal static string[] GetResolvedResourceNames(this IResource resource)
    {
        if (resource.TryGetLastAnnotation<DcpInstancesAnnotation>(out var replicaAnnotation) && !replicaAnnotation.Instances.IsEmpty)
        {
            return replicaAnnotation.Instances.Select(i => i.Name).ToArray();
        }
        else
        {
            return [resource.Name];
        }
    }

    /// <summary>
    /// Processes image push options callbacks for the specified resource.
    /// </summary>
    /// <param name="resource">The resource to process image push options for.</param>
    /// <param name="cancellationToken">A cancellation token to observe while processing.</param>
    /// <returns>The resolved image push options.</returns>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
    internal static async Task<ContainerImagePushOptions> ProcessImagePushOptionsCallbackAsync(
        this IResource resource,
        CancellationToken cancellationToken)
    {
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = resource.Name.ToLowerInvariant(),
            RemoteImageTag = "latest"
        };

        var context = new ContainerImagePushOptionsCallbackContext
        {
            Resource = resource,
            CancellationToken = cancellationToken,
            Options = options
        };

        var callbacks = resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>();

        foreach (var callback in callbacks)
        {
            await callback.Callback(context).ConfigureAwait(false);
        }

        return options;
    }

    /// <summary>
    /// Gets the container registry associated with the specified resource.
    /// </summary>
    /// <param name="resource">The resource to get the container registry for.</param>
    /// <returns>The container registry associated with the resource.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the resource does not have a container registry reference.</exception>
    /// <remarks>
    /// This method checks for a container registry in the following order:
    /// <list type="number">
    /// <item>The <see cref="ContainerRegistryReferenceAnnotation"/> on the resource (set via <c>WithContainerRegistry</c>).</item>
    /// <item>The <see cref="DeploymentTargetAnnotation"/> on the resource.</item>
    /// <item>The <see cref="RegistryTargetAnnotation"/> on the resource (automatically added when a registry is added to the app model).</item>
    /// </list>
    /// </remarks>
    internal static IContainerRegistry GetContainerRegistry(this IResource resource)
    {
        // Try ContainerRegistryReferenceAnnotation (explicit WithContainerRegistry call)
        var registryAnnotation = resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>().LastOrDefault();
        if (registryAnnotation is not null)
        {
            return registryAnnotation.Registry;
        }

        // Try to get the container registry from DeploymentTargetAnnotation first
        var deploymentTarget = resource.GetDeploymentTargetAnnotation();
        if (deploymentTarget?.ContainerRegistry is not null)
        {
            return deploymentTarget.ContainerRegistry;
        }

        // Fall back to RegistryTargetAnnotation (added automatically via BeforeStartEvent)
        var registryTargetAnnotations = resource.Annotations.OfType<RegistryTargetAnnotation>().ToArray();
        if (registryTargetAnnotations.Length == 1)
        {
            return registryTargetAnnotations[0].Registry;
        }

        if (registryTargetAnnotations.Length > 1)
        {
            var registryNames = string.Join(", ", registryTargetAnnotations.Select(a => a.Registry is IResource res ? res.Name : a.Registry.ToString()));
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' has multiple container registries available - '{registryNames}'. " +
                $"Please specify which registry to use with '.WithContainerRegistry(registryBuilder)'.");
        }

        throw new InvalidOperationException($"Resource '{resource.Name}' does not have a container registry reference.");
    }

    /// <summary>
    /// Gets the full remote image name for the specified resource, including registry endpoint and tag.
    /// </summary>
    /// <param name="resource">The resource to get the remote image name for.</param>
    /// <param name="cancellationToken">A cancellation token to observe while processing.</param>
    /// <returns>The fully qualified remote image name.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the resource does not have a container registry reference.</exception>
    /// <remarks>
    /// This method processes any image push options callbacks on the resource and combines the result
    /// with the container registry to produce the full remote image name.
    /// </remarks>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
    internal static async Task<string> GetFullRemoteImageNameAsync(
        this IResource resource,
        CancellationToken cancellationToken)
    {
        var pushOptions = await resource.ProcessImagePushOptionsCallbackAsync(cancellationToken).ConfigureAwait(false);
        var registry = resource.GetContainerRegistry();
        return await pushOptions.GetFullRemoteImageNameAsync(registry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the archive file path for a container image.
    /// </summary>
    /// <param name="outputPath">The output directory path.</param>
    /// <param name="imageName">The image name.</param>
    /// <param name="imageTag">The image tag (optional, defaults to "latest" if provided).</param>
    /// <returns>The full path to the archive file with .tar extension.</returns>
    internal static string GetContainerImageArchivePath(string outputPath, string imageName, string? imageTag = null)
    {
        var fileName = string.IsNullOrEmpty(imageTag)
            ? $"{imageName}.tar"
            : $"{imageName}-{imageTag}.tar";
        return Path.Combine(outputPath, fileName);
    }

    /// <summary>
    /// Gets a logger for the specified resource using the provided service provider.
    /// </summary>
    /// <param name="resource">The resource to get the logger for.</param>
    /// <param name="serviceProvider">The service provider to resolve dependencies.</param>
    /// <returns>A logger instance for the specified resource.</returns>
    internal static ILogger GetLogger(this IResource resource, IServiceProvider serviceProvider)
    {
        var resourceLoggerService = serviceProvider.GetRequiredService<ResourceLoggerService>();
        return resourceLoggerService.GetLogger(resource);
    }
}
