// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding container registry resources to the distributed application.
/// </summary>
public static class ContainerRegistryResourceBuilderExtensions
{
    /// <summary>
    /// Adds a container registry resource to the application model with parameterized values.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the container registry resource.</param>
    /// <param name="endpoint">An <see cref="IResourceBuilder{ParameterResource}"/> containing the registry endpoint URL or hostname.</param>
    /// <param name="repository">An optional <see cref="IResourceBuilder{ParameterResource}"/> containing the repository path within the registry.</param>
    /// <returns>An <see cref="IResourceBuilder{ContainerRegistryResource}"/> for the container registry resource.</returns>
    /// <remarks>
    /// Use this method when the registry endpoint and repository values need to be provided dynamically
    /// via configuration or user input. The resource is only added to the application model in publish mode;
    /// in run mode, a resource builder is created without adding the resource to the model.
    /// </remarks>
    /// <example>
    /// Add a container registry with parameterized values:
    /// <code>
    /// var endpointParameter = builder.AddParameter("registry-endpoint");
    /// var repositoryParameter = builder.AddParameter("registry-repo");
    /// var registry = builder.AddContainerRegistry("my-registry", endpointParameter, repositoryParameter);
    /// </code>
    /// </example>
    [Experimental("ASPIRECOMPUTE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal addContainerRegistry dispatcher export.")]
    public static IResourceBuilder<ContainerRegistryResource> AddContainerRegistry(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource> endpoint,
        IResourceBuilder<ParameterResource>? repository = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(endpoint);

        var endpointExpression = ReferenceExpression.Create($"{endpoint.Resource}");
        var repositoryExpression = repository is not null
            ? ReferenceExpression.Create($"{repository.Resource}")
            : null;

        var resource = new ContainerRegistryResource(name, endpointExpression, repositoryExpression);

        var resourceBuilder = builder.ExecutionContext.IsRunMode
            ? builder.CreateResourceBuilder(resource)
            : builder.AddResource(resource);

        SubscribeToAddRegistryTargetAnnotations(builder, resource);

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a container registry resource to the application model with literal string values.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the container registry resource.</param>
    /// <param name="endpoint">The registry endpoint URL or hostname (e.g., "docker.io", "ghcr.io").</param>
    /// <param name="repository">The optional repository path within the registry (e.g., "myusername" for Docker Hub, "owner/repo" for GHCR).</param>
    /// <returns>An <see cref="IResourceBuilder{ContainerRegistryResource}"/> for the container registry resource.</returns>
    /// <remarks>
    /// Use this method when the registry endpoint and repository values are known at design time
    /// and do not need to be parameterized. The resource is only added to the application model in publish mode;
    /// in run mode, a resource builder is created without adding the resource to the model.
    /// </remarks>
    /// <example>
    /// Add a Docker Hub container registry:
    /// <code>
    /// var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myusername");
    /// </code>
    /// </example>
    /// <example>
    /// Add a GitHub Container Registry:
    /// <code>
    /// var registry = builder.AddContainerRegistry("ghcr", "ghcr.io", "owner/repo");
    /// </code>
    /// </example>
    [Experimental("ASPIRECOMPUTE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal addContainerRegistry dispatcher export.")]
    public static IResourceBuilder<ContainerRegistryResource> AddContainerRegistry(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string endpoint,
        string? repository = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(endpoint);

        var endpointExpression = ReferenceExpression.Create($"{endpoint}");
        var repositoryExpression = repository is not null
            ? ReferenceExpression.Create($"{repository}")
            : null;

        var resource = new ContainerRegistryResource(name, endpointExpression, repositoryExpression);

        var resourceBuilder = builder.ExecutionContext.IsRunMode
            ? builder.CreateResourceBuilder(resource)
            : builder.AddResource(resource);

        SubscribeToAddRegistryTargetAnnotations(builder, resource);

        return resourceBuilder;
    }

    [Experimental("ASPIRECOMPUTE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("addContainerRegistry", Description = "Adds a container registry resource")]
    internal static IResourceBuilder<ContainerRegistryResource> AddContainerRegistryForPolyglot(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object endpoint,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object? repository = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(endpoint);

        return (endpoint, repository) switch
        {
            (string endpointValue, null) => builder.AddContainerRegistry(name, endpointValue),
            (string endpointValue, string repositoryValue) => builder.AddContainerRegistry(name, endpointValue, repositoryValue),
            (IResourceBuilder<ParameterResource> endpointParameter, null) => builder.AddContainerRegistry(name, endpointParameter),
            (IResourceBuilder<ParameterResource> endpointParameter, IResourceBuilder<ParameterResource> repositoryParameter)
                => builder.AddContainerRegistry(name, endpointParameter, repositoryParameter),
            _ => throw new ArgumentException(
                "Endpoint and repository must both be strings or parameter resource builders.",
                nameof(repository))
        };
    }

    /// <summary>
    /// Subscribes to BeforeStartEvent to add RegistryTargetAnnotation to all resources in the model.
    /// </summary>
    private static void SubscribeToAddRegistryTargetAnnotations(IDistributedApplicationBuilder builder, ContainerRegistryResource registry)
    {
        builder.OnBeforeStart((beforeStartEvent, cancellationToken) =>
        {
            foreach (var resource in beforeStartEvent.Model.Resources)
            {
                // Add a RegistryTargetAnnotation to indicate this registry is available as a default target
                resource.Annotations.Add(new RegistryTargetAnnotation(registry));
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Configures the resource to use the specified container registry for container image operations.
    /// </summary>
    /// <typeparam name="TDestination">The type of the destination resource.</typeparam>
    /// <typeparam name="TContainerRegistry">The type of the container registry resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="registry">The container registry resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// This method adds a <see cref="ContainerRegistryReferenceAnnotation"/> to the resource,
    /// indicating that the resource should use the specified container registry for container image operations.
    /// </remarks>
    /// <example>
    /// Configure a project to use a container registry:
    /// <code>
    /// var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myusername");
    /// var project = builder.AddProject&lt;MyProject&gt;("myproject")
    ///     .WithContainerRegistry(registry);
    /// </code>
    /// </example>
    [Experimental("ASPIRECOMPUTE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Configures a resource to use a container registry")]
    public static IResourceBuilder<TDestination> WithContainerRegistry<TDestination, TContainerRegistry>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<TContainerRegistry> registry)
        where TDestination : IResource
        where TContainerRegistry : IResource, IContainerRegistry
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registry);

        return builder.WithAnnotation(new ContainerRegistryReferenceAnnotation(registry.Resource));
    }
}
