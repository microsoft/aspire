// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Advertises a compute environment's effective registry as a default destination for standalone image artifacts.
/// </summary>
/// <remarks>
/// Providers attach this annotation to their <see cref="IComputeEnvironmentResource"/>. Registry selection
/// is evaluated after environment preparation, including registry overrides and removal of unused generated registries.
/// The registry must also be a resource in the application model.
/// Unlike <see cref="RegistryTargetAnnotation"/>, this annotation does not select registries for compute resources.
/// Explicit image associations take precedence over these defaults; distinct defaults require an explicit association.
/// </remarks>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ContainerImageRegistryTargetAnnotation : IResourceAnnotation
{
    private readonly Func<IContainerRegistry?> _registryProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerImageRegistryTargetAnnotation"/> class.
    /// </summary>
    /// <param name="registry">The environment's effective registry.</param>
    /// <exception cref="ArgumentNullException">The registry is <see langword="null"/>.</exception>
    public ContainerImageRegistryTargetAnnotation(IContainerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registryProvider = () => registry;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerImageRegistryTargetAnnotation"/> class
    /// using the environment's deferred registry selection.
    /// </summary>
    /// <param name="registryProvider">
    /// A callback returning the effective registry resource, or <see langword="null"/> when none is configured.
    /// The callback must not provision resources or resolve registry parameter or output values.
    /// </param>
    /// <exception cref="ArgumentNullException">The callback is <see langword="null"/>.</exception>
    public ContainerImageRegistryTargetAnnotation(Func<IContainerRegistry?> registryProvider)
    {
        ArgumentNullException.ThrowIfNull(registryProvider);
        _registryProvider = registryProvider;
    }

    /// <summary>
    /// Gets the environment's effective registry, or <see langword="null"/> when none is configured.
    /// </summary>
    public IContainerRegistry? Registry => _registryProvider();
}
