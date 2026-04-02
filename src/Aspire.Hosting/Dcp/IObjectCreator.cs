// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Empty creation context for object creators that do not need additional context.
/// </summary>
internal sealed class EmptyCreationContext
{
    internal static readonly EmptyCreationContext s_instance = new();
}

/// <summary>
/// Defines the contract for components that prepare and create DCP resources corresponding to Aspire model resources.
/// </summary>
/// <typeparam name="TDcpResource">The type of DCP custom resource this creator handles.</typeparam>
/// <typeparam name="TContext">The type of context passed during creation (e.g. ContainerCreationContext).</typeparam>
internal interface IObjectCreator<TDcpResource, TContext>
    where TDcpResource : CustomResource, IKubernetesStaticMetadata
{
    /// <summary>
    /// Prepares DCP resource objects based on the Aspire application model.
    /// Returns the set of prepared resources that should be created.
    /// </summary>
    IEnumerable<RenderedModelResource<TDcpResource>> PrepareObjects();

    /// <summary>
    /// Determines whether the resource is ready to be created immediately.
    /// Returns false if the resource uses explicit startup and should not be created yet.
    /// Implementations may perform side effects (e.g. setting Spec.Start = false for delayed containers).
    /// </summary>
    bool IsReadyToCreate(RenderedModelResource<TDcpResource> resource, TContext context);

    /// <summary>
    /// Creates the DCP resource object(s) for the given Aspire model resource.
    /// This method should handle all type-specific creation logic (building specs, configuration,
    /// tunnel dependencies, etc.) and call <paramref name="factory"/> to submit the objects.
    /// </summary>
    Task CreateObjectAsync(RenderedModelResource<TDcpResource> resource, TContext context, ILogger logger, IDcpObjectFactory factory, CancellationToken cancellationToken);
}
