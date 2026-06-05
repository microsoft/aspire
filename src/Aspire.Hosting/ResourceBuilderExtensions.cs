// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring resources with environment variables.
/// </summary>
public static class ResourceBuilderExtensions
{
    private const string ConnectionStringEnvironmentName = "ConnectionStrings__";

    /// <summary>
    /// Adds an environment variable to the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="value">The value of the environment variable.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, string? value) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return builder.WithAnnotation(new EnvironmentAnnotation(name, value ?? string.Empty));
    }

    /// <summary>
    /// Adds an environment variable to the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="value">The value of the environment variable.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, in ReferenceExpression.ExpressionInterpolatedStringHandler value)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var expression = value.GetExpression();

        builder.WithReferenceRelationship(expression);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[name] = expression;
        });
    }

    /// <summary>
    /// Adds an environment variable to the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="value">The value of the environment variable.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, ReferenceExpression value)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        builder.WithReferenceRelationship(value);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[name] = value;
        });
    }

    /// <summary>
    /// Adds an environment variable to the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="callback">A callback that allows for deferred execution of a specific environment variable. This runs after resources have been allocated by the orchestrator and allows access to other resources to resolve computed data, e.g. connection strings, ports.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, Func<string> callback) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new EnvironmentCallbackAnnotation(name, callback));
    }

    /// <summary>
    /// Allows for the population of environment variables on a resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback that allows for deferred execution for computing many environment variables. This runs after resources have been allocated by the orchestrator and allows access to other resources to resolve computed data, e.g. connection strings, ports.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, Action<EnvironmentCallbackContext> callback) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new EnvironmentCallbackAnnotation(callback));
    }

    /// <summary>
    /// Allows for the population of environment variables on a resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback that allows for deferred execution for computing many environment variables. This runs after resources have been allocated by the orchestrator and allows access to other resources to resolve computed data, e.g. connection strings, ports.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, Func<EnvironmentCallbackContext, Task> callback) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new EnvironmentCallbackAnnotation(callback));
    }

    /// <summary>
    /// Adds an environment variable to the resource with the endpoint for <paramref name="endpointReference"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="endpointReference">The endpoint from which to extract the url.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, EndpointReference endpointReference)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(endpointReference);

        builder.WithReferenceRelationship(endpointReference.Resource);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[name] = endpointReference;
        });
    }

    /// <summary>
    /// Adds an environment variable to the resource with the URL from the <see cref="ExternalServiceResource"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="externalService">The external service.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, IResourceBuilder<ExternalServiceResource> externalService)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(externalService);

        builder.WithReferenceRelationship(externalService.Resource);

        if (externalService.Resource.Uri is not null)
        {
            builder.WithEnvironment(name, externalService.Resource.Uri.ToString());
        }
        else if (externalService.Resource.UrlParameter is not null)
        {
            builder.WithEnvironment(async context =>
            {
                // In publish mode we can't validate the parameter value so we'll just use it without validating.
                if (!context.ExecutionContext.IsPublishMode)
                {
                    var url = await externalService.Resource.UrlParameter.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
                    if (!ExternalServiceResource.UrlIsValidForExternalService(url, out var _, out var message))
                    {
                        throw new DistributedApplicationException($"The URL parameter '{externalService.Resource.UrlParameter.Name}' for the external service '{externalService.Resource.Name}' is invalid: {message}");
                    }
                }

                context.EnvironmentVariables[name] = externalService.Resource.UrlParameter;
            });
        }

        return builder;
    }

    /// <summary>
    /// Adds an environment variable to the resource with the value from <paramref name="parameter"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">Name of environment variable.</param>
    /// <param name="parameter">Resource builder for the parameter resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, IResourceBuilder<ParameterResource> parameter) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameter);

        builder.WithReferenceRelationship(parameter.Resource);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[name] = parameter.Resource;
        });
    }

    /// <summary>
    /// Adds an environment variable to the resource with the connection string from the referenced resource.
    /// </summary>
    /// <typeparam name="T">The destination resource type.</typeparam>
    /// <param name="builder">The destination resource builder to which the environment variable will be added.</param>
    /// <param name="envVarName">The name of the environment variable under which the connection string will be set.</param>
    /// <param name="resource">The resource builder of the referenced service from which to pull the connection string.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(
        this IResourceBuilder<T> builder,
        string envVarName,
        IResourceBuilder<IResourceWithConnectionString> resource)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(envVarName);
        ArgumentNullException.ThrowIfNull(resource);

        builder.WithReferenceRelationship(resource.Resource);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[envVarName] = new ConnectionStringReference(resource.Resource, optional: false);
        });
    }

    /// <summary>
    /// Adds an environment variable to the resource with a value that implements both <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <typeparam name="TValue">The value type that implements both <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="value">The value that provides both runtime values and manifest expressions.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T, TValue>(this IResourceBuilder<T> builder, string name, TValue value)
        where T : IResourceWithEnvironment
        where TValue : IValueProvider, IManifestExpressionProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        // Check if the value has resource references and link them
        if (value is IValueWithReferences valueWithReferences)
        {
            WalkAndLinkResourceReferences(builder, valueWithReferences.References);
        }

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[name] = value;
        });
    }

    /// <summary>
    /// Adds a connection property annotation to the resource being built. Any resource referencing this resource will
    /// get this connection property included in its environment variables.
    /// </summary>
    /// <remarks>Use this method to associate a named connection property with a resource during its
    /// construction. This is typically used to provide connection-related metadata for resources that require
    /// environment-specific configuration.</remarks>
    /// <typeparam name="T">The type of the resource, which must implement IResourceWithEnvironment.</typeparam>
    /// <param name="builder">The resource builder to which the connection property annotation will be added. Cannot be null.</param>
    /// <param name="name">The name of the connection property to annotate. Cannot be null.</param>
    /// <param name="value">The value of the connection property, specified as a reference expression.</param>
    /// <returns>The same resource builder instance with the connection property annotation applied.</returns>
    public static IResourceBuilder<T> WithConnectionProperty<T>(this IResourceBuilder<T> builder, string name, ReferenceExpression value) where T : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return builder.WithAnnotation(new ConnectionPropertyAnnotation(name, value));
    }

    /// <summary>
    /// Adds a connection property annotation to the resource being built. Any resource referencing this resource will
    /// get this connection property included in its environment variables.
    /// </summary>
    /// <typeparam name="T">The type of resource that implements the IResourceWithEnvironment interface.</typeparam>
    /// <param name="builder">The resource builder to which the connection property will be added. Cannot be null.</param>
    /// <param name="name">The name of the connection property to add. Cannot be null.</param>
    /// <param name="value">The value to assign to the connection property.</param>
    /// <returns>The same resource builder instance with the specified connection property annotation applied.</returns>
    public static IResourceBuilder<T> WithConnectionProperty<T>(this IResourceBuilder<T> builder, string name, string value) where T : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return builder.WithAnnotation(new ConnectionPropertyAnnotation(name, ReferenceExpression.Create($"{value}")));
    }

    /// <summary>
    /// Adds arguments to be passed to a resource that supports arguments when it is launched.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder for a resource implementing <see cref="IResourceWithArgs"/>.</param>
    /// <param name="args">The arguments to be passed to the resource when it is started.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithArgs<T>(this IResourceBuilder<T> builder, params string[] args) where T : IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        return builder.WithArgs(context => context.Args.AddRange(args));
    }

    /// <summary>
    /// Adds arguments to be passed to a resource that supports arguments when it is launched.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder for a resource implementing <see cref="IResourceWithArgs"/>.</param>
    /// <param name="args">The arguments to be passed to the resource when it is started.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithArgs<T>(this IResourceBuilder<T> builder, params object[] args) where T : IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        WalkAndLinkResourceReferences(builder, args);

        return builder.WithArgs(context => context.Args.AddRange(args));
    }

    /// <summary>
    /// Adds a callback to be executed with a list of command-line arguments when a resource is started.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="builder">The resource builder for a resource implementing <see cref="IResourceWithArgs"/>.</param>
    /// <param name="callback">A callback that allows for deferred execution for computing arguments. This runs after resources have been allocated by the orchestrator and allows access to other resources to resolve computed data, e.g. connection strings, ports.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithArgs<T>(this IResourceBuilder<T> builder, Action<CommandLineArgsCallbackContext> callback) where T : IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithArgs(context =>
        {
            callback(context);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Adds an asynchronous callback to be executed with a list of command-line arguments when a resource is started.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder for a resource implementing <see cref="IResourceWithArgs"/>.</param>
    /// <param name="callback">An asynchronous callback that allows for deferred execution for computing arguments. This runs after resources have been allocated by the orchestrator and allows access to other resources to resolve computed data, e.g. connection strings, ports.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithArgs<T>(this IResourceBuilder<T> builder, Func<CommandLineArgsCallbackContext, Task> callback) where T : IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new CommandLineArgsCallbackAnnotation(callback));
    }

    /// <summary>
    /// Registers a callback which is invoked when manifest is generated for the app model.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">Callback method which takes a <see cref="ManifestPublishingContext"/> which can be used to inject JSON into the manifest.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithManifestPublishingCallback<T>(this IResourceBuilder<T> builder, Action<ManifestPublishingContext> callback) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        // You can only ever have one manifest publishing callback, so it must be a replace operation.
        return builder.WithAnnotation(new ManifestPublishingCallbackAnnotation(callback), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Registers an async callback which is invoked when manifest is generated for the app model.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">Callback method which takes a <see cref="ManifestPublishingContext"/> which can be used to inject JSON into the manifest.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithManifestPublishingCallback<T>(this IResourceBuilder<T> builder, Func<ManifestPublishingContext, Task> callback) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        // You can only ever have one manifest publishing callback, so it must be a replace operation.
        return builder.WithAnnotation(new ManifestPublishingCallbackAnnotation(callback), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Registers a callback which is invoked when a connection string is requested for a resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="resource">Resource to which connection string generation is redirected.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithConnectionStringRedirection<T>(this IResourceBuilder<T> builder, IResourceWithConnectionString resource) where T : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);

        // You can only ever have one manifest publishing callback, so it must be a replace operation.
        return builder.WithAnnotation(new ConnectionStringRedirectAnnotation(resource), ResourceAnnotationMutationBehavior.Replace);
    }

    private static Action<EnvironmentCallbackContext> CreateEndpointReferenceEnvironmentPopulationCallback(EndpointReferenceAnnotation endpointReferencesAnnotation, string? specificEndpointName = null, string? name = null)
    {
        return (context) =>
        {
            var annotation = endpointReferencesAnnotation;
            var serviceName = name ?? annotation.Resource.Name;

            // Determine what to inject based on the annotation on the destination resource
            context.Resource.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var injectionAnnotation);
            var flags = injectionAnnotation?.Flags ?? ReferenceEnvironmentInjectionFlags.All;

            foreach (var endpoint in annotation.Resource.GetEndpoints(annotation.ContextNetworkID))
            {
                if (specificEndpointName != null && !string.Equals(endpoint.EndpointName, specificEndpointName, StringComparison.OrdinalIgnoreCase))
                {
                    // Skip this endpoint since it's not the one we want to reference.
                    continue;
                }

                var endpointName = endpoint.EndpointName;
                if (!annotation.UseAllEndpoints && !annotation.EndpointNames.Contains(endpointName))
                {
                    // Skip this endpoint since it's not in the list of endpoints we want to reference.
                    continue;
                }

                // Add the endpoint, rewriting localhost to the container host if necessary.

                if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.Endpoints))
                {
                    var serviceKey = name is null ? serviceName.ToUpperInvariant() : name;
                    var encodedEndpointName = EnvironmentVariableNameEncoder.Encode(endpointName);
                    context.EnvironmentVariables[$"{EnvironmentVariableNameEncoder.Encode(serviceKey)}_{encodedEndpointName.ToUpperInvariant()}"] = endpoint;
                }

                if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.ServiceDiscovery))
                {
                    context.EnvironmentVariables[$"services__{serviceName}__{endpointName}__0"] = endpoint;
                }
            }
        };
    }

    /// <summary>
    /// Configures how information is injected into environment variables when the resource references other resources.
    /// </summary>
    /// <typeparam name="TDestination">The destination resource.</typeparam>
    /// <param name="builder">The resource to configure.</param>
    /// <param name="flags">The injection flags determining which reference information is emitted.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<TDestination> WithReferenceEnvironment<TDestination>(this IResourceBuilder<TDestination> builder, ReferenceEnvironmentInjectionFlags flags)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new ReferenceEnvironmentInjectionAnnotation(flags));
    }

    /// <summary>
    /// Injects a connection string as an environment variable from the source resource into the destination resource, using the source resource's name as the connection string name (if not overridden).
    /// The format of the environment variable will be "ConnectionStrings__{sourceResourceName}={connectionString}".
    /// <para>
    /// Each resource defines the format of the connection string value. The
    /// underlying connection string value can be retrieved using <see cref="IResourceWithConnectionString.GetConnectionStringAsync(CancellationToken)"/>.
    /// </para>
    /// <para>
    /// Connection strings are also resolved by the configuration system (appSettings.json in the AppHost project, or environment variables). If a connection string is not found on the resource, the configuration system will be queried for a connection string
    /// using the resource's name.
    /// </para>
    /// </summary>
    /// <typeparam name="TDestination">The destination resource.</typeparam>
    /// <param name="builder">The resource where connection string will be injected.</param>
    /// <param name="source">The resource from which to extract the connection string.</param>
    /// <param name="connectionName">An override of the source resource's name for the connection string. The resulting connection string will be "ConnectionStrings__connectionName" if this is not null.</param>
    /// <param name="optional"><see langword="true"/> to allow a missing connection string; <see langword="false"/> to throw an exception if the connection string is not found.</param>
    /// <exception cref="DistributedApplicationException">Throws an exception if the connection string resolves to null. It can be null if the resource has no connection string, and if the configuration has no connection string for the source resource.</exception>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(this IResourceBuilder<TDestination> builder, IResourceBuilder<IResourceWithConnectionString> source, string? connectionName = null, bool optional = false)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        var resource = source.Resource;
        connectionName ??= resource.Name;

        builder.WithReferenceRelationship(resource);

        // Determine what to inject based on the annotation on the destination resource
        builder.Resource.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var injectionAnnotation);
        var flags = injectionAnnotation?.Flags ?? ReferenceEnvironmentInjectionFlags.All;

        return builder.WithEnvironment(context =>
        {
            if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.ConnectionString))
            {
                var connectionStringName = resource.ConnectionStringEnvironmentVariable ?? $"{ConnectionStringEnvironmentName}{connectionName}";
                context.EnvironmentVariables[connectionStringName] = new ConnectionStringReference(resource, optional);
            }

            if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.ConnectionProperties))
            {
                var prefix = connectionName switch
                {
                    "" => "",
                    _ => $"{EnvironmentVariableNameEncoder.Encode(connectionName).ToUpperInvariant()}_"
                };

                SplatConnectionProperties(resource, prefix, context);

                if (resource.TryGetAnnotationsOfType<ConnectionPropertyAnnotation>(out var connectionPropertyAnnotations))
                {
                    foreach (var annotation in connectionPropertyAnnotations)
                    {
                        context.EnvironmentVariables[$"{prefix}{annotation.Name.ToUpperInvariant()}"] = annotation.Value;
                    }
                }
            }
        });
    }

    private static void SplatConnectionProperties(IResourceWithConnectionString resource, string prefix, EnvironmentCallbackContext context)
    {
        ArgumentNullException.ThrowIfNull(resource);

        foreach (var connectionProperty in resource.GetConnectionProperties())
        {
            context.EnvironmentVariables[$"{prefix}{connectionProperty.Key.ToUpperInvariant()}"] = connectionProperty.Value;
        }
    }

    /// <summary>
    /// Retrieves the value of a specified connection property from the resource's connection properties.
    /// </summary>
    /// <remarks>Throws a KeyNotFoundException if the specified key does not exist in the resource's
    /// connection properties.</remarks>
    /// <param name="resource">The resource that provides the connection properties. Cannot be null.</param>
    /// <param name="key">The key of the connection property to retrieve. Cannot be null.</param>
    /// <returns>The value associated with the specified connection property key.</returns>
    public static ReferenceExpression GetConnectionProperty(this IResourceWithConnectionString resource, string key)
    {
        foreach (var connectionProperty in resource.GetConnectionProperties())
        {
            if (string.Equals(connectionProperty.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return connectionProperty.Value;
            }
        }

        return ReferenceExpression.Empty;
    }

    /// <summary>
    /// Injects service discovery and endpoint information as environment variables from the project resource into the destination resource, using the source resource's name as the service name.
    /// Each endpoint defined on the project resource will be injected using the format defined by the <see cref="ReferenceEnvironmentInjectionAnnotation"/> on the destination resource, i.e.
    /// either "services__{sourceResourceName}__{endpointName}__{endpointIndex}={uriString}" for .NET service discovery, or "{RESOURCE_ENDPOINT}={uri}" for endpoint injection.
    /// </summary>
    /// <typeparam name="TDestination">The destination resource.</typeparam>
    /// <param name="builder">The resource where the service discovery information will be injected.</param>
    /// <param name="source">The resource from which to extract service discovery information.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(this IResourceBuilder<TDestination> builder, IResourceBuilder<IResourceWithServiceDiscovery> source)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        ApplyEndpoints(builder, source.Resource);
        return builder;
    }

    /// <summary>
    /// Injects service discovery and endpoint information as environment variables from the project resource into the destination resource, using the source resource's name as the service name.
    /// Each endpoint defined on the project resource will be injected using the format defined by the <see cref="ReferenceEnvironmentInjectionAnnotation"/> on the destination resource, i.e.
    /// either "services__{name}__{endpointName}__{endpointIndex}={uriString}" for .NET service discovery, or "{name}_{ENDPOINT}={uri}" for endpoint injection.
    /// </summary>
    /// <typeparam name="TDestination">The destination resource.</typeparam>
    /// <param name="builder">The resource where the service discovery information will be injected.</param>
    /// <param name="source">The resource from which to extract service discovery information.</param>
    /// <param name="name">The name of the resource for the environment variable.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(this IResourceBuilder<TDestination> builder, IResourceBuilder<IResourceWithServiceDiscovery> source, string name)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        ApplyEndpoints(builder, source.Resource, endpointName: null, name);
        return builder;
    }

    /// <summary>
    /// Injects service discovery and endpoint information as environment variables from the uri into the destination resource, using the name as the service name.
    /// The uri will be injected using the format defined by the <see cref="ReferenceEnvironmentInjectionAnnotation"/> on the destination resource, i.e.
    /// either "services__{name}__default__0={uri}" for .NET service discovery, or "{name}={uri}" for endpoint injection.
    /// </summary>
    /// <typeparam name="TDestination"></typeparam>
    /// <param name="builder">The resource where the service discovery information will be injected.</param>
    /// <param name="name">The name of the service.</param>
    /// <param name="uri">The uri of the service.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(this IResourceBuilder<TDestination> builder, string name, Uri uri)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("The uri for service reference must be absolute.");
        }

        if (!uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The uri absolute path must end with '/'.");
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("The URI cannot contain a fragment.");
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidOperationException("The URI cannot contain a query string.");
        }

        // Determine what to inject based on the annotation on the destination resource
        builder.Resource.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var injectionAnnotation);
        var flags = injectionAnnotation?.Flags ?? ReferenceEnvironmentInjectionFlags.All;

        if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.ServiceDiscovery))
        {
            builder.WithEnvironment($"services__{name}__default__0", uri.ToString());
        }

        if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.Endpoints))
        {
            builder.WithEnvironment(EnvironmentVariableNameEncoder.Encode(name), uri.ToString());
        }

        return builder;
    }

    /// <summary>
    /// Injects service discovery information as environment variables from the <see cref="ExternalServiceResource"/> into the destination resource, using the name as the service name.
    /// The uri will be injected using the format "services__{name}__default__0={uri}."
    /// </summary>
    /// <typeparam name="TDestination"></typeparam>
    /// <param name="builder">The resource where the service discovery information will be injected.</param>
    /// <param name="externalService">The external service.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(this IResourceBuilder<TDestination> builder, IResourceBuilder<ExternalServiceResource> externalService)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(externalService);

        builder.WithReferenceRelationship(externalService.Resource);

        // Determine what to inject based on the annotation on the destination resource
        builder.Resource.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var injectionAnnotation);
        var flags = injectionAnnotation?.Flags ?? ReferenceEnvironmentInjectionFlags.All;

        if (externalService.Resource.Uri is { } uri)
        {
            if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.Endpoints))
            {
                var encodedResourceName = EnvironmentVariableNameEncoder.Encode(externalService.Resource.Name);
                builder.WithEnvironment(encodedResourceName.ToUpperInvariant(), uri.ToString());
            }

            if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.ServiceDiscovery))
            {
                var envVarName = $"services__{externalService.Resource.Name}__{uri.Scheme}__0";
                builder.WithEnvironment(envVarName, uri.ToString());
            }
        }
        else if (externalService.Resource.UrlParameter is not null)
        {
            builder.WithEnvironment(async context =>
            {
                string discoveryEnvVarName;
                string endpointEnvVarName;
                var encodedResourceName = EnvironmentVariableNameEncoder.Encode(externalService.Resource.Name);

                if (context.ExecutionContext.IsPublishMode)
                {
                    // In publish mode we can't read the parameter value to get the scheme so use 'default'
                    discoveryEnvVarName = $"services__{externalService.Resource.Name}__default__0";
                    endpointEnvVarName = encodedResourceName.ToUpperInvariant();
                }
                else if (ExternalServiceResource.UrlIsValidForExternalService(await externalService.Resource.UrlParameter.GetValueAsync(context.CancellationToken).ConfigureAwait(false), out var uri, out var message))
                {
                    discoveryEnvVarName = $"services__{externalService.Resource.Name}__{uri.Scheme}__0";
                    var encodedScheme = EnvironmentVariableNameEncoder.Encode(uri.Scheme);
                    endpointEnvVarName = $"{encodedResourceName.ToUpperInvariant()}_{encodedScheme.ToUpperInvariant()}";
                }
                else
                {
                    throw new DistributedApplicationException($"The URL parameter '{externalService.Resource.UrlParameter.Name}' for the external service '{externalService.Resource.Name}' is invalid: {message}");
                }

                if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.ServiceDiscovery))
                {
                    context.EnvironmentVariables[discoveryEnvVarName] = externalService.Resource.UrlParameter;
                }

                if (flags.HasFlag(ReferenceEnvironmentInjectionFlags.Endpoints))
                {
                    context.EnvironmentVariables[endpointEnvVarName] = externalService.Resource.UrlParameter;
                }
            });
        }

        return builder;
    }

    /// <summary>
    /// Injects service discovery and endpoint information from the specified endpoint into the project resource using the source resource's name as the service name.
    /// Each endpoint uri will be injected using the format defined by the <see cref="ReferenceEnvironmentInjectionAnnotation"/> on the destination resource, i.e.
    /// either "services__{name}__{endpointName}__{endpointIndex}={uriString}" for .NET service discovery, or "{NAME}_{ENDPOINT}={uri}" for endpoint injection.
    /// </summary>
    /// <typeparam name="TDestination">The destination resource.</typeparam>
    /// <param name="builder">The resource where the service discovery information will be injected.</param>
    /// <param name="endpointReference">The endpoint from which to extract the url.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(this IResourceBuilder<TDestination> builder, EndpointReference endpointReference)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpointReference);

        ApplyEndpoints(builder, endpointReference.Resource, endpointReference.EndpointName);
        return builder;
    }

    private static void ApplyEndpoints<T>(this IResourceBuilder<T> builder, IResourceWithEndpoints resourceWithEndpoints, string? endpointName = null, string? name = null)
        where T : IResourceWithEnvironment
    {
        // When adding an endpoint we get to see whether there is an EndpointReferenceAnnotation
        // on the resource, if there is then it means we have already been here before and we can just
        // skip this and note the endpoint that we want to apply to the environment in the future
        // in a single pass. There is one EndpointReferenceAnnotation per endpoint source.
        var endpointReferenceAnnotation = builder.Resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .Where(sra => sra.Resource == resourceWithEndpoints)
            .SingleOrDefault();

        if (endpointReferenceAnnotation == null)
        {
            endpointReferenceAnnotation = new EndpointReferenceAnnotation(resourceWithEndpoints);
            if (builder.Resource.IsContainer())
            {
                endpointReferenceAnnotation.ContextNetworkID = KnownNetworkIdentifiers.DefaultAspireContainerNetwork;
            }
            builder.WithAnnotation(endpointReferenceAnnotation);

            var callback = CreateEndpointReferenceEnvironmentPopulationCallback(endpointReferenceAnnotation, null, name);
            builder.WithEnvironment(callback);
        }

        // If no specific endpoint name is specified, go and add all the endpoints.
        if (endpointName == null)
        {
            endpointReferenceAnnotation.UseAllEndpoints = true;
        }
        else
        {
            endpointReferenceAnnotation.EndpointNames.Add(endpointName);
        }

        builder.WithReferenceRelationship(resourceWithEndpoints);
    }

    /// <summary>
    /// Changes an existing endpoint or creates a new endpoint if it doesn't exist and invokes callback to modify the defaults.
    /// </summary>
    /// <param name="builder">Resource builder for resource with endpoints.</param>
    /// <param name="endpointName">Name of endpoint to change.</param>
    /// <param name="callback">Callback that modifies the endpoint.</param>
    /// <param name="createIfNotExists">Create endpoint if it does not exist.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The <see cref="WithEndpoint{T}(IResourceBuilder{T}, string, Action{EndpointAnnotation}, bool)"/> method allows
    /// developers to mutate any aspect of an endpoint annotation. Note that changing one value does not automatically change
    /// other values to compatible/consistent values. For example setting the <see cref="EndpointAnnotation.Protocol"/> property
    /// of the endpoint annotation in the callback will not automatically change the <see cref="EndpointAnnotation.UriScheme"/>.
    /// All values should be set in the callback if the defaults are not acceptable.
    /// </para>
    /// <example>
    /// Configure an endpoint to use UDP.
    /// <code lang="C#">
    /// var builder = DistributedApplication.Create(args);
    /// var container = builder.AddContainer("mycontainer", "myimage")
    ///                        .WithEndpoint("myendpoint", e => {
    ///                          e.Port = 9998;
    ///                          e.TargetPort = 9999;
    ///                          e.Protocol = ProtocolType.Udp;
    ///                          e.UriScheme = "udp";
    ///                        });
    /// </code>
    /// </example>
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "<Pending>")]
    public static IResourceBuilder<T> WithEndpoint<T>(this IResourceBuilder<T> builder, [EndpointName] string endpointName, Action<EndpointAnnotation> callback, bool createIfNotExists = true) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpointName);
        ArgumentNullException.ThrowIfNull(callback);

        var endpoint = builder.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Where(ea => StringComparers.EndpointAnnotationName.Equals(ea.Name, endpointName))
            .SingleOrDefault();

        if (endpoint != null)
        {
            callback(endpoint);
        }

        if (endpoint == null && createIfNotExists)
        {
            // Endpoints for a Container will be consumed from localhost network by default, but the same EndpointAnnotation
            // can also be resolved in the context of container-to-container communication by using the target port
            // and the container name as the host. This is why we only set the context network to localhost,
            // for both container and non-container resources.
            endpoint = new EndpointAnnotation(ProtocolType.Tcp, name: endpointName, networkID: KnownNetworkIdentifiers.LocalhostNetwork);
            callback(endpoint);
            builder.Resource.Annotations.Add(endpoint);
        }
        else if (endpoint == null && !createIfNotExists)
        {
            return builder;
        }

        return builder;
    }

    /// <summary>
    /// Exposes an endpoint on a resource. A reference to this endpoint can be retrieved using <see cref="ResourceBuilderExtensions.GetEndpoint{T}(IResourceBuilder{T}, string, NetworkIdentifier)"/>.
    /// The endpoint name will be the scheme name if not specified.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="targetPort">This is the port the resource is listening on. If the endpoint is used for the container, it is the container port.</param>
    /// <param name="port">An optional port. This is the port that will be given to other resource to communicate with this resource.</param>
    /// <param name="scheme">An optional scheme e.g. (http/https). Defaults to the <paramref name="protocol"/> argument if it is defined or "tcp" otherwise.</param>
    /// <param name="name">An optional name of the endpoint. Defaults to the scheme name if not specified.</param>
    /// <param name="env">An optional name of the environment variable that will be used to inject the <paramref name="targetPort"/>. If the target port is null one will be dynamically generated and assigned to the environment variable.</param>
    /// <param name="isExternal">Indicates that this endpoint should be exposed externally at publish time.</param>
    /// <param name="protocol">Network protocol: TCP or UDP are supported today, others possibly in future.</param>
    /// <param name="isProxied">Specifies if the endpoint will be proxied by DCP. Defaults to true.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="DistributedApplicationException">Throws an exception if an endpoint with the same name already exists on the specified resource.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "<Pending>")]
    public static IResourceBuilder<T> WithEndpoint<T>(this IResourceBuilder<T> builder, int? port = null, int? targetPort = null, string? scheme = null, [EndpointName] string? name = null, string? env = null, bool isProxied = true, bool? isExternal = null, ProtocolType? protocol = null) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Endpoints for a Container will be consumed from localhost network by default, but the same EndpointAnnotation
        // can also be resolved in the context of container-to-container communication by using the target port
        // and the container name as the host. This is why we only set the context network to localhost,
        // for both container and non-container resources.
        var annotation = new EndpointAnnotation(
            protocol: protocol ?? ProtocolType.Tcp,
            uriScheme: scheme,
            name: name,
            port: port,
            targetPort: targetPort,
            isExternal: isExternal,
            isProxied: isProxied,
            networkID: KnownNetworkIdentifiers.LocalhostNetwork);

        if (builder.Resource.Annotations.OfType<EndpointAnnotation>().Any(sb => string.Equals(sb.Name, annotation.Name, StringComparisons.EndpointAnnotationName)))
        {
            throw new DistributedApplicationException($"Endpoint with name '{annotation.Name}' already exists. Endpoint name may not have been explicitly specified and was derived automatically from scheme argument (e.g. 'http', 'https', or 'tcp'). Multiple calls to WithEndpoint (and related methods) may result in a conflict if name argument is not specified. Each endpoint must have a unique name. For more information on networking in Aspire see: https://aka.ms/dotnet/aspire/networking");
        }

        // Set the environment variable on the resource
        if (env is not null && builder.Resource is IResourceWithEndpoints resourceWithEndpoints and IResourceWithEnvironment)
        {
            annotation.TargetPortEnvironmentVariable = env;

            var endpointReference = new EndpointReference(resourceWithEndpoints, annotation, KnownNetworkIdentifiers.LocalhostNetwork);

            builder.WithAnnotation(new EnvironmentCallbackAnnotation(context =>
            {
                context.EnvironmentVariables[env] = endpointReference.Property(EndpointProperty.TargetPort);
            }));
        }

        return builder.WithAnnotation(annotation);
    }

    /// <summary>
    /// Exposes an endpoint on a resource. This endpoint reference can be retrieved using <see cref="ResourceBuilderExtensions.GetEndpoint{T}(IResourceBuilder{T}, string, NetworkIdentifier)"/>.
    /// The endpoint name will be the scheme name if not specified.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="targetPort">This is the port the resource is listening on. If the endpoint is used for the container, it is the container port.</param>
    /// <param name="port">An optional port. This is the port that will be given to other resource to communicate with this resource.</param>
    /// <param name="scheme">An optional scheme e.g. (http/https). Defaults to "tcp" if not specified.</param>
    /// <param name="name">An optional name of the endpoint. Defaults to the scheme name if not specified.</param>
    /// <param name="env">An optional name of the environment variable that will be used to inject the <paramref name="targetPort"/>. If the target port is null one will be dynamically generated and assigned to the environment variable.</param>
    /// <param name="isExternal">Indicates that this endpoint should be exposed externally at publish time.</param>
    /// <param name="isProxied">Specifies if the endpoint will be proxied by DCP. Defaults to true.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="DistributedApplicationException">Throws an exception if an endpoint with the same name already exists on the specified resource.</exception>
    public static IResourceBuilder<T> WithEndpoint<T>(this IResourceBuilder<T> builder, int? port, int? targetPort, string? scheme, [EndpointName] string? name, string? env, bool isProxied, bool? isExternal) where T : IResourceWithEndpoints
    {
        return WithEndpoint(builder, port, targetPort, scheme, name, env, isProxied, isExternal, protocol: null);
    }

    /// <summary>
    /// Exposes an HTTP endpoint on a resource. This endpoint reference can be retrieved using <see cref="ResourceBuilderExtensions.GetEndpoint{T}(IResourceBuilder{T}, string, NetworkIdentifier)"/>.
    /// The endpoint name will be "http" if not specified.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="targetPort">This is the port the resource is listening on. If the endpoint is used for the container, it is the container port.</param>
    /// <param name="port">An optional port. This is the port that will be given to other resource to communicate with this resource.</param>
    /// <param name="name">An optional name of the endpoint. Defaults to "http" if not specified.</param>
    /// <param name="env">An optional name of the environment variable to inject.</param>
    /// <param name="isProxied">Specifies if the endpoint will be proxied by DCP. Defaults to true.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="DistributedApplicationException">Throws an exception if an endpoint with the same name already exists on the specified resource.</exception>
    public static IResourceBuilder<T> WithHttpEndpoint<T>(this IResourceBuilder<T> builder, int? port = null, int? targetPort = null, [EndpointName] string? name = null, string? env = null, bool isProxied = true) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint(targetPort: targetPort, port: port, scheme: "http", name: name, env: env, isProxied: isProxied);
    }

    /// <summary>
    /// Exposes an HTTPS endpoint on a resource. This endpoint reference can be retrieved using <see cref="ResourceBuilderExtensions.GetEndpoint{T}(IResourceBuilder{T}, string, NetworkIdentifier)"/>.
    /// The endpoint name will be "https" if not specified.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="targetPort">This is the port the resource is listening on. If the endpoint is used for the container, it is the container port.</param>
    /// <param name="port">An optional host port.</param>
    /// <param name="name">An optional name of the endpoint. Defaults to "https" if not specified.</param>
    /// <param name="env">An optional name of the environment variable to inject.</param>
    /// <param name="isProxied">Specifies if the endpoint will be proxied by DCP. Defaults to true.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="DistributedApplicationException">Throws an exception if an endpoint with the same name already exists on the specified resource.</exception>
    public static IResourceBuilder<T> WithHttpsEndpoint<T>(this IResourceBuilder<T> builder, int? port = null, int? targetPort = null, [EndpointName] string? name = null, string? env = null, bool isProxied = true) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint(targetPort: targetPort, port: port, scheme: "https", name: name, env: env, isProxied: isProxied);
    }

    /// <summary>
    /// Marks existing http or https endpoints on a resource as external.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithExternalHttpEndpoints<T>(this IResourceBuilder<T> builder) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.Resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints))
        {
            return builder;
        }

        foreach (var endpoint in endpoints)
        {
            if (endpoint.UriScheme == "http" || endpoint.UriScheme == "https")
            {
                endpoint.IsExternal = true;
            }
        }

        return builder;
    }

    /// <summary>
    /// Gets an <see cref="EndpointReference"/> by name from the resource. These endpoints are declared either using <see cref="WithEndpoint{T}(IResourceBuilder{T}, int?, int?, string?, string?, string?, bool, bool?, ProtocolType?)"/> or by launch settings (for project resources).
    /// The <see cref="EndpointReference"/> can be used to resolve the address of the endpoint in <see cref="WithEnvironment{T}(IResourceBuilder{T}, Action{EnvironmentCallbackContext})"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The the resource builder.</param>
    /// <param name="name">The name of the endpoint.</param>
    /// <param name="contextNetworkID">The network context in which to resolve the endpoint. If null, localhost (loopback) network context will be used.</param>
    /// <returns>An <see cref="EndpointReference"/> that can be used to resolve the address of the endpoint after resource allocation has occurred.</returns>
    public static EndpointReference GetEndpoint<T>(this IResourceBuilder<T> builder, [EndpointName] string name, NetworkIdentifier contextNetworkID) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Resource.GetEndpoint(name, contextNetworkID);
    }

    /// <summary>
    /// Gets an <see cref="EndpointReference"/> by name from the resource. These endpoints are declared either using <see cref="WithEndpoint{T}(IResourceBuilder{T}, int?, int?, string?, string?, string?, bool, bool?, ProtocolType?)"/> or by launch settings (for project resources).
    /// The <see cref="EndpointReference"/> can be used to resolve the address of the endpoint in <see cref="WithEnvironment{T}(IResourceBuilder{T}, Action{EnvironmentCallbackContext})"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The the resource builder.</param>
    /// <param name="name">The name of the endpoint.</param>
    /// <returns>An <see cref="EndpointReference"/> that can be used to resolve the address of the endpoint after resource allocation has occurred.</returns>
    public static EndpointReference GetEndpoint<T>(this IResourceBuilder<T> builder, [EndpointName] string name) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Resource.GetEndpoint(name);
    }

    /// <summary>
    /// Configures a resource to mark all endpoints' transport as HTTP/2. This is useful for HTTP/2 services that need prior knowledge.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> AsHttp2Service<T>(this IResourceBuilder<T> builder) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new Http2ServiceAnnotation());
    }

    /// <summary>
    /// Registers a callback to customize the URLs displayed for the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The builder for the resource.</param>
    /// <param name="callback">The callback that will customize URLs for the resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The callback will be executed after endpoints have been allocated for this resource.<br/>
    /// This allows you to modify any URLs for the resource, including adding, modifying, or even deletion.<br/>
    /// Note that any endpoints on the resource will automatically get a corresponding URL added for them.
    /// </para>
    /// <example>
    /// Update all displayed URLs to have display text:
    /// <code lang="C#">
    /// var frontend = builder.AddProject&lt;Projects.Frontend&gt;("frontend")
    ///                       .WithUrls(c =>
    ///                       {
    ///                           foreach (var url in c.Urls)
    ///                           {
    ///                               if (string.IsNullOrEmpty(url.DisplayText))
    ///                               {
    ///                                   url.DisplayText = "frontend";
    ///                               }
    ///                           }
    ///                       });
    /// </code>
    /// </example>
    /// <example>
    /// Update endpoint URLs to use a custom host name based on the resource name:
    /// <code lang="C#">
    /// var frontend = builder.AddProject&lt;Projects.Frontend&gt;("frontend")
    ///                       .WithUrls(c =>
    ///                       {
    ///                           foreach (var url in c.Urls)
    ///                           {
    ///                               if (url.Endpoint is not null)
    ///                               {
    ///                                   var uri = new UriBuilder(url.Url) { Host = $"{c.Resource.Name}.localhost" };
    ///                                   url.Url = uri.ToString();
    ///                               }
    ///                           }
    ///                       });
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithUrls<T>(this IResourceBuilder<T> builder, Action<ResourceUrlsCallbackContext> callback)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new ResourceUrlsCallbackAnnotation(callback));
    }

    /// <summary>
    /// Registers an async callback to customize the URLs displayed for the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The builder for the resource.</param>
    /// <param name="callback">The async callback that will customize URLs for the resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The callback will be executed after endpoints have been allocated for this resource.<br/>
    /// This allows you to modify any URLs for the resource, including adding, modifying, or even deletion.<br/>
    /// Note that any endpoints on the resource will automatically get a corresponding URL added for them.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<T> WithUrls<T>(this IResourceBuilder<T> builder, Func<ResourceUrlsCallbackContext, Task> callback)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new ResourceUrlsCallbackAnnotation(callback));
    }

    /// <summary>
    /// Adds a URL to be displayed for the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The builder for the resource.</param>
    /// <param name="url">A URL to show for the resource.</param>
    /// <param name="displayText">The display text to show when the link is displayed.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Use this method to add a URL to be displayed for the resource.<br/>
    /// If the URL is relative, it will be applied to all URLs for the resource, replacing the path portion of the URL.<br/>
    /// Note that any endpoints on the resource will automatically get a corresponding URL added for them.<br/>
    /// To modify the URL for a specific endpoint, use <see cref="WithUrlForEndpoint{T}(IResourceBuilder{T}, string, Action{ResourceUrlAnnotation})"/>.
    /// </remarks>
    /// <example>
    /// Add a static URL to be displayed for the resource:
    /// <code lang="C#">
    /// var frontend = builder.AddProject&lt;Projects.Frontend&gt;("frontend")
    ///                       .WithUrl("https://example.com/", "Home");
    /// </code>
    /// </example>
    /// <example>
    /// Update all displayed URLs to use the specified path and (optional) display text:
    /// <code lang="C#">
    /// var frontend = builder.AddProject&lt;Projects.Frontend&gt;("frontend")
    ///                       .WithUrl("/home", "Home");
    /// </code>
    /// </example>
    public static IResourceBuilder<T> WithUrl<T>(this IResourceBuilder<T> builder, string url, string? displayText = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(url);

        if (Uri.TryCreate(url, UriKind.Relative, out var relativeUri))
        {
            // Apply relative URL to all URLs for the resource
            return builder.WithUrls(c =>
            {
                foreach (var u in c.Urls)
                {
                    if (Uri.TryCreate(u.Url, UriKind.Absolute, out var absoluteUri)
                        && Uri.TryCreate(absoluteUri, relativeUri, out var uri))
                    {
                        u.Url = uri.ToString();
                        u.DisplayText = displayText ?? u.DisplayText;
                    }
                }
            });
        }

        // Treat as a static URL
        return builder.WithAnnotation(new ResourceUrlAnnotation { Url = url, DisplayText = displayText });
    }

    /// <summary>
    /// Adds a URL to be displayed for the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The builder for the resource.</param>
    /// <param name="url">The interpolated string that produces the URL.</param>
    /// <param name="displayText">The display text to show when the link is displayed.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Use this method to add a URL to be displayed for the resource.<br/>
    /// Note that any endpoints on the resource will automatically get a corresponding URL added for them.
    /// </remarks>
    public static IResourceBuilder<T> WithUrl<T>(this IResourceBuilder<T> builder, in ReferenceExpression.ExpressionInterpolatedStringHandler url, string? displayText = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var expression = url.GetExpression();

        return builder.WithUrl(expression, displayText);
    }

    /// <summary>
    /// Adds a URL to be displayed for the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The builder for the resource.</param>
    /// <param name="url">A <see cref="ReferenceExpression"/> that will produce the URL.</param>
    /// <param name="displayText">The display text to show when the link is displayed.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Use this method to add a URL to be displayed for the resource.<br/>
    /// Note that any endpoints on the resource will automatically get a corresponding URL added for them.
    /// </remarks>
    public static IResourceBuilder<T> WithUrl<T>(this IResourceBuilder<T> builder, ReferenceExpression url, string? displayText = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(url);

        return builder.WithAnnotation(new ResourceUrlsCallbackAnnotation(async c =>
        {
            var endpoint = url.ValueProviders.OfType<EndpointReference>().FirstOrDefault();
            var urlValue = await url.GetValueAsync(c.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(urlValue))
            {
                c.Urls.Add(new() { Endpoint = endpoint, Url = urlValue, DisplayText = displayText });
            }
        }));
    }

    /// <summary>
    /// Registers a callback to update the URL displayed for the endpoint with the specified name.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The builder for the resource.</param>
    /// <param name="endpointName">The name of the endpoint to customize the URL for.</param>
    /// <param name="callback">The callback that will customize the URL.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Use this method to customize the URL that is automatically added for an endpoint on the resource.<br/>
    /// To add another URL for an endpoint, use <see cref="WithUrlForEndpoint{T}(IResourceBuilder{T}, string, Func{EndpointReference, ResourceUrlAnnotation})"/>.
    /// </para>
    /// <para>
    /// The callback will be executed after endpoints have been allocated and the URL has been generated.<br/>
    /// This allows you to modify the URL or its display text.
    /// </para>
    /// <para>
    /// If the URL returned by <paramref name="callback"/> is relative, it will be combined with the endpoint URL to create an absolute URL.
    /// </para>
    /// <para>
    /// If the endpoint with the specified name does not exist, the callback will not be executed and a warning will be logged.
    /// </para>
    /// <example>
    /// Customize the URL for the "https" endpoint to use the link text "Home":
    /// <code lang="C#">
    /// var frontend = builder.AddProject&lt;Projects.Frontend&gt;("frontend")
    ///                       .WithUrlForEndpoint("https", url => url.DisplayText = "Home");
    /// </code>
    /// </example>
    /// <example>
    /// Customize the URL for the "https" endpoint to deep to the "/home" path:
    /// <code lang="C#">
    /// var frontend = builder.AddProject&lt;Projects.Frontend&gt;("frontend")
    ///                       .WithUrlForEndpoint("https", url => url.Url = "/home");
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithUrlForEndpoint<T>(this IResourceBuilder<T> builder, string endpointName, Action<ResourceUrlAnnotation> callback)
        where T : IResource
    {
        builder.WithUrls(context =>
        {
            var urlForEndpoint = context.Urls.FirstOrDefault(u => u.Endpoint?.EndpointName == endpointName);
            if (urlForEndpoint is not null)
            {
                callback(urlForEndpoint);
            }
            else
            {
                context.Logger.LogWarning("Could not execute callback to customize endpoint URL as no endpoint with name '{EndpointName}' could be found on resource '{ResourceName}'.", endpointName, builder.Resource.Name);
            }
        });

        return builder;
    }

    /// <summary>
    /// Registers a callback to add a URL for the endpoint with the specified name.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The builder for the resource.</param>
    /// <param name="endpointName">The name of the endpoint to add the URL for.</param>
    /// <param name="callback">The callback that will create the URL.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Use this method to add another URL for an endpoint on the resource.<br/>
    /// To customize the URL that is automatically added for an endpoint, use <see cref="WithUrlForEndpoint{T}(IResourceBuilder{T}, string, Action{ResourceUrlAnnotation})"/>.
    /// </para>
    /// <para>
    /// The callback will be executed after endpoints have been allocated and the resource is about to start.
    /// </para>
    /// <para>
    /// If the endpoint with the specified name does not exist, the callback will not be executed and a warning will be logged.
    /// </para>
    /// <example>
    /// Add a URL for the "https" endpoint that deep-links to an admin page with the text "Admin":
    /// <code lang="C#">
    /// var frontend = builder.AddProject&lt;Projects.Frontend&gt;("frontend")
    ///                       .WithUrlForEndpoint("https", ep => new() { Url = "/admin", DisplayText = "Admin" });
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithUrlForEndpoint<T>(this IResourceBuilder<T> builder, string endpointName, Func<EndpointReference, ResourceUrlAnnotation> callback)
        where T : IResourceWithEndpoints
    {
        builder.WithUrls(context =>
        {
            var endpoint = builder.GetEndpoint(endpointName);
            if (endpoint.Exists)
            {
                var url = callback(endpoint).WithEndpoint(endpoint);
                context.Urls.Add(url);
            }
            else
            {
                context.Logger.LogWarning("Could not execute callback to add an endpoint URL as no endpoint with name '{EndpointName}' could be found on resource '{ResourceName}'.", endpointName, builder.Resource.Name);
            }
        });

        return builder;
    }

    /// <summary>
    /// Configures the resource to copy container files from the specified source resource during publishing.
    /// </summary>
    /// <typeparam name="T">The type of resource being built. Must implement <see cref="IContainerFilesDestinationResource"/>.</typeparam>
    /// <param name="builder">The resource builder to which container files will be copied to.</param>
    /// <param name="source">The resource which contains the container files to be copied.</param>
    /// <param name="destinationPath">The destination path within the resource's container where the files will be copied.</param>
    public static IResourceBuilder<T> PublishWithContainerFiles<T>(
         this IResourceBuilder<T> builder,
         IResourceBuilder<IResourceWithContainerFiles> source,
         string destinationPath) where T : IContainerFilesDestinationResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        return builder.WithAnnotation(new ContainerFilesDestinationAnnotation()
        {
            Source = source.Resource,
            DestinationPath = destinationPath
        });
    }

    /// <summary>
    /// Adds a container files source annotation to the resource being built, specifying the path to the container files
    /// source.
    /// </summary>
    /// <typeparam name="T">The type of resource that supports container files and is being built.</typeparam>
    /// <param name="builder">The resource builder to which the container files source annotation will be added. Cannot be null.</param>
    /// <param name="sourcePath">The path to the container files source to associate with the resource. Cannot be null.</param>
    /// <returns>The resource builder instance with the container files source annotation applied.</returns>
    public static IResourceBuilder<T> WithContainerFilesSource<T>(
         this IResourceBuilder<T> builder,
         string sourcePath) where T : IResourceWithContainerFiles
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sourcePath);

        return builder.WithAnnotation(new ContainerFilesSourceAnnotation()
        {
            SourcePath = sourcePath
        });
    }

    /// <summary>
    /// Removes any container files source annotation from the resource being built.
    /// </summary>
    /// <typeparam name="T">The type of resource that supports container files and is being built.</typeparam>
    /// <param name="builder">The resource builder to which the container files source annotations should be removed. Cannot be null.</param>
    /// <returns>The resource builder instance with the container files source annotation applied.</returns>
    public static IResourceBuilder<T> ClearContainerFilesSources<T>(
         this IResourceBuilder<T> builder) where T : IResourceWithContainerFiles
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.Annotations
                .OfType<ContainerFilesSourceAnnotation>()
                .ToList()
                .ForEach(w => builder.Resource.Annotations.Remove(w));

        return builder;
    }

    /// <summary>
    /// Excludes a resource from being published to the manifest.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource to exclude.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> ExcludeFromManifest<T>(this IResourceBuilder<T> builder) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(ManifestPublishingCallbackAnnotation.Ignore);
    }

    /// <summary>
    /// Waits for the dependency resource to enter the Running state before starting the resource.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder for the resource that will be waiting.</param>
    /// <param name="dependency">The resource builder for the dependency resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>This method is useful when a resource should wait until another has started running. This can help
    /// reduce errors in logs during local development where dependency resources.</para>
    /// <para>Some resources automatically register health checks with the application host container. For these
    /// resources, calling <see cref="WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/> also results
    /// in the resource being blocked from starting until the health checks associated with the dependency resource
    /// return <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy"/>.</para>
    /// <para>The <see cref="WithHealthCheck{T}(IResourceBuilder{T}, string)"/> method can be used to associate
    /// additional health checks with a resource.</para>
    /// <example>
    /// Start message queue before starting the worker service.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var messaging = builder.AddRabbitMQ("messaging");
    /// builder.AddProject&lt;Projects.MyApp&gt;("myapp")
    ///        .WithReference(messaging)
    ///        .WaitFor(messaging);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WaitFor<T>(this IResourceBuilder<T> builder, IResourceBuilder<IResource> dependency) where T : IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dependency);

        return WaitForCore(builder, dependency, waitBehavior: null, addRelationship: true);
    }

    /// <summary>
    /// Waits for the dependency resource to enter the Running state before starting the resource.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder for the resource that will be waiting.</param>
    /// <param name="dependency">The resource builder for the dependency resource.</param>
    /// <param name="waitBehavior">The wait behavior to use.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>This method is useful when a resource should wait until another has started running. This can help
    /// reduce errors in logs during local development where dependency resources.</para>
    /// <para>Some resources automatically register health checks with the application host container. For these
    /// resources, calling <see cref="WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource}, WaitBehavior)"/> also results
    /// in the resource being blocked from starting until the health checks associated with the dependency resource
    /// return <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy"/>.</para>
    /// <para>The <see cref="WithHealthCheck{T}(IResourceBuilder{T}, string)"/> method can be used to associate
    /// additional health checks with a resource.</para>
    /// <para>The <paramref name="waitBehavior"/> parameter can be used to control the behavior of the
    /// wait operation. When <see cref="WaitBehavior.WaitOnResourceUnavailable"/> is specified, the wait
    /// operation will continue to wait until the resource becomes healthy. This is the default
    /// behavior with the <see cref="WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/> overload.</para>
    /// <para>When <see cref="WaitBehavior.StopOnResourceUnavailable"/> is specified, the wait operation
    /// will throw a <see cref="DistributedApplicationException"/> if the resource enters an unavailable state.</para>
    /// <example>
    /// Start message queue before starting the worker service.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var messaging = builder.AddRabbitMQ("messaging");
    /// builder.AddProject&lt;Projects.MyApp&gt;("myapp")
    ///        .WithReference(messaging)
    ///        .WaitFor(messaging, WaitBehavior.StopOnResourceUnavailable);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WaitFor<T>(this IResourceBuilder<T> builder, IResourceBuilder<IResource> dependency, WaitBehavior waitBehavior) where T : IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dependency);

        return WaitForCore(builder, dependency, waitBehavior, addRelationship: true);
    }

    private static IResourceBuilder<T> WaitForCore<T>(this IResourceBuilder<T> builder, IResourceBuilder<IResource> dependency, WaitBehavior? waitBehavior, bool addRelationship) where T : IResourceWithWaitSupport
    {
        if (builder.Resource as IResource == dependency.Resource)
        {
            throw new DistributedApplicationException($"The '{builder.Resource.Name}' resource cannot wait for itself.");
        }

        if (builder.Resource is IResourceWithParent resourceWithParent && resourceWithParent.Parent == dependency.Resource)
        {
            throw new DistributedApplicationException($"The '{builder.Resource.Name}' resource cannot wait for its parent '{dependency.Resource.Name}'.");
        }

        if (dependency.Resource is IResourceWithParent dependencyResourceWithParent)
        {
            // If the dependency resource is a child resource we automatically apply
            // the WaitFor to the parent resource. This caters for situations where
            // the child resource itself does not have any health checks setup.
            var parentBuilder = builder.ApplicationBuilder.CreateResourceBuilder(dependencyResourceWithParent.Parent);

            // Waiting for the parent is an internal implementaiton detail. Don't add a relationship here.
            builder.WaitForCore(parentBuilder, waitBehavior, addRelationship: false);
        }

        if (addRelationship)
        {
            builder.WithRelationship(dependency.Resource, KnownRelationshipTypes.WaitFor);
        }

        return builder.WithAnnotation(new WaitAnnotation(dependency.Resource, WaitType.WaitUntilHealthy) { WaitBehavior = waitBehavior });
    }

    /// <summary>
    /// Waits for the dependency resource to enter the Running state before starting the resource.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder for the resource that will be waiting.</param>
    /// <param name="dependency">The resource builder for the dependency resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>This method is useful when a resource should wait until another has started running but
    /// doesn't need to wait for health checks to pass. This can help enable initialization scenarios
    /// where services need to start before health checks can pass.</para>
    /// <para>Unlike <see cref="WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>, this method
    /// only waits for the dependency resource to enter the Running state and ignores any health check
    /// annotations associated with the dependency resource.</para>
    /// <example>
    /// Start message queue before starting the worker service, but don't wait for health checks.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var messaging = builder.AddRabbitMQ("messaging");
    /// builder.AddProject&lt;Projects.MyApp&gt;("myapp")
    ///        .WithReference(messaging)
    ///        .WaitForStart(messaging);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WaitForStart<T>(this IResourceBuilder<T> builder, IResourceBuilder<IResource> dependency) where T : IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dependency);

        return WaitForStartCore(builder, dependency, waitBehavior: null, addRelationship: true);
    }

    /// <summary>
    /// Waits for the dependency resource to enter the Running state before starting the resource.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder for the resource that will be waiting.</param>
    /// <param name="dependency">The resource builder for the dependency resource.</param>
    /// <param name="waitBehavior">The wait behavior to use.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>This method is useful when a resource should wait until another has started running but
    /// doesn't need to wait for health checks to pass. This can help enable initialization scenarios
    /// where services need to start before health checks can pass.</para>
    /// <para>Unlike <see cref="WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource}, WaitBehavior)"/>, this method
    /// only waits for the dependency resource to enter the Running state and ignores any health check
    /// annotations associated with the dependency resource.</para>
    /// <para>The <paramref name="waitBehavior"/> parameter can be used to control the behavior of the
    /// wait operation. When <see cref="WaitBehavior.WaitOnResourceUnavailable"/> is specified, the wait
    /// operation will continue to wait until the resource enters the Running state. This is the default
    /// behavior with the <see cref="WaitForStart{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/> overload.</para>
    /// <para>When <see cref="WaitBehavior.StopOnResourceUnavailable"/> is specified, the wait operation
    /// will throw a <see cref="DistributedApplicationException"/> if the resource enters an unavailable state.</para>
    /// <example>
    /// Start message queue before starting the worker service, but don't wait for health checks.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var messaging = builder.AddRabbitMQ("messaging");
    /// builder.AddProject&lt;Projects.MyApp&gt;("myapp")
    ///        .WithReference(messaging)
    ///        .WaitForStart(messaging, WaitBehavior.StopOnResourceUnavailable);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WaitForStart<T>(this IResourceBuilder<T> builder, IResourceBuilder<IResource> dependency, WaitBehavior waitBehavior) where T : IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dependency);

        return WaitForStartCore(builder, dependency, waitBehavior, addRelationship: true);
    }

    private static IResourceBuilder<T> WaitForStartCore<T>(this IResourceBuilder<T> builder, IResourceBuilder<IResource> dependency, WaitBehavior? waitBehavior, bool addRelationship) where T : IResourceWithWaitSupport
    {
        if (builder.Resource as IResource == dependency.Resource)
        {
            throw new DistributedApplicationException($"The '{builder.Resource.Name}' resource cannot wait for itself.");
        }

        if (builder.Resource is IResourceWithParent resourceWithParent && resourceWithParent.Parent == dependency.Resource)
        {
            throw new DistributedApplicationException($"The '{builder.Resource.Name}' resource cannot wait for its parent '{dependency.Resource.Name}'.");
        }

        if (dependency.Resource is IResourceWithParent dependencyResourceWithParent)
        {
            // If the dependency resource is a child resource we automatically apply
            // the WaitForStart to the parent resource. This caters for situations where
            // the child resource itself does not have any health checks setup.
            var parentBuilder = builder.ApplicationBuilder.CreateResourceBuilder(dependencyResourceWithParent.Parent);

            // Waiting for the parent is an internal implementation detail. Don't add a relationship here.
            builder.WaitForStartCore(parentBuilder, waitBehavior, addRelationship: false);
        }

        // Wait for any referenced resources in the connection string.
        if (dependency.Resource is ConnectionStringResource cs)
        {
            // We only look at top level resources with the assumption that they are transitive themselves.
            foreach (var referencedResource in cs.ConnectionStringExpression.ValueProviders.OfType<IResource>())
            {
                builder.WaitForStartCore(builder.ApplicationBuilder.CreateResourceBuilder(referencedResource), waitBehavior, addRelationship: false);
            }
        }

        if (addRelationship)
        {
            builder.WithRelationship(dependency.Resource, KnownRelationshipTypes.WaitFor);
        }

        return builder.WithAnnotation(new WaitAnnotation(dependency.Resource, WaitType.WaitUntilStarted) { WaitBehavior = waitBehavior });
    }

    /// <summary>
    /// Adds a <see cref="ExplicitStartupAnnotation" /> annotation to the resource so it doesn't automatically start
    /// with the app host startup.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>This method is useful when a resource shouldn't automatically start when the app host starts.</para>
    /// <example>
    /// The database clean up tool project isn't started with the app host.
    /// The resource start command can be used to run it ondemand later.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var pgsql = builder.AddPostgres("postgres");
    /// builder.AddProject&lt;Projects.CleanUpDatabase&gt;("dbcleanuptool")
    ///        .WithReference(pgsql)
    ///        .WithExplicitStart();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithExplicitStart<T>(this IResourceBuilder<T> builder) where T : IResource
    {
        return builder.WithAnnotation(new ExplicitStartupAnnotation());
    }

    /// <summary>
    /// Waits for the dependency resource to enter the Exited or Finished state before starting the resource.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder for the resource that will be waiting.</param>
    /// <param name="dependency">The resource builder for the dependency resource.</param>
    /// <param name="exitCode">The exit code which is interpreted as successful.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>This method is useful when a resource should wait until another has completed. A common usage pattern
    /// would be to include a console application that initializes the database schema or performs other one off
    /// initialization tasks.</para>
    /// <para>Note that this method has no impact at deployment time and only works for local development.</para>
    /// <example>
    /// Wait for database initialization app to complete running.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var pgsql = builder.AddPostgres("postgres");
    /// var dbprep = builder.AddProject&lt;Projects.DbPrepApp&gt;("dbprep")
    ///                     .WithReference(pgsql);
    /// builder.AddProject&lt;Projects.DatabasePrepTool&gt;("dbpreptool")
    ///        .WithReference(pgsql)
    ///        .WaitForCompletion(dbprep);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WaitForCompletion<T>(this IResourceBuilder<T> builder, IResourceBuilder<IResource> dependency, int exitCode = 0) where T : IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dependency);

        if (builder.Resource as IResource == dependency.Resource)
        {
            throw new DistributedApplicationException($"The '{builder.Resource.Name}' resource cannot wait for itself.");
        }

        if (builder.Resource is IResourceWithParent resourceWithParent && resourceWithParent.Parent == dependency.Resource)
        {
            throw new DistributedApplicationException($"The '{builder.Resource.Name}' resource cannot wait for its parent '{dependency.Resource.Name}'.");
        }

        builder.WithRelationship(dependency.Resource, KnownRelationshipTypes.WaitFor);

        return builder.WithAnnotation(new WaitAnnotation(dependency.Resource, WaitType.WaitForCompletion, exitCode));
    }

    /// <summary>
    /// Adds a <see cref="HealthCheckAnnotation"/> to the resource annotations to associate a resource with a named health check managed by the health check service.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="key">The key for the health check.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The <see cref="WithHealthCheck{T}(IResourceBuilder{T}, string)"/> method is used in conjunction with
    /// the <see cref="WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/> to associate a resource
    /// registered in the application hosts dependency injection container. The <see cref="WithHealthCheck{T}(IResourceBuilder{T}, string)"/>
    /// method does not inject the health check itself it is purely an association mechanism.
    /// </para>
    /// <example>
    /// Define a custom health check and associate it with a resource.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var startAfter = DateTime.Now.AddSeconds(30);
    ///
    /// builder.Services.AddHealthChecks().AddCheck(mycheck", () =>
    /// {
    ///     return DateTime.Now > startAfter ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
    /// });
    ///
    /// var pg = builder.AddPostgres("pg")
    ///                 .WithHealthCheck("mycheck");
    ///
    /// builder.AddProject&lt;Projects.MyApp&gt;("myapp")
    ///        .WithReference(pg)
    ///        .WaitFor(pg); // This will result in waiting for the building check, and the
    ///                      // custom check defined in the code.
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithHealthCheck<T>(this IResourceBuilder<T> builder, string key) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(key);

        if (builder.Resource.TryGetAnnotationsOfType<HealthCheckAnnotation>(out var annotations) && annotations.Any(a => a.Key == key))
        {
            throw new DistributedApplicationException($"Resource '{builder.Resource.Name}' already has a health check with key '{key}'.");
        }

        builder.WithAnnotation(new HealthCheckAnnotation(key));

        return builder;
    }

    /// <summary>
    /// Adds a health check to the resource which is mapped to a specific endpoint.
    /// </summary>
    /// <typeparam name="T">A resource type that implements <see cref="IResourceWithEndpoints" />.</typeparam>
    /// <param name="builder">A resource builder.</param>
    /// <param name="path">The relative path to test.</param>
    /// <param name="statusCode">The result code to interpret as healthy.</param>
    /// <param name="endpointName">The name of the endpoint to derive the base address from.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method adds a health check to the health check service which polls the specified endpoint on the resource
    /// on a periodic basis. The base address is dynamically determined based on the endpoint that was selected. By
    /// default the path is set to "/" and the status code is set to 200.
    /// </para>
    /// <example>
    /// This example shows adding an HTTP health check to a backend project.
    /// The health check makes sure that the front end does not start until the backend is
    /// reporting a healthy status based on the return code returned from the
    /// "/health" path on the backend server.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var backend = builder.AddProject&lt;Projects.Backend&gt;("backend")
    ///                      .WithHttpHealthCheck("/health");
    /// builder.AddProject&lt;Projects.Frontend&gt;("frontend")
    ///        .WithReference(backend).WaitFor(backend);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithHttpHealthCheck<T>(this IResourceBuilder<T> builder, string? path = null, int? statusCode = null, string? endpointName = null) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        var endpointSelector = endpointName is not null
            ? NamedEndpointSelector(builder, [endpointName], "HTTP health check")
            : NamedEndpointSelector(builder, s_httpSchemes, "HTTP health check");

        return WithHttpHealthCheck(builder, endpointSelector, path, statusCode);
    }

    /// <summary>
    /// Adds a health check to the resource which is mapped to a specific endpoint.
    /// </summary>
    /// <typeparam name="T">A resource type that implements <see cref="IResourceWithEndpoints" />.</typeparam>
    /// <param name="builder">A resource builder.</param>
    /// <param name="endpointSelector"></param>
    /// <param name="path">The relative path to test.</param>
    /// <param name="statusCode">The result code to interpret as healthy.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method adds a health check to the health check service which polls the specified endpoint on a periodic basis.
    /// The base address is dynamically determined based on the endpoint that was selected. By default the path is set to "/"
    /// and the status code is set to 200.
    /// </para>
    /// <example>
    /// This example shows adding an HTTP health check to a backend project.
    /// The health check makes sure that the front end does not start until the backend is
    /// reporting a healthy status based on the return code returned from the
    /// "/health" path on the backend server.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var backend = builder.AddProject&lt;Projects.Backend&gt;("backend");
    /// backend.WithHttpHealthCheck(() => backend.GetEndpoint("https"), path: "/health")
    /// builder.AddProject&lt;Projects.Frontend&gt;("frontend")
    ///        .WithReference(backend).WaitFor(backend);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithHttpHealthCheck<T>(this IResourceBuilder<T> builder, Func<EndpointReference>? endpointSelector, string? path = null, int? statusCode = null) where T : IResourceWithEndpoints
    {
        endpointSelector ??= DefaultEndpointSelector(builder);

        var endpoint = endpointSelector()
            ?? throw new DistributedApplicationException($"Could not create HTTP health check for resource '{builder.Resource.Name}' as the endpoint selector returned null.");

        if (endpoint.Scheme != "http" && endpoint.Scheme != "https")
        {
            throw new DistributedApplicationException($"Could not create HTTP health check for resource '{builder.Resource.Name}' as the endpoint with name '{endpoint.EndpointName}' and scheme '{endpoint.Scheme}' is not an HTTP endpoint.");
        }

        path ??= "/";
        statusCode ??= 200;

        var endpointName = endpoint.EndpointName;

        builder.OnResourceEndpointsAllocated((_, @event, ct) =>
        {
            if (!endpoint.Exists)
            {
                throw new DistributedApplicationException($"The endpoint '{endpointName}' does not exist on the resource '{builder.Resource.Name}'.");
            }

            return Task.CompletedTask;
        });

        Uri? uri = null;
        builder.OnBeforeResourceStarted((_, @event, ct) =>
        {
            var baseUri = new Uri(endpoint.Url, UriKind.Absolute);
            uri = new Uri(baseUri, path);
            return Task.CompletedTask;
        });

        var healthCheckKey = $"{builder.Resource.Name}_{endpointName}_{path}_{statusCode}_check";

        builder.ApplicationBuilder.Services.SuppressHealthCheckHttpClientLogging(healthCheckKey);

        builder.ApplicationBuilder.Services.AddHealthChecks().AddUrlGroup(options =>
        {
            if (uri is null)
            {
                throw new DistributedApplicationException($"The URI for the health check is not set. Ensure that the resource has been allocated before the health check is executed.");
            }

            options.AddUri(uri, setup => setup.ExpectHttpCode(statusCode ?? 200));
        }, healthCheckKey);

        builder.WithHealthCheck(healthCheckKey);

        return builder;
    }

    /// <summary>
    /// Adds a health check to the resource which is mapped to a specific endpoint.
    /// </summary>
    /// <typeparam name="T">A resource type that implements <see cref="IResourceWithEndpoints" />.</typeparam>
    /// <param name="builder">A resource builder.</param>
    /// <param name="path">The relative path to test.</param>
    /// <param name="statusCode">The result code to interpret as healthy.</param>
    /// <param name="endpointName">The name of the endpoint to derive the base address from.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method adds a health check to the health check service which polls the specified endpoint on the resource
    /// on a periodic basis. The base address is dynamically determined based on the endpoint that was selected. By
    /// default the path is set to "/" and the status code is set to 200.
    /// </para>
    /// <example>
    /// This example shows adding an HTTPS health check to a backend project.
    /// The health check makes sure that the front end does not start until the backend is
    /// reporting a healthy status based on the return code returned from the
    /// "/health" path on the backend server.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var backend = builder.AddProject&lt;Projects.Backend&gt;("backend")
    ///                      .WithHttpsHealthCheck("/health");
    /// builder.AddProject&lt;Projects.Frontend&gt;("frontend")
    ///        .WithReference(backend).WaitFor(backend);
    /// </code>
    /// </example>
    /// </remarks>
    [Obsolete("This method is obsolete and will be removed in a future version. Use the WithHttpHealthCheck method instead.")]
    public static IResourceBuilder<T> WithHttpsHealthCheck<T>(this IResourceBuilder<T> builder, string? path = null, int? statusCode = null, string? endpointName = null) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithHttpHealthCheck(path, statusCode, endpointName ?? "https");
    }

    /// <summary>
    /// Adds a <see cref="ResourceCommandAnnotation"/> to the resource annotations to add a resource command.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the command. The name uniquely identifies the command.</param>
    /// <param name="displayName">The display name visible in UI.</param>
    /// <param name="executeCommand">
    /// A callback that is executed when the command is executed. The callback is run inside the .NET Aspire host.
    /// The callback result is used to indicate success or failure in the UI.
    /// </param>
    /// <param name="commandOptions">Optional configuration for the command.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>The <c>WithCommand</c> method is used to add commands to the resource. Commands are displayed in the dashboard
    /// and can be executed by a user using the dashboard UI.</para>
    /// <para>When a command is executed, the <paramref name="executeCommand"/> callback is called and is run inside the .NET Aspire host.</para>
    /// </remarks>
    [OverloadResolutionPriority(1)]
    public static IResourceBuilder<T> WithCommand<T>(
        this IResourceBuilder<T> builder,
        string name,
        string displayName,
        Func<ExecuteCommandContext, Task<ExecuteCommandResult>> executeCommand,
        CommandOptions? commandOptions = null) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(executeCommand);

        commandOptions ??= CommandOptions.Default;

        // Replace existing annotation with the same name.
        var existingAnnotation = builder.Resource.Annotations.OfType<ResourceCommandAnnotation>().SingleOrDefault(a => a.Name == name);
        if (existingAnnotation is not null)
        {
            builder.Resource.Annotations.Remove(existingAnnotation);
        }

        return builder.WithAnnotation(new ResourceCommandAnnotation(name, displayName, commandOptions.UpdateState ?? (c => ResourceCommandState.Enabled), executeCommand, commandOptions.Description, commandOptions.Parameter, commandOptions.ConfirmationMessage, commandOptions.IconName, commandOptions.IconVariant, commandOptions.IsHighlighted));
    }

    /// <summary>
    /// Adds a <see cref="ResourceCommandAnnotation"/> to the resource annotations to add a resource command.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the command. The name uniquely identifies the command.</param>
    /// <param name="displayName">The display name visible in UI.</param>
    /// <param name="executeCommand">
    /// A callback that is executed when the command is executed. The callback is run inside the .NET Aspire host.
    /// The callback result is used to indicate success or failure in the UI.
    /// </param>
    /// <param name="updateState">
    /// <para>A callback that is used to update the command state. The callback is executed when the command's resource snapshot is updated.</para>
    /// <para>If a callback isn't specified, the command is always enabled.</para>
    /// </param>
    /// <param name="displayDescription">
    /// Optional description of the command, to be shown in the UI.
    /// Could be used as a tooltip. May be localized.
    /// </param>
    /// <param name="parameter">
    /// Optional parameter that configures the command in some way.
    /// Clients must return any value provided by the server when invoking the command.
    /// </param>
    /// <param name="confirmationMessage">
    /// When a confirmation message is specified, the UI will prompt with an OK/Cancel dialog
    /// and the confirmation message before starting the command.
    /// </param>
    /// <param name="iconName">The icon name for the command. The name should be a valid FluentUI icon name from <see href="https://aka.ms/fluentui-system-icons"/></param>
    /// <param name="iconVariant">The icon variant.</param>
    /// <param name="isHighlighted">A flag indicating whether the command is highlighted in the UI.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>The <c>WithCommand</c> method is used to add commands to the resource. Commands are displayed in the dashboard
    /// and can be executed by a user using the dashboard UI.</para>
    /// <para>When a command is executed, the <paramref name="executeCommand"/> callback is called and is run inside the .NET Aspire host.</para>
    /// </remarks>
    [Obsolete("This method is obsolete and will be removed in a future version. Use the overload that accepts a CommandOptions instance instead.")]
    public static IResourceBuilder<T> WithCommand<T>(
        this IResourceBuilder<T> builder,
        string name,
        string displayName,
        Func<ExecuteCommandContext, Task<ExecuteCommandResult>> executeCommand,
        Func<UpdateCommandStateContext, ResourceCommandState>? updateState = null,
        string? displayDescription = null,
        object? parameter = null,
        string? confirmationMessage = null,
        string? iconName = null,
        IconVariant? iconVariant = null,
        bool isHighlighted = false) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(executeCommand);

        // Replace existing annotation with the same name.
        var existingAnnotation = builder.Resource.Annotations.OfType<ResourceCommandAnnotation>().SingleOrDefault(a => a.Name == name);
        if (existingAnnotation != null)
        {
            builder.Resource.Annotations.Remove(existingAnnotation);
        }

        return builder.WithAnnotation(new ResourceCommandAnnotation(name, displayName, updateState ?? (c => ResourceCommandState.Enabled), executeCommand, displayDescription, parameter, confirmationMessage, iconName, iconVariant, isHighlighted));
    }

    /// <summary>
    /// Adds a command to the resource that when invoked sends an HTTP request to the specified endpoint and path.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="path">The path to send the request to when the command is invoked.</param>
    /// <param name="displayName">The display name visible in UI.</param>
    /// <param name="endpointName">The name of the HTTP endpoint on this resource to send the request to when the command is invoked.</param>
    /// <param name="commandName">Optional name of the command. The name uniquely identifies the command. If a name isn't specified then it's inferred using the command's endpoint and HTTP method.</param>
    /// <param name="commandOptions">Optional configuration for the command.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The command will be added to the resource represented by <paramref name="builder"/>.
    /// </para>
    /// <para>
    /// If <paramref name="endpointName"/> is specified, the request will be sent to the endpoint with that name on the resource represented by <paramref name="builder"/>.
    /// If an endpoint with that name is not found, or the endpoint with that name is not an HTTP endpoint, an exception will be thrown.
    /// </para>
    /// <para>
    /// If no <paramref name="endpointName"/> is specified, the first HTTP endpoint found on the resource will be used.
    /// HTTP endpoints with an <c>https</c> scheme are preferred over those with an <c>http</c> scheme. If no HTTP endpoint
    /// is found on the resource, an exception will be thrown.
    /// </para>
    /// <para>
    /// The command will not be enabled until the endpoint is allocated and the resource the endpoint is associated with is healthy.
    /// </para>
    /// <para>
    /// If <see cref="HttpCommandOptions.Method"/> is not specified, <c>POST</c> will be used.
    /// </para>
    /// <para>
    /// Specifying <see cref="HttpCommandOptions.HttpClientName"/> will use that named <see cref="HttpClient"/> when sending the request. This allows you to configure the <see cref="HttpClient"/>
    /// instance with a specific handler or other options using <see cref="HttpClientFactoryServiceCollectionExtensions.AddHttpClient(IServiceCollection, string)"/>.
    /// If <see cref="HttpCommandOptions.HttpClientName"/> is not specified, the default <see cref="HttpClient"/> will be used.
    /// </para>
    /// <para>
    /// The <see cref="HttpCommandOptions.PrepareRequest"/> callback will be invoked to configure the request before it is sent. This can be used to add headers or a request payload
    /// before the request is sent.
    /// </para>
    /// <para>
    /// The <see cref="HttpCommandOptions.GetCommandResult"/> callback will be invoked after the response is received to determine the result of the command invocation. If this callback
    /// is not specified, the command will be considered succesful if the response status code is in the 2xx range.
    /// </para>
    /// <example>
    /// Adds a command to the project resource that when invoked sends an HTTP POST request to the path <c>/clear-cache</c>.
    /// <code lang="csharp">
    /// var apiService = builder.AddProject&gt;MyApiService&gt;("api")
    ///     .WithHttpCommand("/clear-cache", "Clear cache");
    /// </code>
    /// </example>
    /// <example>
    /// Adds a command to the project resource that when invoked sends an HTTP GET request to the path <c>/reset-db</c> on endpoint named <c>admin</c>.
    /// The request's headers are configured to include an <c>X-Admin-Key</c> header for verification.
    /// <code lang="csharp">
    /// var adminKey = builder.AddParameter("admin-key");
    /// var apiService = builder.AddProject&gt;MyApiService&gt;("api")
    ///     .WithHttpsEndpoint("admin")
    ///     .WithEnvironment("ADMIN_KEY", adminKey)
    ///     .WithHttpCommand("/reset-db", "Reset database",
    ///                      endpointName: "admin",
    ///                      commandOptions: new ()
    ///                      {
    ///                         Method = HttpMethod.Get,
    ///                         ConfirmationMessage = "Are you sure you want to reset the database?",
    ///                         PrepareRequest: request =>
    ///                         {
    ///                             request.Headers.Add("X-Admin-Key", adminKey);
    ///                             return Task.CompletedTask;
    ///                         }
    ///                      });
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<TResource> WithHttpCommand<TResource>(
        this IResourceBuilder<TResource> builder,
        string path,
        string displayName,
        [EndpointName] string? endpointName = null,
        string? commandName = null,
        HttpCommandOptions? commandOptions = null)
        where TResource : IResourceWithEndpoints
        => builder.WithHttpCommand(
            path: path,
            displayName: displayName,
            endpointSelector: endpointName is not null
                ? NamedEndpointSelector(builder, [endpointName], "HTTP command")
                : NamedEndpointSelector(builder, s_httpSchemes, "HTTP command"),
            commandName: commandName,
            commandOptions: commandOptions);

    /// <summary>
    /// Adds a command to the resource that when invoked sends an HTTP request to the specified endpoint and path.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="path">The path to send the request to when the command is invoked.</param>
    /// <param name="displayName">The display name visible in UI.</param>
    /// <param name="endpointSelector">A callback that selects the HTTP endpoint to send the request to when the command is invoked.</param>
    /// <param name="commandOptions">Optional configuration for the command.</param>
    /// <param name="commandName">The name of command. The name uniquely identifies the command.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="DistributedApplicationException"></exception>
    /// <remarks>
    /// <para>
    /// The command will be added to the resource represented by <paramref name="builder"/>.
    /// </para>
    /// <para>
    /// If no <see cref="HttpCommandOptions.EndpointSelector"/> is specified, the first HTTP endpoint found on the resource will be used.
    /// HTTP endpoints with an <c>https</c> scheme are preferred over those with an <c>http</c> scheme. If no HTTP endpoint
    /// is found on the resource, an exception will be thrown.
    /// </para>
    /// <para>
    /// The supplied <see cref="HttpCommandOptions.EndpointSelector"/> may return an endpoint from a different resource to that which the command is being added to.
    /// </para>
    /// <para>
    /// The command will not be enabled until the endpoint is allocated and the resource the endpoint is associated with is healthy.
    /// </para>
    /// <para>
    /// If <see cref="HttpCommandOptions.Method"/> is not specified, <c>POST</c> will be used.
    /// </para>
    /// <para>
    /// Specifying a <see cref="HttpCommandOptions.HttpClientName"/> will use that named <see cref="HttpClient"/> when sending the request. This allows you to configure the <see cref="HttpClient"/>
    /// instance with a specific handler or other options using <see cref="HttpClientFactoryServiceCollectionExtensions.AddHttpClient(IServiceCollection, string)"/>.
    /// If no <see cref="HttpCommandOptions.HttpClientName"/> is specified, the default <see cref="HttpClient"/> will be used.
    /// </para>
    /// <para>
    /// The <see cref="HttpCommandOptions.PrepareRequest"/> callback will be invoked to configure the request before it is sent. This can be used to add headers or a request payload
    /// before the request is sent.
    /// </para>
    /// <para>
    /// The <see cref="HttpCommandOptions.GetCommandResult"/> callback will be invoked after the response is received to determine the result of the command invocation. If this callback
    /// is not specified, the command will be considered succesful if the response status code is in the 2xx range.
    /// </para>
    /// <example>
    /// Adds commands to a project resource that when invoked sends an HTTP POST request to an endpoint on a separate load generator resource, to generate load against the
    /// resource the command was executed against.
    /// <code lang="csharp">
    /// var loadGenerator = builder.AddProject&gt;LoadGenerator&gt;("load");
    /// var loadGeneratorEndpoint = loadGenerator.GetEndpoint("https");
    /// var customerService = builder.AddProject&gt;CustomerService&gt;("customer-service")
    ///     .WithHttpCommand("/stress?resource=customer-service&amp;requests=1000", "Apply load (1000)", endpointSelector: () => loadGeneratorEndpoint)
    ///     .WithHttpCommand("/stress?resource=customer-service&amp;requests=5000", "Apply load (5000)", endpointSelector: () => loadGeneratorEndpoint);
    /// loadGenerator.WithReference(customerService);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<TResource> WithHttpCommand<TResource>(
        this IResourceBuilder<TResource> builder,
        string path,
        string displayName,
        Func<EndpointReference>? endpointSelector,
        string? commandName = null,
        HttpCommandOptions? commandOptions = null)
        where TResource : IResourceWithEndpoints
    {
        endpointSelector ??= DefaultEndpointSelector(builder);

        var endpoint = endpointSelector()
            ?? throw new DistributedApplicationException($"Could not create HTTP command for resource '{builder.Resource.Name}' as the endpoint selector returned null.");

        if (endpoint.Scheme != "http" && endpoint.Scheme != "https")
        {
            throw new DistributedApplicationException($"Could not create HTTP command for resource '{builder.Resource.Name}' as the endpoint with name '{endpoint.EndpointName}' and scheme '{endpoint.Scheme}' is not an HTTP endpoint.");
        }

        builder.ApplicationBuilder.Services.AddHttpClient();

        commandOptions ??= HttpCommandOptions.Default;
        commandOptions.Method ??= HttpMethod.Post;

        commandName ??= $"{endpoint.Resource.Name}-{endpoint.EndpointName}-http-{commandOptions.Method.Method.ToLowerInvariant()}-{path}";

        if (commandOptions.UpdateState is null)
        {
            var targetRunning = false;
            builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((e, ct) =>
            {
                var rns = e.Services.GetRequiredService<ResourceNotificationService>();
                _ = Task.Run(async () =>
                {
                    await foreach (var resourceEvent in rns.WatchAsync(ct).WithCancellation(ct))
                    {
                        if (resourceEvent.Resource == endpoint.Resource)
                        {
                            var resourceState = resourceEvent.Snapshot.State?.Text;
                            targetRunning = resourceState == KnownResourceStates.Running || resourceState == KnownResourceStates.RuntimeUnhealthy;
                        }
                    }
                }, ct);

                return Task.CompletedTask;
            });
            commandOptions.UpdateState = context => targetRunning ? ResourceCommandState.Enabled : ResourceCommandState.Disabled;
        }

        builder.WithCommand(commandName, displayName,
            async context =>
            {
                if (!endpoint.IsAllocated)
                {
                    return new ExecuteCommandResult { Success = false, ErrorMessage = "Endpoints are not yet allocated." };
                }

                var uri = new UriBuilder(endpoint.Url) { Path = path }.Uri;
                var httpClient = context.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(commandOptions.HttpClientName ?? Options.DefaultName);
                var request = new HttpRequestMessage(commandOptions.Method, uri);
                if (commandOptions.PrepareRequest is not null)
                {
                    var requestContext = new HttpCommandRequestContext
                    {
                        ServiceProvider = context.ServiceProvider,
                        ResourceName = context.ResourceName,
                        Endpoint = endpoint,
                        CancellationToken = context.CancellationToken,
                        HttpClient = httpClient,
                        Request = request
                    };
                    await commandOptions.PrepareRequest(requestContext).ConfigureAwait(false);
                }
                try
                {
                    var response = await httpClient.SendAsync(request, context.CancellationToken).ConfigureAwait(false);
                    if (commandOptions.GetCommandResult is not null)
                    {
                        var resultContext = new HttpCommandResultContext
                        {
                            ServiceProvider = context.ServiceProvider,
                            ResourceName = context.ResourceName,
                            Endpoint = endpoint,
                            CancellationToken = context.CancellationToken,
                            HttpClient = httpClient,
                            Response = response
                        };
                        return await commandOptions.GetCommandResult(resultContext).ConfigureAwait(false);
                    }

                    return response.IsSuccessStatusCode
                        ? CommandResults.Success()
                        : CommandResults.Failure($"Request failed with status code {response.StatusCode}");
                }
                catch (Exception ex)
                {
                    return CommandResults.Failure(ex);
                }
            },
            commandOptions);

        return builder;
    }

    /// <summary>
    /// Adds a <see cref="CertificateAuthorityCollectionAnnotation"/> to the resource annotations to associate a certificate authority collection with the resource.
    /// This is used to configure additional trusted certificate authorities for the resource.
    /// Custom certificate trust is only applied in run mode; in publish mode resources will use their default certificate trust behavior.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="certificateAuthorityCollection">Additional certificates in a <see cref="CertificateAuthorityCollection"/> to treat as trusted certificate authorities for the resource.</param>
    /// <returns>The <see cref="IResourceBuilder{TResource}"/>.</returns>
    /// <remarks>
    /// <example>
    /// Add a certificate authority collection to a container resource.
    /// <code lang="csharp">
    /// var caCollection = builder.AddCertificateAuthorityCollection("my-cas")
    ///     .WithCertificatesFromFile("../my-ca.pem");
    ///
    /// var container = builder.AddContainer("my-service", "my-service:latest")
    ///     .WithCertificateAuthorityCollection(caCollection);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<TResource> WithCertificateAuthorityCollection<TResource>(this IResourceBuilder<TResource> builder, IResourceBuilder<CertificateAuthorityCollection> certificateAuthorityCollection)
        where TResource : IResourceWithEnvironment, IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(certificateAuthorityCollection);

        var annotation = new CertificateAuthorityCollectionAnnotation
        {
            CertificateAuthorityCollections = { certificateAuthorityCollection.Resource },
        };
        if (builder.Resource.TryGetLastAnnotation<CertificateAuthorityCollectionAnnotation>(out var existingAnnotation))
        {
            foreach (var existingCollection in existingAnnotation.CertificateAuthorityCollections)
            {
                if (existingCollection != certificateAuthorityCollection.Resource)
                {
                    annotation.CertificateAuthorityCollections.Add(existingCollection);
                }
            }
            annotation.TrustDeveloperCertificates ??= existingAnnotation.TrustDeveloperCertificates;
            annotation.Scope ??= existingAnnotation.Scope;
        }

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Indicates whether developer certificates should be treated as trusted certificate authorities for the resource at run time.
    /// Currently this indicates trust for the ASP.NET Core developer certificate. The developer certificate will only be trusted
    /// when running in local development scenarios; in publish mode resources will use their default certificate trust.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="trust">Indicates whether the developer certificate should be treated as trusted.</param>
    /// <returns>The <see cref="IResourceBuilder{TResource}"/>.</returns>
    /// <remarks>
    /// <example>
    /// Disable trust for app host managed developer certificate(s) for a container resource.
    /// <code lang="csharp">
    /// var container = builder.AddContainer("my-service", "my-service:latest")
    ///     .WithDeveloperCertificateTrust(false);
    /// </code>
    /// </example>
    /// <example>
    /// Disable automatic trust for app host managed developer certificate(s), but explicitly enable it for a specific resource.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions()
    /// {
    ///     Args = args,
    ///     TrustDeveloperCertificate = false,
    /// });
    /// var project = builder.AddProject&lt;MyService&gt;("my-service")
    ///    .WithDeveloperCertificateTrust(true);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<TResource> WithDeveloperCertificateTrust<TResource>(this IResourceBuilder<TResource> builder, bool trust)
        where TResource : IResourceWithEnvironment, IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);

        var annotation = new CertificateAuthorityCollectionAnnotation
        {
            TrustDeveloperCertificates = trust,
        };
        if (builder.Resource.TryGetLastAnnotation<CertificateAuthorityCollectionAnnotation>(out var existingAnnotation))
        {
            annotation.CertificateAuthorityCollections.AddRange(existingAnnotation.CertificateAuthorityCollections);
            annotation.TrustDeveloperCertificates ??= existingAnnotation.TrustDeveloperCertificates;
            annotation.Scope ??= existingAnnotation.Scope;
        }

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Sets the <see cref="CertificateTrustScope"/> for custom certificate authorities associated with the resource. The scope
    /// specifies how custom certificate authorities should be applied to a resource at run time in local development scenarios.
    /// Custom certificate trust is only applied in run mode; in publish mode resources will use their default certificate trust behavior.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="scope">The scope to apply to custom certificate authorities associated with the resource.</param>
    /// <returns>The <see cref="IResourceBuilder{TResource}"/>.</returns>
    /// <remarks>
    /// The default scope if not overridden is <see cref="CertificateTrustScope.Append"/> which means that custom certificate
    /// authorities should be appended to the default trusted certificate authorities for the resource. Setting the scope to
    /// <see cref="CertificateTrustScope.Override"/> indicates the set of certificates in referenced
    /// <see cref="CertificateAuthorityCollection"/> (and optionally Aspire developer certificiates) should be used as the
    /// exclusive source of trust for a resource.
    /// In all cases, this is a best effort implementation as not all resources support full customization of certificate
    /// trust.
    /// <example>
    /// Set the scope for custom certificate authorities to override the default trusted certificate authorities for a container resource.
    /// <code lang="csharp">
    /// var caCollection = builder.AddCertificateAuthorityCollection("my-cas")
    ///     .WithCertificate(new X509Certificate2("my-ca.pem"));
    ///
    /// var container = builder.AddContainer("my-service", "my-service:latest")
    ///     .WithCertificateAuthorityCollection(caCollection)
    ///     .WithCertificateTrustScope(CertificateTrustScope.Override);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<TResource> WithCertificateTrustScope<TResource>(this IResourceBuilder<TResource> builder, CertificateTrustScope scope)
        where TResource : IResourceWithEnvironment, IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);

        var annotation = new CertificateAuthorityCollectionAnnotation
        {
            Scope = scope,
        };
        if (builder.Resource.TryGetLastAnnotation<CertificateAuthorityCollectionAnnotation>(out var existingAnnotation))
        {
            annotation.CertificateAuthorityCollections.AddRange(existingAnnotation.CertificateAuthorityCollections);
            annotation.TrustDeveloperCertificates ??= existingAnnotation.TrustDeveloperCertificates;
            annotation.Scope ??= existingAnnotation.Scope;
        }

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Adds a <see cref="CertificateTrustConfigurationCallbackAnnotation"/> to the resource annotations to associate a callback that
    /// is invoked when a resource needs to configure itself for custom certificate trust. May be called multiple times to register
    /// additional callbacks to append additional configuration.
    /// Custom certificate trust is only applied in run mode; in publish mode resources will use their default certificate trust behavior.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback to invoke when a resource needs to configure itself for custom certificate trust.</param>
    /// <returns>The updated resource builder.</returns>
    /// <remarks>
    /// <example>
    /// Add an environment variable that needs to reference the path to the certificate bundle for the container resource:
    /// <code lang="csharp">
    /// var container = builder.AddContainer("my-service", "my-service:latest")
    ///     .WithCertificateTrustConfigurationCallback(ctx =>
    ///     {
    ///         if (ctx.Scope != CertificateTrustScope.Append)
    ///         {
    ///             ctx.EnvironmentVariables["CUSTOM_CERTS_BUNDLE_ENV"] = ctx.CertificateBundlePath;
    ///         }
    ///         ctx.EnvironmentVariables["ADDITIONAL_CERTS_DIR_ENV"] = ctx.CertificateDirectoriesPath;
    ///     });
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<TResource> WithCertificateTrustConfiguration<TResource>(this IResourceBuilder<TResource> builder, Func<CertificateTrustConfigurationCallbackAnnotationContext, Task> callback)
        where TResource : IResourceWithArgs, IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new CertificateTrustConfigurationCallbackAnnotation(callback), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Indicates that a resource should use the developer certificate key pair for HTTPS endpoints at run time.
    /// Currently this indicates use of the ASP.NET Core developer certificate. The developer certificate will only be used
    /// when running in local development scenarios; in publish mode resources will use their default certificate configuration.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="password">A parameter specifying the password used to encrypt the certificate private key.</param>
    /// <returns>The <see cref="IResourceBuilder{TResource}"/>.</returns>
    /// <remarks>
    /// <example>
    /// Use the developer certificate for HTTPS/TLS endpoints on a container resource:
    /// <code lang="csharp">
    /// builder.AddContainer("my-service", "my-image")
    ///     .WithHttpsDeveloperCertificate()
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<TResource> WithHttpsDeveloperCertificate<TResource>(this IResourceBuilder<TResource> builder, IResourceBuilder<ParameterResource>? password = null)
        where TResource : IResourceWithEnvironment, IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);

        var annotation = new HttpsCertificateAnnotation
        {
            UseDeveloperCertificate = true,
            Password = password?.Resource,
        };

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Adds a <see cref="HttpsCertificateAnnotation"/> to the resource annotations to associate an X.509 certificate key pair with the resource.
    /// This is used to configure the certificate presented by the resource for HTTPS/TLS endpoints.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="certificate">An <see cref="X509Certificate2"/> key pair to use for HTTPS/TLS endpoints on the resource.</param>
    /// <param name="password">A parameter specifying the password used to encrypt the certificate private key.</param>
    /// <returns>The <see cref="IResourceBuilder{TResource}"/>.</returns>
    /// <remarks>
    /// <example>
    /// Use a custom certificate for HTTPS/TLS endpoints on a container resource:
    /// <code lang="csharp">
    /// var certificate = new X509Certificate2("path/to/certificate.pfx", "password");
    /// builder.AddContainer("my-service", "my-image")
    ///    .WithHttpsCertificate(certificate);
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<TResource> WithHttpsCertificate<TResource>(this IResourceBuilder<TResource> builder, X509Certificate2 certificate, IResourceBuilder<ParameterResource>? password = null)
        where TResource : IResourceWithEnvironment, IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);

        var annotation = new HttpsCertificateAnnotation
        {
            Certificate = certificate,
            Password = password?.Resource,
        };

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Disable HTTPS/TLS server certificate configuration for the resource. No HTTPS/TLS termination configuration will be applied.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{TResource}"/>.</returns>
    /// <remarks>
    /// <example>
    /// Disable HTTPS certificate configuration for a Redis resource:
    /// <code lang="csharp">
    /// var redis = builder.AddRedis("cache")
    ///     .WithoutHttpsCertificate();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<TResource> WithoutHttpsCertificate<TResource>(this IResourceBuilder<TResource> builder)
        where TResource : IResourceWithEnvironment, IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);

        var annotation = new HttpsCertificateAnnotation
        {
            Certificate = null,
            UseDeveloperCertificate = false,
        };

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Adds a callback that allows configuring the resource to use a specific HTTPS/TLS certificate key pair for server authentication.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback to configure the resource to use a certificate key pair.</param>
    /// <returns>The updated resource builder.</returns>
    /// <remarks>
    /// <example>
    /// Pass the path to the PFX certificate file to the container arguments.
    /// <code lang="csharp">
    /// builder.AddContainer("my-service", "my-image")
    ///     .WithHttpsCertificateConfiguration(ctx =>
    ///     {
    ///         ctx.Arguments.Add("--https-certificate-path");
    ///         ctx.Arguments.Add(ctx.PfxPath);
    ///         return Task.CompletedTask;
    ///     });
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<TResource> WithHttpsCertificateConfiguration<TResource>(this IResourceBuilder<TResource> builder, Func<HttpsCertificateConfigurationCallbackAnnotationContext, Task> callback)
        where TResource : IResourceWithEnvironment, IResourceWithArgs
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        var annotation = new HttpsCertificateConfigurationCallbackAnnotation(callback);

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Append);
    }

    // These match the default endpoint names resulting from calling WithHttpsEndpoint or WithHttpEndpoint as well as the defaults
    // created for ASP.NET Core projects with the default launch settings added via AddProject. HTTPS is first so that we prefer it
    // if found.
    private static readonly string[] s_httpSchemes = ["https", "http"];

    private static Func<EndpointReference> NamedEndpointSelector<TResource>(IResourceBuilder<TResource> builder, string[] endpointNames, string errorDisplayNoun)
        where TResource : IResourceWithEndpoints
        => () =>
        {
            // Find a matching endpoint using those names and if not an HTTP endpoint or not found throw an exception.
            var endpoints = builder.Resource.GetEndpoints();
            EndpointReference? matchingEndpoint = null;

            foreach (var name in endpointNames)
            {
                matchingEndpoint = endpoints.FirstOrDefault(e => string.Equals(e.EndpointName, name, StringComparisons.EndpointAnnotationName));
                if (matchingEndpoint is not null)
                {
                    if (!s_httpSchemes.Contains(matchingEndpoint.Scheme, StringComparers.EndpointAnnotationUriScheme))
                    {
                        throw new DistributedApplicationException($"Could not create {errorDisplayNoun} for resource '{builder.Resource.Name}' as the endpoint with name '{matchingEndpoint.EndpointName}' and scheme '{matchingEndpoint.Scheme}' is not an HTTP endpoint.");
                    }
                    return matchingEndpoint;
                }
            }

            // No endpoint found with the specified names
            var endpointNamesString = string.Join(", ", endpointNames);
            throw new DistributedApplicationException($"Could not create {errorDisplayNoun} for resource '{builder.Resource.Name}' as no endpoint was found matching one of the specified names: {endpointNamesString}");
        };

    private static Func<EndpointReference> DefaultEndpointSelector<TResource>(IResourceBuilder<TResource> builder)
        where TResource : IResourceWithEndpoints
        => () =>
        {
            // Use the first HTTP endpoint (preferring HTTPS over HTTP), otherwise throw an exception if no endpoint is found.
            var endpoints = builder.Resource.GetEndpoints();
            EndpointReference? matchingEndpoint = null;

            foreach (var scheme in s_httpSchemes)
            {
                matchingEndpoint = endpoints.FirstOrDefault(e => string.Equals(e.EndpointName, scheme, StringComparisons.EndpointAnnotationUriScheme));
                if (matchingEndpoint is not null)
                {
                    return matchingEndpoint;
                }
            }

            throw new DistributedApplicationException($"Could not create HTTP command for resource '{builder.Resource.Name}' as it has no HTTP endpoints.");
        };

    /// <summary>
    /// Adds a <see cref="ResourceRelationshipAnnotation"/> to the resource annotations to add a relationship.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="resource">The resource that the relationship is to.</param>
    /// <param name="type">The relationship type.</param>
    /// <returns>A resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The <c>WithRelationship</c> method is used to add relationships to the resource. Relationships are used to link
    /// resources together in UI. The <paramref name="type"/> indicates information about the relationship type.
    /// </para>
    /// <example>
    /// This example shows adding a relationship between two resources.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var backend = builder.AddProject&lt;Projects.Backend&gt;("backend");
    /// var manager = builder.AddProject&lt;Projects.Manager&gt;("manager")
    ///                      .WithRelationship(backend.Resource, "Manager");
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithRelationship<T>(
        this IResourceBuilder<T> builder,
        IResource resource,
        string type) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(type);

        return builder.WithAnnotation(new ResourceRelationshipAnnotation(resource, type));
    }

    /// <summary>
    /// Adds a <see cref="ResourceRelationshipAnnotation"/> to the resource annotations to add a reference to another resource.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="resource">The resource that the relationship is to.</param>
    /// <returns>A resource builder.</returns>
    public static IResourceBuilder<T> WithReferenceRelationship<T>(
        this IResourceBuilder<T> builder,
        IResource resource) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);

        return builder.WithAnnotation(new ResourceRelationshipAnnotation(resource, KnownRelationshipTypes.Reference));
    }

    /// <summary>
    /// Walks the reference expression and adds <see cref="ResourceRelationshipAnnotation"/>s for all resources found in the expression.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="expression">The reference expression.</param>
    /// <returns>A resource builder.</returns>
    public static IResourceBuilder<T> WithReferenceRelationship<T>(
        this IResourceBuilder<T> builder,
        ReferenceExpression expression) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(expression);

        WalkAndLinkResourceReferences(builder, expression.ValueProviders);

        return builder;
    }

    private static void WalkAndLinkResourceReferences<T>(IResourceBuilder<T> builder, IEnumerable<object> values)
        where T : IResource
    {
        var processed = new HashSet<object>();

        void AddReference(IResource resource)
        {
            builder.WithReferenceRelationship(resource);
        }

        void Walk(object value)
        {
            if (!processed.Add(value))
            {
                return;
            }

            if (value is IResource resource)
            {
                AddReference(resource);
            }
            else if (value is IResourceBuilder<IResource> resourceBuilder)
            {
                AddReference(resourceBuilder.Resource);
            }
            else if (value is IValueWithReferences valueWithReferences)
            {
                foreach (var reference in valueWithReferences.References)
                {
                    Walk(reference);
                }
            }
        }

        foreach (var value in values)
        {
            Walk(value);
        }
    }

    /// <summary>
    /// Adds a <see cref="ResourceRelationshipAnnotation"/> to the resource annotations to add a reference to another resource.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="resourceBuilder">The resource builder that the relationship is to.</param>
    /// <returns>A resource builder.</returns>
    public static IResourceBuilder<T> WithReferenceRelationship<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<IResource> resourceBuilder) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resourceBuilder);

        return builder.WithAnnotation(new ResourceRelationshipAnnotation(resourceBuilder.Resource, KnownRelationshipTypes.Reference));
    }

    /// <summary>
    /// Adds a <see cref="ResourceRelationshipAnnotation"/> to the resource annotations to add a parent-child relationship.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="parent">The parent of <paramref name="builder"/>.</param>
    /// <returns>A resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The <c>WithParentRelationship</c> method is used to add parent relationships to the resource. Relationships are used to link
    /// resources together in UI.
    /// </para>
    /// <example>
    /// This example shows adding a relationship between two resources.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var backend = builder.AddProject&lt;Projects.Backend&gt;("backend");
    ///
    /// var frontend = builder.AddProject&lt;Projects.Manager&gt;("frontend")
    ///                      .WithParentRelationship(backend);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithParentRelationship<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<IResource> parent) where T : IResource
    {
        return builder.WithParentRelationship(parent.Resource);
    }

    /// <summary>
    /// Adds a <see cref="ResourceRelationshipAnnotation"/> to the resource annotations to add a parent-child relationship.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="parent">The parent of <paramref name="builder"/>.</param>
    /// <returns>A resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The <c>WithParentRelationship</c> method is used to add parent relationships to the resource. Relationships are used to link
    /// resources together in UI.
    /// </para>
    /// <example>
    /// This example shows adding a relationship between two resources.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var backend = builder.AddProject&lt;Projects.Backend&gt;("backend");
    ///
    /// var frontend = builder.AddProject&lt;Projects.Manager&gt;("frontend")
    ///                      .WithParentRelationship(backend.Resource);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithParentRelationship<T>(
        this IResourceBuilder<T> builder,
        IResource parent) where T : IResource
    {
        return builder.WithRelationship(parent, KnownRelationshipTypes.Parent);
    }

    /// <summary>
    /// Adds a <see cref="ResourceRelationshipAnnotation"/> to the resource annotations to add a parent-child relationship.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="child">The child of <paramref name="builder"/>.</param>
    /// <returns>A resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The <c>WithChildRelationship</c> method is used to add child relationships to the resource. Relationships are used to link
    /// resources together in UI.
    /// </para>
    /// <example>
    /// This example shows adding a relationship between two resources.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var parameter = builder.AddParameter("parameter");
    ///
    /// var backend = builder.AddProject&lt;Projects.Backend&gt;("backend");
    ///                      .WithChildRelationship(parameter);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithChildRelationship<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<IResource> child) where T : IResource
    {
        child.WithRelationship(builder.Resource, KnownRelationshipTypes.Parent);
        return builder;
    }

    /// <summary>
    /// Adds a <see cref="ResourceRelationshipAnnotation"/> to the resource annotations to add a parent-child relationship.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="child">The child of <paramref name="builder"/>.</param>
    /// <returns>A resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The <c>WithChildRelationship</c> method is used to add child relationships to the resource. Relationships are used to link
    /// resources together in UI.
    /// </para>
    /// <example>
    /// This example shows adding a relationship between two resources.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var parameter = builder.AddParameter("parameter");
    ///
    /// var backend = builder.AddProject&lt;Projects.Backend&gt;("backend");
    ///                     .WithChildRelationship(parameter.Resource);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithChildRelationship<T>(
         this IResourceBuilder<T> builder,
         IResource child) where T : IResource
    {
        var childBuilder = builder.ApplicationBuilder.CreateResourceBuilder(child);
        return builder.WithChildRelationship(childBuilder);
    }

    /// <summary>
    /// Specifies the icon to use when displaying the resource in the dashboard.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="iconName">The name of the FluentUI icon to use. See https://aka.ms/fluentui-system-icons for available icons.</param>
    /// <param name="iconVariant">The variant of the icon (Regular or Filled). Defaults to Filled.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method allows you to specify a custom FluentUI icon that will be displayed for the resource in the dashboard.
    /// If no custom icon is specified, the dashboard will use default icons based on the resource type.
    /// </para>
    /// <example>
    /// Set a Redis resource to use the Database icon:
    /// <code lang="C#">
    /// var redis = builder.AddContainer("redis", "redis:latest")
    ///     .WithIconName("Database");
    /// </code>
    /// </example>
    /// <example>
    /// Set a custom service to use a specific icon with Regular variant:
    /// <code lang="C#">
    /// var service = builder.AddProject&lt;Projects.MyService&gt;("service")
    ///     .WithIconName("CloudArrowUp", IconVariant.Regular);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithIconName<T>(this IResourceBuilder<T> builder, string iconName, IconVariant iconVariant = IconVariant.Filled) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconName);

        return builder.WithAnnotation(new ResourceIconAnnotation(iconName, iconVariant));
    }

    /// <summary>
    /// Configures the compute environment for the compute resource.
    /// </summary>
    /// <param name="builder">The compute resource builder.</param>
    /// <param name="computeEnvironmentResource">The compute environment resource to associate with the compute resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method allows associating a specific compute environment with the compute resource.
    /// </remarks>
    public static IResourceBuilder<T> WithComputeEnvironment<T>(this IResourceBuilder<T> builder, IResourceBuilder<IComputeEnvironmentResource> computeEnvironmentResource)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(computeEnvironmentResource);

        builder.WithAnnotation(new ComputeEnvironmentAnnotation(computeEnvironmentResource.Resource));
        return builder;
    }

    /// <summary>
    /// Adds support for debugging the resource in VS Code when running in an extension host.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="launchConfigurationProducer">Launch configuration producer for the resource.</param>
    /// <param name="launchConfigurationType">The type of the resource.</param>
    /// <param name="argsCallback">Optional callback to add or modify command line arguments when running in an extension host. Useful if the entrypoint is usually provided as an argument to the resource executable.</param>
    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithDebugSupport<T, TLaunchConfiguration>(this IResourceBuilder<T> builder, Func<string, TLaunchConfiguration> launchConfigurationProducer, string launchConfigurationType, Action<CommandLineArgsCallbackContext>? argsCallback = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(launchConfigurationProducer);

        if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        if (builder is IResourceBuilder<IResourceWithArgs> resourceWithArgs)
        {
            resourceWithArgs.WithArgs(async ctx =>
            {
                var config = ctx.ExecutionContext.ServiceProvider.GetRequiredService<IConfiguration>();
                if (resourceWithArgs.SupportsDebugging(config) && argsCallback is not null)
                {
                    argsCallback(ctx);
                }
            });
        }

        return builder.WithAnnotation(SupportsDebuggingAnnotation.Create(launchConfigurationType, launchConfigurationProducer));
    }

    /// <summary>
    /// Adds a HTTP probe to the resource.
    /// </summary>
    /// <typeparam name="T">Type of resource.</typeparam>
    /// <param name="builder">Resource builder.</param>
    /// <param name="type">Type of the probe.</param>
    /// <param name="path">The path to be used.</param>
    /// <param name="initialDelaySeconds">The initial delay before calling the probe endpoint for the first time.</param>
    /// <param name="periodSeconds">The period between each probe.</param>
    /// <param name="timeoutSeconds">Number of seconds after which the probe times out.</param>
    /// <param name="failureThreshold">Number of failures in a row before considers that the overall check has failed.</param>
    /// <param name="successThreshold">Minimum consecutive successes for the probe to be considered successful after having failed.</param>
    /// <param name="endpointName">The name of the endpoint to be used for the probe.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method allows you to specify a probe and implicit adds an http health check to the resource based on probe parameters.
    /// </para>
    /// <example>
    /// For example add a probe to a resource in this way:
    /// <code lang="C#">
    /// var service = builder.AddProject&lt;Projects.MyService&gt;("service")
    ///     .WithHttpProbe(ProbeType.Liveness, "/health");
    /// </code>
    /// Is the same of writing:
    /// <code lang="C#">
    /// var service = builder.AddProject&lt;Projects.MyService&gt;("service")
    ///     .WithHttpProbe(ProbeType.Liveness, "/health")
    ///     .WithHttpHealthCheck("/health");
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREPROBES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithHttpProbe<T>(this IResourceBuilder<T> builder, ProbeType type, string? path = null, int? initialDelaySeconds = null, int? periodSeconds = null, int? timeoutSeconds = null, int? failureThreshold = null, int? successThreshold = null, string? endpointName = null)
        where T : IResourceWithEndpoints, IResourceWithProbes
    {
        ArgumentNullException.ThrowIfNull(builder);

        var endpointSelector = endpointName is not null
            ? NamedEndpointSelector(builder, [endpointName], "HTTP probe")
            : NamedEndpointSelector(builder, s_httpSchemes, "HTTP probe");

        return builder.WithHttpProbe(type, endpointSelector, path, initialDelaySeconds, periodSeconds, timeoutSeconds, failureThreshold, successThreshold);
    }

    /// <summary>
    /// Adds a HTTP probe to the resource.
    /// </summary>
    /// <typeparam name="T">Type of resource.</typeparam>
    /// <param name="builder">Resource builder.</param>
    /// <param name="type">Type of the probe.</param>
    /// <param name="endpointSelector">The selector used to get endpoint reference.</param>
    /// <param name="path">The path to be used.</param>
    /// <param name="initialDelaySeconds">The initial delay before calling the probe endpoint for the first time.</param>
    /// <param name="periodSeconds">The period between each probe.</param>
    /// <param name="timeoutSeconds">Number of seconds after which the probe times out.</param>
    /// <param name="failureThreshold">Number of failures in a row before considers that the overall check has failed.</param>
    /// <param name="successThreshold">Minimum consecutive successes for the probe to be considered successful after having failed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method allows you to specify a probe and implicit adds an http health check to the resource based on probe parameters.
    /// </para>
    /// <example>
    /// For example add a probe to a resource in this way:
    /// <code lang="C#">
    /// var service = builder.AddProject&lt;Projects.MyService&gt;("service")
    ///     .WithHttpProbe(ProbeType.Liveness, "/health");
    /// </code>
    /// Is the same of writing:
    /// <code lang="C#">
    /// var service = builder.AddProject&lt;Projects.MyService&gt;("service")
    ///     .WithHttpProbe(ProbeType.Liveness, "/health")
    ///     .WithHttpHealthCheck("/health");
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREPROBES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithHttpProbe<T>(this IResourceBuilder<T> builder, ProbeType type, Func<EndpointReference>? endpointSelector, string? path = null, int? initialDelaySeconds = null, int? periodSeconds = null, int? timeoutSeconds = null, int? failureThreshold = null, int? successThreshold = null)
        where T : IResourceWithEndpoints, IResourceWithProbes
    {
        endpointSelector ??= DefaultEndpointSelector(builder);

        var endpoint = endpointSelector() ?? throw new DistributedApplicationException($"Could not create HTTP probe for resource '{builder.Resource.Name}' as the endpoint selector returned null.");
        var endpointProbeAnnotation = new EndpointProbeAnnotation
        {
            Type = type,
            EndpointReference = endpoint,
            Path = path ?? "/",
            InitialDelaySeconds = initialDelaySeconds ?? 5,
            PeriodSeconds = periodSeconds ?? 5,
            TimeoutSeconds = timeoutSeconds ?? 1,
            FailureThreshold = failureThreshold ?? 3,
            SuccessThreshold = successThreshold ?? 1,
        };

        return builder
            .WithProbe(endpointProbeAnnotation)
            .WithHttpHealthCheck(endpointSelector, path);
    }

    /// <summary>
    /// Adds a probe to the resource to check its health state.
    /// </summary>
    /// <typeparam name="T">Type of resource.</typeparam>
    /// <param name="builder">Resource builder.</param>
    /// <param name="probeAnnotation">Probe annotation to add to resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [Experimental("ASPIREPROBES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    private static IResourceBuilder<T> WithProbe<T>(this IResourceBuilder<T> builder, ProbeAnnotation probeAnnotation) where T : IResourceWithProbes
    {
        // Replace existing annotation with the same type
        if (builder.Resource.Annotations.OfType<ProbeAnnotation>().SingleOrDefault(a => a.Type == probeAnnotation.Type) is { } existingAnnotation)
        {
            builder.Resource.Annotations.Remove(existingAnnotation);
        }

        return builder.WithAnnotation(probeAnnotation);
    }

    /// <summary>
    /// Exclude the resource from MCP operations using the Aspire MCP server. The resource is excluded from results that return resources, console logs and telemetry.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> ExcludeFromMcp<T>(this IResourceBuilder<T> builder) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new ExcludeFromMcpAnnotation());
    }

    /// <summary>
    /// Adds a callback to configure container image push options for the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback to configure push options.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="callback"/> is <c>null</c>.</exception>
    /// <remarks>
    /// This method allows customization of how container images are named and tagged when pushed to a registry.
    /// The callback receives a <see cref="ContainerImagePushOptionsCallbackContext"/> that provides access to the resource
    /// and the <see cref="ContainerImagePushOptions"/> that can be modified.
    /// Multiple callbacks can be registered on the same resource, and they will be invoked in the order they were added.
    /// </remarks>
    /// <example>
    /// Configure a custom image name and tag for a container resource:
    /// <code>
    /// var container = builder.AddContainer("myapp", "myapp:latest")
    ///     .WithImagePushOptions(context =>
    ///     {
    ///         context.Options.RemoteImageName = "myorg/myapp";
    ///         context.Options.RemoteImageTag = "v1.0.0";
    ///     });
    /// </code>
    /// </example>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
    public static IResourceBuilder<T> WithImagePushOptions<T>(
        this IResourceBuilder<T> builder,
        Action<ContainerImagePushOptionsCallbackContext> callback)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new ContainerImagePushOptionsCallbackAnnotation(callback));
    }

    /// <summary>
    /// Adds an asynchronous callback to configure container image push options for the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The asynchronous callback to configure push options.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="callback"/> is <c>null</c>.</exception>
    /// <remarks>
    /// This method allows customization of how container images are named and tagged when pushed to a registry using an asynchronous callback.
    /// Use this overload when the callback needs to perform asynchronous operations such as retrieving configuration values from external sources.
    /// The callback receives a <see cref="ContainerImagePushOptionsCallbackContext"/> that provides access to the resource
    /// and the <see cref="ContainerImagePushOptions"/> that can be modified.
    /// Multiple callbacks can be registered on the same resource, and they will be invoked in the order they were added.
    /// </remarks>
    /// <example>
    /// Configure image options asynchronously by retrieving values from configuration:
    /// <code>
    /// var container = builder.AddContainer("myapp", "myapp:latest")
    ///     .WithImagePushOptions(async context =>
    ///     {
    ///         var config = await GetConfigurationAsync();
    ///         context.Options.RemoteImageName = config.ImageName;
    ///         context.Options.RemoteImageTag = config.ImageTag;
    ///     });
    /// </code>
    /// </example>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
    public static IResourceBuilder<T> WithImagePushOptions<T>(
        this IResourceBuilder<T> builder,
        Func<ContainerImagePushOptionsCallbackContext, Task> callback)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new ContainerImagePushOptionsCallbackAnnotation(callback));
    }

    /// <summary>
    /// Sets the remote image name (without registry endpoint or tag) for container push operations.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="remoteImageName">The remote image name (e.g., "myapp" or "myorg/myapp").</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="remoteImageName"/> is <c>null</c>.</exception>
    /// <remarks>
    /// This is a convenience method that registers a callback to set the <see cref="ContainerImagePushOptions.RemoteImageName"/> property.
    /// The remote image name should not include the registry endpoint or tag. Those are managed separately.
    /// This method can be combined with <see cref="WithRemoteImageTag{T}"/> to fully customize the image reference.
    /// </remarks>
    /// <example>
    /// Set a custom remote image name for a container:
    /// <code>
    /// var container = builder.AddContainer("myapp", "myapp:latest")
    ///     .WithRemoteImageName("myorg/myapp");
    /// </code>
    /// </example>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
    public static IResourceBuilder<T> WithRemoteImageName<T>(
        this IResourceBuilder<T> builder,
        string remoteImageName)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(remoteImageName);

        return builder.WithImagePushOptions(context =>
        {
            context.Options.RemoteImageName = remoteImageName;
        });
    }

    /// <summary>
    /// Sets the remote image tag for container push operations.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="remoteImageTag">The remote image tag (e.g., "latest", "v1.0.0").</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="remoteImageTag"/> is <c>null</c>.</exception>
    /// <remarks>
    /// This is a convenience method that registers a callback to set the <see cref="ContainerImagePushOptions.RemoteImageTag"/> property.
    /// The tag can be any valid container image tag such as version numbers, environment names, or deployment identifiers.
    /// This method can be combined with <see cref="WithRemoteImageName{T}"/> to fully customize the image reference.
    /// </remarks>
    /// <example>
    /// Set a specific version tag for a container:
    /// <code>
    /// var container = builder.AddContainer("myapp", "myapp:latest")
    ///     .WithRemoteImageTag("v1.0.0");
    /// </code>
    /// </example>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
    public static IResourceBuilder<T> WithRemoteImageTag<T>(
        this IResourceBuilder<T> builder,
        string remoteImageTag)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(remoteImageTag);

        return builder.WithImagePushOptions(context =>
        {
            context.Options.RemoteImageTag = remoteImageTag;
        });
    }
}
