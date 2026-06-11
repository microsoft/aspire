// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents Azure Container Registry information for deployment targets.
/// </summary>
[Obsolete("Use IAzureContainerRegistryResource instead and access container registry through IAzureComputeEnvironmentResource.ContainerRegistry property.")]
public interface IAzureContainerRegistry : IContainerRegistry
{
    /// <summary>
    /// Gets the managed identity ID associated with the container registry.
    /// </summary>
    ReferenceExpression ManagedIdentityId { get; }
}
