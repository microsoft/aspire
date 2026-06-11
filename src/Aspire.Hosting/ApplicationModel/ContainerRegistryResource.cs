// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a general-purpose container registry resource that can be used to reference external container registries
/// (e.g., Docker Hub, GitHub Container Registry, or private registries) in the application model.
/// </summary>
/// <remarks>
/// This resource implements <see cref="IContainerRegistry"/> and allows configuration using either
/// <see cref="ParameterResource"/> values or hard-coded strings, providing flexibility for scenarios
/// where registry configuration needs to be dynamically provided or statically defined.
/// Use <see cref="ContainerRegistryResourceBuilderExtensions.AddContainerRegistry(IDistributedApplicationBuilder, string, IResourceBuilder{ParameterResource}, IResourceBuilder{ParameterResource}?)"/>
/// to add a container registry with parameterized values, or
/// <see cref="ContainerRegistryResourceBuilderExtensions.AddContainerRegistry(IDistributedApplicationBuilder, string, string, string?)"/>
/// to add a container registry with literal values.
/// </remarks>
/// <example>
/// Add a container registry with parameterized values:
/// <code>
/// var endpointParameter = builder.AddParameter("registry-endpoint");
/// var repositoryParameter = builder.AddParameter("registry-repo");
/// var registry = builder.AddContainerRegistry("my-registry", endpointParameter, repositoryParameter);
/// </code>
/// </example>
/// <example>
/// Add a container registry with literal values:
/// <code>
/// var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myusername");
/// </code>
/// </example>
[Experimental("ASPIRECOMPUTE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class ContainerRegistryResource : Resource, IContainerRegistry
{
    private readonly ReferenceExpression _registryName;
    private readonly ReferenceExpression _endpoint;
    private readonly ReferenceExpression? _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerRegistryResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="endpoint">The endpoint URL or hostname of the container registry.</param>
    /// <param name="repository">The optional repository path within the container registry.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpoint"/> is <see langword="null"/>.</exception>
    public ContainerRegistryResource(string name, ReferenceExpression endpoint, ReferenceExpression? repository = null)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        _registryName = ReferenceExpression.Create($"{name}");
        _endpoint = endpoint;
        _repository = repository;
    }

    /// <inheritdoc />
    ReferenceExpression IContainerRegistry.Name => _registryName;

    /// <inheritdoc />
    ReferenceExpression IContainerRegistry.Endpoint => _endpoint;

    /// <inheritdoc />
    ReferenceExpression? IContainerRegistry.Repository => _repository;
}
